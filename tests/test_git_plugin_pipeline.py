#!/usr/bin/env python3
"""Real local Git + compiler pipeline. Never downloads or executes third-party code."""
from unittest import mock
import importlib.util, json, pathlib, tempfile, subprocess, unittest, os, sys, time
ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('worker', ROOT/'scripts/git-plugin-worker.py')
w = importlib.util.module_from_spec(spec); spec.loader.exec_module(w)
def reload_probe(root, app):
 # Compile exact production reload/discovery bodies with isolated lifecycle stubs.
 # This tests directory discovery + real ALC loads without a game or render loop.
 source = (ROOT/'GameHelper/Plugin/PManager.cs').read_text(encoding='utf-8-sig')
 methods = []
 for signature in ('internal static int ReloadAllPlugins()', 'private static List<PluginWithName> LoadPlugins()',
                   'private static List<DirectoryInfo> GetPluginsDirectories()',
                   'private static (Assembly assembly, PluginAssemblyLoadContext alc)? ReadPluginFiles(DirectoryInfo pluginDirectory)'):
  start = source.index(signature); brace = source.index('{', start); end = brace + 1; depth = 1
  while depth:
   depth += (source[end] == '{') - (source[end] == '}'); end += 1
  methods.append(source[start:end])
 project = root/'reload-probe'; project.mkdir()
 (project/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
 (project/'Program.cs').write_text((ROOT/'tests/GitPluginReloadProbe/Probe.cs').read_text().replace('// PRODUCTION_METHODS', '\n'.join(methods)))
 dotnet = os.environ.get('DOTNET', 'dotnet')
 subprocess.run([dotnet, 'build', str(project), '-c', 'Release', '--nologo', '-v:q'], check=True, capture_output=True)
 process = subprocess.Popen([dotnet, str(project/'bin/Release/net10.0/Probe.dll'), str(app/'Plugins')], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
 assert process.stdout.readline().strip() == 'ready'
 return process

def reload(process):
 process.stdin.write('reload\n'); process.stdin.flush()
 return process.stdout.readline().strip()

def close_probe(process):
 process.stdin.close()
 process.wait(timeout=10)
 process.stdout.close()

class Pipeline(unittest.TestCase):
 def test_pipeline(self):
  with tempfile.TemporaryDirectory() as tmp:
   root=pathlib.Path(tmp); repo=root/'repo'; repo.mkdir(); app=root/'app'; app.mkdir()
   def git(*args): subprocess.run(['git','-C',str(repo),*args],check=True,stdout=subprocess.DEVNULL)
   git('init','-q'); git('config','user.email','fixture@example.invalid'); git('config','user.name','Fixture')
   (repo/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name="ValidateGameHelperHost" BeforeTargets="PrepareForBuild"><Error Text="host source unavailable" /></Target><ItemGroup><ProjectReference Include="$(GameHelperHostRoot)/GameHelper/GameHelper.csproj" /></ItemGroup></Project>')
   (repo/'Plugin.cs').write_text('namespace GameHelper.Plugin { public class PCore<T> {} } public sealed class Fixture : GameHelper.Plugin.PCore<int> { public const int Version = 1; }')
   git('add','.'); git('commit','-qm','v1')
   worker=w.Worker(app, allow_local=True)
   probe=reload_probe(root, app); self.addCleanup(lambda: close_probe(probe) if probe.poll() is None else None)
   self.assertEqual('0:0', reload(probe))
   source=worker.install(str(repo),True)
   self.assertTrue((app/'Plugins/Fixture/Fixture.dll').is_file())
   self.assertFalse((worker.home/'pending'/source['id']).exists())
   self.assertIn('Reload all plugins', source['status'])
   self.assertEqual(source['version'], source['installedVersion'])
   self.assertEqual('1:1', reload(probe))
   target=app/'Plugins/Fixture'; old=(target/'Fixture.dll').read_bytes(); (target/'config').mkdir(); (target/'config/settings.json').write_text('keep')
   (repo/'Plugin.cs').write_text('namespace GameHelper.Plugin { public class PCore<T> {} } public sealed class Fixture : GameHelper.Plugin.PCore<int> { public const int Version = 2; }'); git('add','.'); git('commit','-qm','v2')
   updated=worker.install(str(repo),True); self.assertNotEqual(source['version'],updated['version']); self.assertEqual(old,(target/'Fixture.dll').read_bytes())
   self.assertEqual('1:1', reload(probe))  # old ALC remains strongly retained
   self.assertEqual(old,(target/'Fixture.dll').read_bytes())
   close_probe(probe)  # startup activation is only legal after host exit
   original_rename=pathlib.Path.rename
   def fail_activation(path, destination):
    if path.parent.name=='pending': raise OSError('fixture activation failure')
    return original_rename(path,destination)
   with mock.patch.object(pathlib.Path,'rename',fail_activation), self.assertRaises(w.InstallError): worker.activate()
   self.assertEqual(old,(target/'Fixture.dll').read_bytes())
   worker.activate(); self.assertNotEqual(old,(target/'Fixture.dll').read_bytes()); self.assertEqual('keep',(target/'config/settings.json').read_text())
   restarted=root/'restarted'; restarted.mkdir()
   after=reload_probe(restarted,app)
   try: self.assertEqual('1:2',reload(after))
   finally: close_probe(after)
   good=(target/'Fixture.dll').read_bytes()
   self.assertTrue((worker.home/('previous-'+source['id'])/'Fixture.dll').exists())
   worker.sources[0]['update']=False; worker.save()
   self.assertFalse(w.Worker(app,allow_local=True).sources[0]['update'])
   (repo/'Extra.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk" />'); git('add','.'); git('commit','-qm','ambiguous')
   with self.assertRaises(w.InstallError): worker.install(str(repo),True)
   (repo/'Extra.csproj').unlink()
   (repo/'Plugin.cs').write_text('public class NotAPlugin {}'); git('add','-A'); git('commit','-qm','invalid plugin')
   with self.assertRaises(w.InstallError): worker.install(str(repo),True)
   self.assertEqual(good,(target/'Fixture.dll').read_bytes())
   (repo/'Plugin.cs').write_text('syntax error'); git('add','.'); git('commit','-qm','broken')
   with self.assertRaises(w.InstallError): worker.install(str(repo),True)
   self.assertEqual(good,(target/'Fixture.dll').read_bytes())
   repo.rename(root/'offline')
   with self.assertRaises(w.InstallError): worker.install(str(repo),True)
   self.assertEqual(good,(target/'Fixture.dll').read_bytes())
   with self.assertRaises(w.InstallError): worker.install('https://token@github.com/a/b',True)
   with self.assertRaises(w.InstallError): worker.install('https://github.com/a/b?token=secret',True)
   with self.assertRaises(w.InstallError): worker.install('https://github.com/a/b',False)
 def pending(self, root, installed=False):
  worker=w.Worker(root)
  source={'id':'a'*24,'name':'Fixture','version':'v1','url':'https://example.invalid/a/b','update':False,'status':'Staged; restart to activate'}
  if installed: source['installedVersion']='v0'
  worker.sources=[source]; worker.save()
  staged=worker.home/'pending'/source['id']; staged.mkdir(parents=True)
  (staged/'Fixture.dll').write_bytes(b'MZfixture')
  w.atomic(staged/'.git-source-owner.json',{'id':source['id'],'version':source['version']})
  return worker, source, staged
 def activation_fixture(self, root):
  worker, source, staged=self.pending(root, installed=True)
  source['version']='v2'; source['installedVersion']='v1'; worker.save()
  w.atomic(staged/'.git-source-owner.json',{'id':source['id'],'version':'v2'})
  target=worker.app/'Plugins/Fixture'; target.mkdir(parents=True)
  (target/'Fixture.dll').write_bytes(b'MZv1')
  (target/'config').mkdir(); (target/'config/settings.json').write_text('before update')
  w.atomic(target/'.git-source-owner.json',{'id':source['id'],'version':'v1'})
  previous=worker.home/('previous-'+source['id']); previous.mkdir()
  (previous/'old').write_text('v0')
  return worker, source, staged, target, previous
 def assert_active(self, app, version, config):
  target=pathlib.Path(app)/'Plugins/Fixture'
  self.assertEqual(version,json.loads((target/'.git-source-owner.json').read_text())['version'])
  self.assertEqual(version,w.Worker(app).sources[0]['installedVersion'])
  self.assertEqual(config,(target/'config/settings.json').read_text())
 def test_finalization_failure_refuses_then_recovers_without_later_config_loss(self):
  for failure in ('remove_previous','rename_backup'):
   with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
    worker, source, staged, target, previous=self.activation_fixture(tmp)
    rename=pathlib.Path.rename; rmtree=w.shutil.rmtree
    def fail_rename(path, destination):
     if path.name.startswith('backup-') and pathlib.Path(destination)==previous: raise OSError('injected backup rename')
     return rename(path,destination)
    def fail_remove(path, *args, **kwargs):
     if pathlib.Path(path)==previous:
      (previous/'old').unlink(missing_ok=True)  # partial removal is retryable
      raise OSError('injected previous removal')
     return rmtree(path,*args,**kwargs)
    patch=mock.patch.object(w.shutil,'rmtree',fail_remove) if failure=='remove_previous' else mock.patch.object(pathlib.Path,'rename',fail_rename)
    with patch, self.assertRaises(w.InstallError): worker.activate()
    self.assertTrue((worker.home/('backup-'+source['id'])).exists())
    w.Worker(tmp).activate()
    self.assert_active(tmp,'v1','before update')
    # A successful launch may now edit configuration. No stale backup can undo it.
    (target/'config/settings.json').write_text('after recovered launch')
    w.Worker(tmp).activate()
    self.assert_active(tmp,'v1','after recovered launch')
 def test_recovery_reconciles_registry_after_interrupted_restore(self):
  for restore_finished in (False,True):
   with self.subTest(restore_finished=restore_finished), tempfile.TemporaryDirectory() as tmp:
    worker, source, staged, target, previous=self.activation_fixture(tmp)
    w.shutil.rmtree(staged)
    source['installedVersion']='v2'; worker.save()
    if not restore_finished: target.rename(worker.home/('backup-'+source['id']))
    w.Worker(tmp).activate()
    self.assert_active(tmp,'v1','before update')
    (target/'config/settings.json').write_text('later')
    w.Worker(tmp).activate(); self.assert_active(tmp,'v1','later')
 def test_recovery_save_failure_refuses_and_retries(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker, source, staged, target, previous=self.activation_fixture(tmp)
   w.shutil.rmtree(staged); source['installedVersion']='v2'; worker.save()
   target.rename(worker.home/('backup-'+source['id']))
   with mock.patch.object(worker,'save',side_effect=OSError('injected registry failure')), self.assertRaises(OSError): worker.activate()
   w.Worker(tmp).activate(); self.assert_active(tmp,'v1','before update')
 def test_interrupted_recovery_refuses_until_backup_can_be_restored(self):
  for failure in ('remove_active','restore_backup'):
   with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
    worker, source, staged, target, previous=self.activation_fixture(tmp)
    backup=worker.home/('backup-'+source['id']); target.rename(backup); staged.rename(target)
    source['installedVersion']='v2'; worker.save()
    rename=pathlib.Path.rename; rmtree=w.shutil.rmtree
    def fail_rename(path, destination):
     if path==backup: raise OSError('injected restore failure')
     return rename(path,destination)
    def fail_remove(path, *args, **kwargs):
     if pathlib.Path(path)==target: raise OSError('injected recovery removal')
     return rmtree(path,*args,**kwargs)
    patch=mock.patch.object(w.shutil,'rmtree',fail_remove) if failure=='remove_active' else mock.patch.object(pathlib.Path,'rename',fail_rename)
    with patch, self.assertRaises(OSError): w.Worker(tmp).activate()
    self.assertTrue(backup.exists())
    w.Worker(tmp).activate(); self.assert_active(tmp,'v1','before update')
 def test_successful_update_preserves_later_config_and_version_on_next_start(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker, source, staged, target, previous=self.activation_fixture(tmp)
   worker.activate(); self.assert_active(tmp,'v2','before update')
   (target/'config/settings.json').write_text('after v2 launch')
   w.Worker(tmp).activate(); self.assert_active(tmp,'v2','after v2 launch')
 def test_launcher_honors_real_worker_finalization_refusal(self):
  for failure in ('remove_previous','rename_backup'):
   with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
    root=pathlib.Path(tmp); app=root/'app'; app.mkdir()
    worker, source, staged, target, previous=self.activation_fixture(app)
    (app/'GameHelper.exe').touch()
    proc=root/'proc/1234'; proc.mkdir(parents=True)
    (proc/'cmdline').write_bytes(b'PathOfExileSteam.exe\0')
    (proc/'environ').write_bytes(b'SteamAppId=2694490\0')
    steam=root/'steam'; (steam/'steamapps').mkdir(parents=True)
    library=root/'library'; (library/'steamapps/compatdata/2694490/pfx').mkdir(parents=True)
    proton=root/'proton'; calls=root/'proton-calls'
    proton.write_text('#!/bin/sh\nprintf called >> "$PROTON_CALLS"\nexit 42\n'); proton.chmod(0o755)
    # Inject only filesystem failures into the actual worker subprocess; neither
    # activate/main nor the production launcher is replaced by a stub.
    (root/'sitecustomize.py').write_text("""import os, pathlib, shutil
if os.environ.get('INJECT_FINALIZATION') == 'remove_previous':
 original = shutil.rmtree
 def remove(path, *args, **kwargs):
  if pathlib.Path(path).name.startswith('previous-'): raise OSError('injected previous removal')
  return original(path, *args, **kwargs)
 shutil.rmtree = remove
elif os.environ.get('INJECT_FINALIZATION') == 'rename_backup':
 original = pathlib.Path.rename
 def rename(path, destination):
  if path.name.startswith('backup-') and pathlib.Path(destination).name.startswith('previous-'): raise OSError('injected backup rename')
  return original(path, destination)
 pathlib.Path.rename = rename
""")
    env=dict(os.environ, PYTHONPATH=str(root), INJECT_FINALIZATION=failure,
             GH2_PROC_ROOT=str(root/'proc'), GH2_LOCK_FILE=str(root/'lock'),
             GAMEHELPER2_EXE=str(app/'GameHelper.exe'), STEAM_ROOT=str(steam),
             POE2_LIBRARY=str(library), PROTON=str(proton), PROTON_CALLS=str(calls),
             GH2_POLL_INTERVAL='0.02', GH2_HELPER_START_LIMIT='1')
    result=subprocess.run([str(ROOT/'run-gamehelper2-linux.sh')],env=env,capture_output=True,text=True,timeout=10)
    self.assertEqual(10,result.returncode,result.stdout+result.stderr)
    self.assertIn('refusing launch',result.stderr)
    self.assertFalse(calls.exists(),'Proton must never start on unresolved activation')
    self.assertFalse((worker.home/'heartbeat.json').exists(),'serve must never start on unresolved activation')
    w.Worker(app).activate(); self.assert_active(app,'v1','before update')
 def test_pending_first_install_and_updates(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker, source, staged=self.pending(tmp)
   self.assertTrue(worker.publish_first(source))
   self.assertFalse(staged.exists())
   self.assertTrue((worker.app/'Plugins/Fixture/Fixture.dll').exists())
   self.assertFalse(worker.publish_first(source))
  for history in ('installedVersion','backup','previous'):
   with self.subTest(history=history), tempfile.TemporaryDirectory() as tmp:
    worker, source, staged=self.pending(tmp, installed=history=='installedVersion')
    if history!='installedVersion': (worker.home/(history+'-'+source['id'])).mkdir()
    self.assertFalse(worker.publish_first(source))
    self.assertTrue(staged.exists())
    self.assertFalse((worker.app/'Plugins/Fixture').exists())
 def test_pending_migration_with_updates_disabled(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker, source, staged=self.pending(tmp)
   process=subprocess.Popen([sys.executable,str(ROOT/'scripts/git-plugin-worker.py'),'serve',tmp])
   try:
    for _ in range(100):
     if (worker.home/'heartbeat.json').exists(): break
     time.sleep(.05)
    self.assertTrue((worker.app/'Plugins/Fixture/Fixture.dll').is_file())
    self.assertFalse(staged.exists())
   finally:
    process.terminate(); process.wait(timeout=3)
 def test_atomic_conflict_preserves_empty_target(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker, source, staged=self.pending(tmp)
   target=worker.app/'Plugins/Fixture'
   original=w.rename_absent
   def race(src, dest):
    dest.mkdir()  # even an empty directory cannot be replaced by renameat2
    original(src,dest)
   with mock.patch.object(w,'rename_absent',race), self.assertRaises(w.InstallError):
    worker.publish_first(source)
   self.assertTrue(target.is_dir()); self.assertEqual([], list(target.iterdir()))
   self.assertTrue((staged/'Fixture.dll').is_file())
   self.assertNotIn('installedVersion', source)
 def test_startup_pending_first_install_uses_atomic_publication(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker, source, staged=self.pending(tmp)
   with mock.patch.object(w,'rename_absent',wraps=w.rename_absent) as publish:
    worker.activate()
   publish.assert_called_once()
   self.assertFalse(staged.exists())
   self.assertTrue((worker.app/'Plugins/Fixture/Fixture.dll').is_file())
 def test_publication_failure_and_ownership(self):
  for failure in ('save','rename','owner','symlink'):
   with self.subTest(failure=failure), tempfile.TemporaryDirectory() as tmp:
    worker, source, staged=self.pending(tmp)
    if failure=='owner': w.atomic(staged/'.git-source-owner.json',{'id':'b'*24,'version':'v1'})
    if failure=='symlink': (staged/'escape').symlink_to('/tmp')
    if failure=='save': context=mock.patch.object(worker,'save',side_effect=[OSError('fixture'),None])
    elif failure=='rename': context=mock.patch.object(w,'rename_absent',side_effect=w.InstallError('fixture'))
    else: context=mock.patch.object(worker,'cancel')
    with context, self.assertRaises((OSError,w.InstallError)): worker.publish_first(source)
    self.assertFalse((worker.app/'Plugins/Fixture').exists())
    self.assertTrue((staged/'Fixture.dll').is_file())
    self.assertNotIn('installedVersion', source)
 def test_conflict_and_symlink(self):
  with tempfile.TemporaryDirectory() as tmp:
   app=pathlib.Path(tmp); worker=w.Worker(app)
   (app/'Plugins').mkdir(exist_ok=True); (app/'Plugins/NinjaPricer').mkdir()
   with self.assertRaises(w.InstallError): worker.check_target('NinjaPricer','abc')
   with self.assertRaises(w.InstallError): worker.check_target('ninjapricer','abc')
   with self.assertRaises(w.InstallError): worker.check_target('../Escape','abc')
   (app/'Plugins/Escape').symlink_to('/tmp',target_is_directory=True)
   with self.assertRaises(w.InstallError): worker.check_target('Escape','abc')
 def test_timeout(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker=w.Worker(tmp); start=time.monotonic()
   with self.assertRaises(w.InstallError): worker.run([sys.executable,'-c','import time; time.sleep(60)'],timeout=0.1)
   self.assertLess(time.monotonic()-start,3); self.assertIsNone(worker.child)
 def test_worker_shutdown(self):
  with tempfile.TemporaryDirectory() as tmp:
   process=subprocess.Popen([sys.executable,str(ROOT/'scripts/git-plugin-worker.py'),'serve',tmp])
   try:
    for _ in range(100):
     if (pathlib.Path(tmp)/'PluginSources/heartbeat.json').exists(): break
     time.sleep(.05)
    self.assertTrue((pathlib.Path(tmp)/'PluginSources/heartbeat.json').exists())
    process.terminate(); self.assertEqual(0,process.wait(timeout=3))
   finally:
    if process.poll() is None: process.kill(); process.wait()
if __name__=='__main__': unittest.main()
