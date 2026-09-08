#!/usr/bin/env python3
"""Trusted source installer. Builds execute unrestricted code as the desktop user."""
import argparse, contextlib, ctypes, errno, hashlib, json, os, pathlib, re, shutil, signal, subprocess, tempfile, time, uuid, urllib.parse, xml.etree.ElementTree as ET

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

MANIFEST = '.git-build-manifest.json'
STATE = '.git-plugin.json'
CHECKOUT = '.git-source'


def metadata(relative):
    return relative.parts[0] in ('.git', CHECKOUT, MANIFEST, STATE, '.git-status.json', '.git-source-owner.json')


def file_hashes(path, artifacts_only=False):
    safe_tree(path)
    return {str(f.relative_to(path)): hashlib.sha256(f.read_bytes()).hexdigest()
            for f in sorted(path.rglob('*')) if f.is_file()
            and (not artifacts_only or (not metadata(f.relative_to(path))
                 and not is_configuration(f.relative_to(path))))}


def write_build_manifest(output, url, name, commit):
    atomic(output/MANIFEST, {'schema': 1, 'url': url, 'name': name, 'commit': commit,
                           'files': file_hashes(output, True)})


def proven_commit(target, url, name, proof=None):
    try:
        proof = proof if proof is not None else json.loads((target/MANIFEST).read_text())
        if (proof.get('schema') != 1 or proof.get('url') != url or proof.get('name') != name
                or not re.fullmatch('[a-f0-9]{40,64}', proof.get('commit', ''))
                or name+'.dll' not in proof.get('files', {})
                or proof['files'] != file_hashes(target, True)): return None
        return proof['commit']
    except (OSError, ValueError, InstallError): return None


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
    return count, size

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

def exchange(source, target):
    """Atomically swap complete directories: active folder never disappears."""
    libc = ctypes.CDLL(None, use_errno=True)
    if libc.renameat2(-100, os.fsencode(source), -100, os.fsencode(target), 2) != 0:
        raise InstallError('Atomic update failed; existing plugin retained.')


class Worker:
    def __init__(self, app, allow_local=False, read_only=False):
        self.app=pathlib.Path(app).absolute(); safe_tree(self.app)
        self.plugins=self.app/'Plugins'; self.home=self.app/'PluginSources'
        if not read_only: self.home.mkdir(exist_ok=True)
        self.allow_local=allow_local; self.child=None; self.sources=[]
        # Legacy registry/pending/backups are left untouched and never consumed.
        self.discover()

    def checkout(self, target):
        for repo in (target/CHECKOUT, target):
            if (repo/'.git').exists(): return repo
        return None

    @contextlib.contextmanager
    def check_context(self, repo):
        """Read plugin objects/refs in a disposable repo, never its executable config.

        User Git credentials and SSH configuration remain available. Only literal
        remote URLs and branch tracking values cross the checkout boundary.
        """
        gitdir=repo/'.git'
        if gitdir.is_file():
            pointer=gitdir.read_text().strip()
            if not pointer.startswith('gitdir: '): raise InstallError('Invalid Git directory.')
            gitdir=(repo/pointer[8:]).resolve()
        common=gitdir
        if (gitdir/'commondir').is_file(): common=(gitdir/(gitdir/'commondir').read_text().strip()).resolve()
        safe_tree(gitdir); safe_tree(common)
        if not (gitdir/'HEAD').is_file() or not (common/'objects').is_dir():
            raise InstallError('Not a plugin checkout.')
        with tempfile.TemporaryDirectory(prefix='plugin-check-') as directory:
            context=pathlib.Path(directory); isolated=context/'.git'; isolated.mkdir()
            (isolated/'config').write_text('[core]\n repositoryformatversion = 0\n bare = false\n hooksPath = /dev/null\n fsmonitor = false\n')
            (isolated/'objects/info').mkdir(parents=True)
            (isolated/'objects/info/alternates').write_text(str(common/'objects')+'\n')
            shutil.copy2(gitdir/'HEAD',isolated/'HEAD')
            shutil.copytree(common/'refs',isolated/'refs')
            for name in ('packed-refs','shallow'):
                if (common/name).is_file(): shutil.copy2(common/name,isolated/name)
            # --file plus --no-includes reads data without following local includes.
            raw=self.git_output(['config','--file',str(common/'config'),'--no-includes','--null','--list'],context)
            for entry in raw.split('\0'):
                key,separator,value=entry.partition('\n')
                if separator and (re.fullmatch(r'remote\..+\.url|branch\..+\.(remote|merge)',key)
                                  or key=='extensions.objectformat' and value=='sha256'):
                    self.git_output(['config','--local',key,value],context)
                    if key=='extensions.objectformat':
                        self.git_output(['config','--local','core.repositoryformatversion','1'],context)
            yield context

    def folder_key(self, target):
        info=target.stat()
        return [info.st_dev, info.st_ino]

    def discover(self):
        previous={s['name']:s for s in self.sources}; found=[]
        if self.plugins.exists():
            for target in sorted(self.plugins.iterdir()):
                branch=None
                if not target.is_dir() or target.name.startswith('.'): continue
                try:
                    safe_tree(target)
                    key=self.folder_key(target)
                except FileNotFoundError: continue
                if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_.-]{0,79}',target.name): continue
                source=dict(name=target.name, id=target.name, revisionState='local', status='Local')
                try:
                    state=json.loads((target/STATE).read_text()) if (target/STATE).exists() else {}
                    source.update({k:state[k] for k in ('instance','pending','auth','update') if k in state})
                    repo=self.checkout(target)
                    if repo:
                        source['status']='Installed revision unverified'
                        with self.check_context(repo) as context:
                            try:
                                branch=self.git_output(['symbolic-ref','--quiet','--short','HEAD'],context)
                                remote=self.git_output(['config','--get','branch.'+branch+'.remote'],context)
                            except InstallError:
                                remotes=self.git_output(['remote'],context).splitlines()
                                remote='origin' if 'origin' in remotes else remotes[0] if len(remotes)==1 else None
                                if remote is None: raise InstallError('No unambiguous remote repository linked.')
                            if remote == '.': raise InstallError('No remote repository linked.')
                            url,_=self.source_url(self.git_output(['remote','get-url',remote],context))
                            head=self.git_output(['rev-parse','HEAD'],context)
                            source.update(url=url, sourceHead=head, remote=remote, revisionState='unknown')
                            try: source['remoteRef']=self.git_output(['config','--get','branch.'+branch+'.merge'],context)
                            except (InstallError,TypeError): source['remoteRef']='HEAD'
                        old=previous.get(target.name,{})
                        if (old.get('folderKey')==key and old.get('url')==url and old.get('sourceHead')==head
                                and old.get('remote')==remote and old.get('remoteRef')==source['remoteRef']):
                            for field in ('revisionState','sourceRevisionState','behind','sourceBehind','checkedAt','status','checkError'):
                                if field in old: source[field]=old[field]
                        commit=proven_commit(target,url,target.name)
                        if commit: source['verifiedInstalledVersion']=commit
                        if commit != old.get('verifiedInstalledVersion'):
                            source['revisionState']='unknown'
                    else: source.pop('pending',None)
                except (OSError, ValueError, InstallError):
                    source['status']='Git metadata unavailable; installed revision unknown'
                if source.get('pending'):
                    source['pendingVersion']=source['pending'].get('version','')
                    source['status']='Staged update; restart to activate'
                source['folderKey']=key
                found.append(source)
        self.sources=found
        return found

    def save(self):
        # Disposable UI snapshot, never an input to discovery or installation.
        for source in self.sources:
            if self.same_folder(source) and self.checkout(self.plugins/source['name']):
                try: atomic(self.plugins/source['name']/'.git-status.json', source)
                except FileNotFoundError: pass

    def save_state(self, source):
        target=self.plugins/source['name']
        if not self.same_folder(source): raise InstallError('Plugin folder removed or replaced; request cancelled.')
        atomic(target/STATE,{k:source[k] for k in ('instance','pending','auth','update') if k in source})

    def same_folder(self, source):
        target=self.plugins/source['name']
        try: return target.is_dir() and self.folder_key(target)==source.get('folderKey')
        except FileNotFoundError: return False

    def run(self, args, cwd=None, timeout=180, capture=False):
        env=os.environ.copy(); env.update(GIT_TERMINAL_PROMPT='0', GIT_LFS_SKIP_SMUDGE='1', GH_PROMPT_DISABLED='1')
        # Never inherit controller tokens or injected Git configuration into a worker child.
        for key in list(env):
            if key in ('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN', 'GIT_CONFIG_PARAMETERS') or key.startswith('GIT_CONFIG_KEY_') or key.startswith('GIT_CONFIG_VALUE_') or key in ('GIT_CONFIG_COUNT', 'GIT_CONFIG', 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES'): env.pop(key)
        output = tempfile.TemporaryFile() if capture else None
        try:
            # Discard compiler/git output: arbitrary repositories can print credentials.
            self.child=subprocess.Popen(args,cwd=cwd,env=env,stdout=output if capture else subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
            deadline=time.monotonic()+timeout
            while self.child.poll() is None:
                if time.monotonic()>deadline: raise subprocess.TimeoutExpired(args,timeout)
                if cwd: safe_tree(cwd)
                time.sleep(0.2)
            if self.child.returncode: raise InstallError('Git or build failed. Check repository access, SDK and project compatibility.')
            if output:
                output.seek(0); return output.read(65536).decode('utf-8').strip()
        except InstallError:
            self.cancel(); raise
        except (OSError, subprocess.TimeoutExpired):
            self.cancel(); raise InstallError('Git/build unavailable or timed out (3 minute limit).') from None
        finally:
            self.child=None
            if output: output.close()
    def cancel(self):
        if self.child and self.child.poll() is None:
            os.killpg(self.child.pid, signal.SIGKILL); self.child.wait()
    def git_args(self, auth='none'):
        if auth not in ('none','desktop-gh'): raise InstallError('Unsupported authentication mode.')
        return ['git'] + (['-c','credential.https://github.com.helper=!gh auth git-credential'] if auth=='desktop-gh' else [])

    def git_transport(self, url, auth='none'):
        # Git/SSH use the launching user's normal helpers, agent and SSH config.
        return self.git_args(auth), url

    def source_url(self, url):
        local=self.allow_local and pathlib.Path(url).is_absolute()
        if local: return url.rstrip('/'), True
        scp=re.fullmatch(r'git@[A-Za-z0-9_.-]+:[A-Za-z0-9_./-]+',url)
        if scp: return url.rstrip('/'), False
        try:
            parsed=urllib.parse.urlsplit(url)
            valid=(parsed.scheme in ('https','ssh') and parsed.hostname and not parsed.password
                   and not parsed.query and not parsed.fragment
                   and (not parsed.username if parsed.scheme=='https' else parsed.username in (None,'git'))
                   and re.fullmatch(r'/[A-Za-z0-9_./-]+',parsed.path))
            if parsed.port is not None and not 0 < parsed.port < 65536: valid=False
        except ValueError: valid=False
        if not valid: raise InstallError('Use a credential-free HTTPS or Git SSH repository URL.')
        return url.rstrip('/'), False

    def git_output(self, args, cwd):
        return self.run(['git', *args], cwd=cwd, capture=True)

    def relation(self, repo, commit, head):
        counts=self.git_output(['rev-list','--left-right','--count',commit+'...'+head],repo).split()
        if len(counts)!=2 or not all(x.isdigit() for x in counts): raise InstallError('Invalid ancestry result.')
        ahead,behind=map(int,counts)
        return ('diverged' if ahead and behind else 'ahead' if ahead else 'behind' if behind else 'current'),behind

    def check_status(self, source):
        source['revisionState']='unknown'; source['sourceRevisionState']='unknown'
        source.pop('behind',None); source.pop('verifiedInstalledVersion',None)
        try:
            if not self.same_folder(source): raise InstallError('Plugin removed.')
            repo=self.checkout(self.plugins/source['name'])
            if repo is None or not source.get('url'): raise InstallError('No linked Git checkout.')
            with self.check_context(repo) as context:
                head=self.git_output(['rev-parse','HEAD'],context); source['sourceHead']=head
                commit=proven_commit(self.plugins/source['name'],source['url'],source['name'])
                if commit: source['verifiedInstalledVersion']=commit
                git,_=self.git_transport(source['url'],source.get('auth','none'))
                remote_ref=source.get('remoteRef','HEAD')
                if remote_ref!='HEAD' and not remote_ref.startswith('refs/heads/'):
                    raise InstallError('Unsupported upstream branch.')
                self.run(git+['fetch','--no-tags','--no-recurse-submodules','--',source['url'],remote_ref],cwd=context)
                remote_head=self.git_output(['rev-parse','FETCH_HEAD'],context)
                source['sourceRevisionState'],source['sourceBehind']=self.relation(context,head,remote_head)
                commit=proven_commit(self.plugins/source['name'],source['url'],source['name'])
                if commit:
                    source['verifiedInstalledVersion']=commit
                    source['revisionState'],source['behind']=self.relation(context,commit,remote_head)
            source['checkedAt']=int(time.time()); source.pop('checkError',None)
        except (OSError,ValueError,InstallError):
            source['checkError']='Repository unavailable or build provenance unverified; files retained.'
        self.save()
        return source['revisionState']

    def check_target(self, name, url):
        if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_.-]{0,79}',name): raise InstallError('Invalid plugin assembly name.')
        safe_tree(self.plugins)
        if self.plugins.exists() and any(p.name.casefold()==name.casefold() and p.name!=name for p in self.plugins.iterdir()):
            raise InstallError('Conflict: plugin name differs only by case.')
        target=self.plugins/name
        if target.exists():
            source=next((s for s in self.sources if s['name']==name),None)
            if not source or source.get('url')!=url: raise InstallError('Conflict: existing folder is Local or linked to another repository.')
        return target

    def build(self, repo, temp, url, version):
        # Compile a disposable copy. Keep the embedded checkout pristine, including
        # original project files; build adaptation is not part of the source commit.
        build_repo=temp/'build-source'; shutil.copytree(repo,build_repo,ignore=shutil.ignore_patterns('.git'))
        projects=[p for p in build_repo.rglob('*.csproj') if not any(x.lower() in ('obj','bin') or 'test' in x.lower() for x in p.relative_to(build_repo).parts)]
        if len(projects)!=1: raise InstallError('Expected exactly one non-test .csproj.')
        project=projects[0]; tree=ET.parse(project); root=tree.getroot()
        name=root.findtext('./PropertyGroup/AssemblyName') or project.stem
        self.check_target(name,url)
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
        shutil.copytree(repo,output/CHECKOUT)
        write_build_manifest(output,url,name,version)
        return name,output

    def install(self, url, trust, auth=None, expected=None):
        if trust is not True: raise InstallError('Explicit source trust acceptance is required.')
        self.discover()
        url,local=self.source_url(url)
        linked=[s for s in self.sources if s.get('url')==url]
        if len(linked)>1 and expected is None: raise InstallError('Select the existing plugin row to update.')
        existing=expected if expected is not None else next(iter(linked),None)
        if existing and not self.same_folder(existing): raise InstallError('Plugin removed; update cancelled.')
        auth=auth or (existing.get('auth','none') if existing else 'none')
        git,transport=self.git_transport(url,auth)
        active_files=file_hashes(self.plugins/existing['name'],True) if existing else None
        with tempfile.TemporaryDirectory(prefix='build-',dir=self.home) as temp:
            temp=pathlib.Path(temp); repo=temp/'repo'
            self.run(git+['-c','protocol.file.allow='+('always' if local else 'never'),'clone','--no-tags','--',transport,str(repo)],cwd=temp)
            if existing and existing.get('remoteRef','HEAD') != 'HEAD':
                remote_ref=existing['remoteRef']
                if not remote_ref.startswith('refs/heads/'): raise InstallError('Unsupported upstream branch.')
                self.run(git+['fetch','--no-tags','--','origin',remote_ref],cwd=repo)
                self.git_output(['checkout','-B',remote_ref[len('refs/heads/'):],'FETCH_HEAD'],repo)
                self.git_output(['config','branch.'+remote_ref[len('refs/heads/'):]+'.remote','origin'],repo)
                self.git_output(['config','branch.'+remote_ref[len('refs/heads/'):]+'.merge',remote_ref],repo)
            safe_tree(repo); version=self.git_output(['rev-parse','HEAD'],repo)
            name,output=self.build(repo,temp,url,version)
            target=self.check_target(name,url)
            if existing and (name!=existing['name'] or not self.same_folder(existing)):
                raise InstallError('Plugin folder removed or replaced during build; update cancelled.')
            if not existing and target.exists(): raise InstallError('Plugin folder appeared during build; Add cancelled.')
            record=dict(id=name,name=name,url=url,version=version,auth=auth,update=existing.get('update',True) if existing else True,
                        instance=existing.get('instance',uuid.uuid4().hex) if existing else uuid.uuid4().hex)
            atomic(output/STATE,{k:record[k] for k in ('instance','auth','update')})
            if existing:
                if file_hashes(target,True)!=active_files: raise InstallError('Installed files changed during build.')
                token=uuid.uuid4().hex
                pending=self.home/'pending'/token; pending.parent.mkdir(exist_ok=True)
                output.rename(pending)
                existing.update(instance=record['instance'],auth=auth,pending=dict(token=token,files=active_files,version=version,folderKey=existing['folderKey']))
                self.save_state(existing)
                record['status']='Staged update; restart to activate'
            else:
                target.parent.mkdir(exist_ok=True)
                rename_absent(output,target)
                record['status']='Installed; use Reload all plugins (no restart required)'
                record['installedVersion']=version
            self.discover()
            source=next(s for s in self.sources if s['name']==name)
            source['status']=record['status']; self.check_status(source)
            record.update({k:source[k] for k in ('revisionState','verifiedInstalledVersion','folderKey') if k in source})
            self.save(); return record

    def activate(self):
        # Only a pending reference inside a present folder authorizes activation.
        # Orphaned legacy staging/backups never become reinstall instructions.
        for source in self.discover():
            pending=source.get('pending')
            if not pending or not source.get('url'): continue
            if not isinstance(pending,dict) or not re.fullmatch('[a-f0-9]{32}',pending.get('token','')):
                raise InstallError('Invalid pending update.')
            target=self.plugins/source['name']; staged=self.home/'pending'/pending['token']
            if not self.same_folder(source) or source['folderKey']!=pending.get('folderKey'): continue
            safe_tree(staged)
            if not staged.is_dir(): raise InstallError('Pending update unavailable; active plugin retained.')
            if proven_commit(staged,source['url'],source['name'])!=pending['version']:
                raise InstallError('Staged build provenance changed; active plugin retained.')
            if json.loads((staged/STATE).read_text()).get('instance')!=source.get('instance'):
                raise InstallError('Staged update belongs to another installation.')
            if file_hashes(target,True)!=pending['files']: raise InstallError('Installed files changed since staging; review required.')
            for item in target.rglob('*'):
                relative=item.relative_to(target)
                if item.is_file() and is_configuration(relative):
                    dest=staged/relative; dest.parent.mkdir(parents=True,exist_ok=True); shutil.copy2(item,dest)
            atomic(staged/STATE,{k:source[k] for k in ('instance','auth','update') if k in source})
            # Reserve backup destination before swapping. If finalization fails,
            # old files remain at the unique pending path; active new folder has
            # no pending reference, so later startup cannot revert personal data.
            backups=self.home/'backups'; backups.mkdir(exist_ok=True)
            backup=backups/pending['token']
            if backup.exists(): raise InstallError('Backup destination already exists.')
            if not self.same_folder(source): continue
            exchange(staged,target)
            staged.rename(backup)
        self.discover(); self.save()

    def handle_request(self, data):
        self.discover()
        action=data.get('action')
        if action=='toggle':
            source=next((s for s in self.sources if s['id']==data.get('id') and s.get('url')),None)
            if source is None: raise InstallError('Plugin folder is no longer available.')
            source['update']=bool(data['update']); self.save_state(source); self.save()
            return 'Update preference saved.'
        if action=='check':
            for source in self.sources:
                if source.get('url'): self.check_status(source)
            self.save(); return 'Revision checks complete.'
        if action in ('update','update-all'):
            if data.get('trust') is not True: raise InstallError('Explicit source trust acceptance is required.')
            selected=[s for s in self.sources if s.get('url') and (action=='update-all' or s['id']==data.get('id'))]
            if not selected or len(selected)>32: raise InstallError('Select between 1 and 32 existing Git plugin folders.')
            failed=0
            for source in selected:
                try:
                    if not self.same_folder(source): raise InstallError('Plugin removed.')
                    self.install(source['url'],True,expected=source)
                except Exception: failed+=1
            self.discover(); self.save()
            return f'Updates complete: {len(selected)-failed} succeeded, {failed} failed.'
        if action=='install': return self.install(data['url'],data.get('trust'),data.get('auth'))['status']
        raise InstallError('Unknown installer action.')

    def serve(self):
        # Startup checks fetch metadata only. Builds require a user request.
        for source in self.discover():
            if source.get('url') and source.get('update',True): self.check_status(source)
        while True:
            self.discover(); self.save()
            atomic(self.home/'heartbeat.json',{'time':time.time()})
            request=self.home/'request.json'
            if request.exists():
                try:
                    safe_tree(request)
                    if request.stat().st_size>8192: raise InstallError('Request exceeds size limit.')
                    data=json.loads(request.read_text()); request.unlink()
                    result=self.handle_request(data)
                    atomic(self.home/'result.json',{'status':result})
                except Exception as error:
                    atomic(self.home/'result.json',{'status':str(error) if isinstance(error,InstallError) else 'Installation failed; previous copy retained.'})
            time.sleep(1)


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('mode',choices=['activate','serve']); parser.add_argument('app'); args=parser.parse_args()
    worker=Worker(args.app)
    def stop(*_): worker.cancel(); raise SystemExit(0)
    signal.signal(signal.SIGTERM,stop); signal.signal(signal.SIGINT,stop)
    if args.mode=='activate': worker.activate()
    else: worker.serve()
if __name__=='__main__': main()
