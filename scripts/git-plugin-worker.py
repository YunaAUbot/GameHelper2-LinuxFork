#!/usr/bin/env python3
"""Trusted source installer. Builds execute unrestricted code as the desktop user."""
import argparse, ctypes, errno, hashlib, json, os, pathlib, re, shutil, signal, subprocess, tempfile, time, urllib.parse, xml.etree.ElementTree as ET

class InstallError(Exception): pass

def host_references(app):
    """Use published managed assets, never self-contained framework/native DLLs."""
    manifest = app/'GameHelper.deps.json'
    if not manifest.exists():
        if any(app.glob('*.dll')): raise InstallError('Installed host dependency manifest is missing.')
        return []
    data = json.loads(manifest.read_text())
    target = data['targets'][data['runtimeTarget']['name']]
    names = set()
    for library, assets in target.items():
        if library.startswith('runtimepack.'): continue
        names.update(pathlib.PurePosixPath(p).name for p in assets.get('runtime', {}))
        names.update(pathlib.PurePosixPath(p).name for p, info in assets.get('runtimeTargets', {}).items()
                     if info.get('assetType') == 'runtime')
    return [app/name for name in sorted(names) if name.endswith('.dll') and (app/name).is_file()]

def is_configuration(relative):
    """Keep explicit config locations; shipped assets/code must follow the update."""
    if relative.parts[0].lower() in ('config', 'configs'): return True
    return (len(relative.parts) == 1
            and relative.stem.lower() in ('settings', 'config', 'preferences', 'options')
            and relative.suffix.lower() in ('.json', '.ini', '.cfg', '.toml', '.yaml', '.yml', '.xml'))

def safe_tree(path):
    path = pathlib.Path(path)
    for parent in (path, *path.parents):
        if parent.is_symlink(): raise InstallError('Symlink paths are not supported.')
    count = size = 0
    if path.exists():
        for base, dirs, files in os.walk(path, followlinks=False):
            for name in dirs + files:
                item = pathlib.Path(base)/name
                if item.is_symlink(): raise InstallError('Repository or installation contains a symlink.')
                if item.is_file():
                    count += 1; size += item.stat().st_size
                    if count > 30000 or size > 1024**3: raise InstallError('Installation exceeds 30,000 files or 1 GiB.')

def atomic(path, value):
    safe_tree(path.parent)
    temporary = path.with_suffix('.tmp')
    if temporary.is_symlink() or path.is_symlink(): raise InstallError('Unsafe state path.')
    temporary.write_text(json.dumps(value)); temporary.replace(path)

def rename_absent(source, target):
    """Linux atomic directory publication; never replace even an empty target."""
    libc = ctypes.CDLL(None, use_errno=True)
    rename = libc.renameat2
    rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
    rename.restype = ctypes.c_int
    if rename(-100, os.fsencode(source), -100, os.fsencode(target), 1) != 0:
        error = ctypes.get_errno()
        if error == errno.EEXIST: raise InstallError('Conflict: plugin directory appeared during installation; it will not be replaced.')
        raise InstallError('Atomic installation unavailable; completed build retained for next launch.')

class Worker:
    def __init__(self, app, allow_local=False):
        self.app=pathlib.Path(app).absolute(); safe_tree(self.app)
        self.home=self.app/'PluginSources'; self.home.mkdir(exist_ok=True)
        self.allow_local=allow_local; self.child=None
        self.sources=json.loads((self.home/'sources.json').read_text()) if (self.home/'sources.json').exists() else []
    def save(self): atomic(self.home/'sources.json', self.sources)
    def run(self, args, cwd=None, timeout=180):
        env=os.environ.copy(); env.update(GIT_TERMINAL_PROMPT='0', GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL='/dev/null', GIT_LFS_SKIP_SMUDGE='1')
        try:
            # Discard compiler/git output: arbitrary repositories can print credentials.
            self.child=subprocess.Popen(args,cwd=cwd,env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
            deadline=time.monotonic()+timeout
            while self.child.poll() is None:
                if time.monotonic()>deadline: raise subprocess.TimeoutExpired(args,timeout)
                if cwd: safe_tree(cwd)
                time.sleep(0.2)
            if self.child.returncode: raise InstallError('Git or build failed. Check repository access, SDK and project compatibility.')
        except InstallError:
            self.cancel(); raise
        except (OSError, subprocess.TimeoutExpired):
            self.cancel(); raise InstallError('Git/build unavailable or timed out (3 minute limit).') from None
        finally: self.child=None
    def cancel(self):
        if self.child and self.child.poll() is None:
            os.killpg(self.child.pid, signal.SIGKILL); self.child.wait()
    def check_target(self, name, identity):
        if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_.-]{0,79}',name): raise InstallError('Invalid plugin assembly name.')
        plugins=self.app/'Plugins'; safe_tree(plugins)
        if plugins.exists() and any(p.name.casefold()==name.casefold() and p.name!=name for p in plugins.iterdir()):
            raise InstallError('Conflict: a plugin with this name already exists (case-insensitive).')
        target=plugins/name; safe_tree(target)
        if target.exists():
            marker=target/'.git-source-owner.json'
            if not marker.is_file() or json.loads(marker.read_text()).get('id') != identity:
                raise InstallError('Conflict: plugin directory is bundled or belongs to another source. It will not be replaced.')
        return target
    def publish_first(self, source):
        """Publish only never-installed sources. Reload must never activate updates."""
        identity = source['id']
        if not re.fullmatch('[a-f0-9]{24}', identity): raise InstallError('Invalid source identity.')
        staged = self.home/'pending'/identity
        target = self.check_target(source['name'], identity)
        if (target.exists() or source.get('installedVersion')
                or (self.home/('backup-'+identity)).exists()
                or (self.home/('previous-'+identity)).exists()): return False
        if not staged.exists(): return False
        safe_tree(staged)
        if json.loads((staged/'.git-source-owner.json').read_text()) != {'id': identity, 'version': source['version']}:
            raise InstallError('Invalid staged ownership.')
        target.parent.mkdir(exist_ok=True)
        self.check_target(source['name'], identity)
        previous = dict(source)
        # Persist before exposing files to discovery. After publication no rollback
        # may remove a directory that Reload all plugins could already have loaded.
        source['installedVersion'] = source['version']
        source['status'] = 'Installed; use Reload all plugins (no restart required)'
        try:
            self.save()
            rename_absent(staged, target)
        except BaseException:
            source.clear(); source.update(previous)
            self.save()
            raise
        return True

    def install(self, url, trust):
        if trust is not True: raise InstallError('Explicit trust acceptance is required: build and plugin execute code.')
        parsed=urllib.parse.urlsplit(url)
        local=self.allow_local and pathlib.Path(url).is_absolute()
        if not local and (parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or not re.fullmatch(r'/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/?',parsed.path)):
            raise InstallError('Use a credential-free HTTPS repository URL (no query or fragment).')
        url=url.rstrip('/'); identity=hashlib.sha256(url.encode()).hexdigest()[:24]
        existing=next((s for s in self.sources if s['id']==identity),None)
        if len(self.sources)>=32 and not existing: raise InstallError('Maximum 32 sources.')
        with tempfile.TemporaryDirectory(prefix='build-',dir=self.home) as temp:
            temp=pathlib.Path(temp); repo=temp/'repo'
            self.run(['git','-c','protocol.file.allow='+('always' if local else 'never'),'clone','--depth','1','--no-tags','--',url,str(repo)], cwd=temp)
            safe_tree(repo)
            version=(repo/'.git/HEAD').read_text().strip()
            if version.startswith('ref: '): version=(repo/'.git'/version[5:]).read_text().strip()
            if existing and existing.get('version')==version:
                if self.publish_first(existing): return existing
                existing['status']='Installed; use Reload all plugins (no restart required)' if existing.get('installedVersion')==version else 'Staged update; restart to activate'
                self.save(); return existing
            projects=[p for p in repo.rglob('*.csproj') if not any(x.lower() in ('obj','bin') or 'test' in x.lower() for x in p.relative_to(repo).parts)]
            if len(projects)!=1: raise InstallError('Expected exactly one non-test .csproj; repository layout is ambiguous or unsupported.')
            project=projects[0]; tree=ET.parse(project); root=tree.getroot()
            name=root.findtext('./PropertyGroup/AssemblyName') or project.stem
            active_target = self.check_target(name,identity)
            if existing and existing['name']!=name: raise InstallError('Source changed its assembly name; refusing replacement.')
            # Adapt the two published source layouts to the installed host; no host rebuild.
            for group in root.findall('ItemGroup'):
                for reference in list(group):
                    if reference.tag=='ProjectReference' and reference.get('Include','').replace('\\','/').endswith('/GameHelper/GameHelper.csproj'):
                        group.remove(reference)
                        for dll in host_references(self.app):
                            ref=ET.SubElement(group,'Reference',Include=dll.stem)
                            ET.SubElement(ref,'HintPath').text=str(dll)
                            ET.SubElement(ref,'Private').text='false'
            for target in list(root.findall('Target')):
                if target.get('Name') in ('ValidateGameHelperHost','CopyFiles'): root.remove(target)
            tree.write(project)
            output=temp/'output'
            self.run([os.environ.get('DOTNET','dotnet'),'build',str(project),'-c','Release','-p:EnableWindowsTargeting=true','-p:SkipNativeGpuOverlayBuild=true','-p:CopyLocalLockFileAssemblies=true','-o',str(output)], cwd=temp)
            safe_tree(output)
            dll=output/(name+'.dll')
            if not dll.is_file() or dll.read_bytes()[:2]!=b'MZ': raise InstallError('Build produced no plugin assembly.')
            validator=pathlib.Path(__file__).parent/'plugin-assembly-check/PluginAssemblyCheck.csproj'
            self.run([os.environ.get('DOTNET','dotnet'),'run','--project',str(validator),'-c','Release','--',str(dll)], cwd=temp)
            # Host dependencies must resolve from the running application.
            for host in self.app.glob('*.dll'):
                bundled=output/host.name
                if bundled.exists(): bundled.unlink()
            if not dll.exists(): raise InstallError('Plugin name conflicts with a host assembly.')
            localization=project.parent/'Localization'
            if localization.is_dir() and not (output/'Localization').exists(): shutil.copytree(localization,output/'Localization')
            record={'id':identity,'url':url,'name':name,'version':version,'update':existing.get('update',True) if existing else True,'status':'Staged update; restart to activate'}
            if existing and existing.get('installedVersion'): record['installedVersion'] = existing['installedVersion']
            elif active_target.exists():
                record['installedVersion'] = json.loads((active_target/'.git-source-owner.json').read_text())['version']
            atomic(output/'.git-source-owner.json',{'id':identity,'version':version})
            pending=self.home/'pending'; pending.mkdir(exist_ok=True); safe_tree(pending)
            destination=pending/identity
            if destination.exists(): shutil.rmtree(destination)
            shutil.move(str(output),destination)
            self.sources=[s for s in self.sources if s['id']!=identity]+[record]; self.save()
            self.publish_first(record)
            return record
    def activate(self):
        for source in self.sources:
            identity=source['id']
            if not re.fullmatch('[a-f0-9]{24}',identity): raise InstallError('Invalid source identity.')
            staged=self.home/'pending'/identity; backup=self.home/('backup-'+identity)
            target=self.check_target(source['name'],identity); target.parent.mkdir(exist_ok=True)
            safe_tree(backup)
            if backup.exists():
                # An interrupted transaction is rolled back before discovery.
                owner=json.loads((backup/'.git-source-owner.json').read_text())
                if owner.get('id') != identity or not owner.get('version'): raise InstallError('Invalid backup ownership.')
                if target.exists(): shutil.rmtree(target)
                backup.rename(target)
            # Directory renames and sources.json are separate atomic operations.
            # Reconcile even without a backup: recovery may have stopped after
            # restoring the directory but before saving its installed version.
            installed=None
            if target.exists():
                installed=json.loads((target/'.git-source-owner.json').read_text())['version']
                if not installed: raise InstallError('Invalid installed version.')
            if source.get('installedVersion') != installed:
                if installed is None: source.pop('installedVersion',None)
                else: source['installedVersion']=installed
                source['status']='Recovered installation' if installed else 'Staged; restart to activate'
                self.save()
            if not staged.exists(): continue
            try:
                if self.publish_first(source): continue
                safe_tree(staged)
                if json.loads((staged/'.git-source-owner.json').read_text()) != {'id': identity, 'version': source['version']}: raise InstallError('Invalid staged ownership.')
                # Configuration wins over repository defaults. Do not resurrect obsolete assets.
                if target.exists():
                    for item in target.rglob('*'):
                        relative=item.relative_to(target); dest=staged/relative
                        if item.is_file() and is_configuration(relative):
                            dest.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(item,dest)
                    if backup.exists(): shutil.rmtree(backup)
                    target.rename(backup)
                previous=dict(source)
                try:
                    staged.rename(target)
                    source['status']='Installed'; source['installedVersion']=source['version']; self.save()
                except BaseException:
                    if target.exists(): target.rename(staged)
                    if backup.exists(): backup.rename(target)
                    source.clear(); source.update(previous)
                    raise
                if backup.exists():
                    previous_copy=self.home/('previous-'+identity)
                    safe_tree(previous_copy)
                    if previous_copy.exists(): shutil.rmtree(previous_copy)
                    backup.rename(previous_copy)
            except Exception:
                # A backup remains a rollback journal until finalization removes
                # its name. Never let the host edit configuration while a later
                # startup could still discard that active directory.
                source['status']='Activation failed; launch refused until recovery completes'; self.save()
                raise InstallError('Activation failed; refusing launch until recovery completes.') from None
    def serve(self):
        # Migrate old pending first installs even when automatic updates are off.
        # Backups/installed versions always remain startup-only recovery work.
        for source in self.sources:
            try: self.publish_first(source)
            except Exception: source['status']='Installation unavailable; completed build retained'; self.save()
        for source in list(self.sources):
            if source.get('update',True):
                try: self.install(source['url'],True)
                except Exception: source['status']='Update unavailable; previous installation retained'; self.save()
        while True:
            atomic(self.home/'heartbeat.json',{'time':time.time()})
            request=self.home/'request.json'
            if request.exists():
                try:
                    safe_tree(request)
                    if request.stat().st_size > 8192: raise InstallError('Request exceeds size limit.')
                    data=json.loads(request.read_text()); request.unlink()
                    if data.get('action')=='toggle':
                        for source in self.sources:
                            if source['id']==data['id']: source['update']=bool(data['update'])
                        self.save()
                        result = 'Update preference saved.'
                    else: result = self.install(data['url'],data.get('trust'))['status']
                    atomic(self.home/'result.json',{'status':result})
                except Exception as error:
                    atomic(self.home/'result.json',{'status':str(error) if isinstance(error,InstallError) else 'Installation failed; previous copy retained.'})
            time.sleep(0.5)

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('mode',choices=['activate','serve']); parser.add_argument('app'); args=parser.parse_args()
    worker=Worker(args.app)
    def stop(*_): worker.cancel(); raise SystemExit(0)
    signal.signal(signal.SIGTERM,stop); signal.signal(signal.SIGINT,stop)
    if args.mode=='activate': worker.activate()
    else: worker.serve()
if __name__=='__main__': main()
