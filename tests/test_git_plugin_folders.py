"""Folder authority using real local Git; builds use an isolated local fixture."""
import hashlib, importlib.util, json, os, pathlib, shutil, subprocess, tempfile, unittest
from unittest import mock
ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('worker', ROOT/'scripts/git-plugin-worker.py')
w = importlib.util.module_from_spec(spec); spec.loader.exec_module(w)

class Folders(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
  self.root=pathlib.Path(self.tmp.name); self.app=self.root/'app'; self.app.mkdir()
  self.repo=self.root/'remote'; self.repo.mkdir()
  self.git(self.repo,'init','-q'); self.git(self.repo,'config','user.email','fixture@example.invalid'); self.git(self.repo,'config','user.name','Fixture')
  (self.repo/'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
  (self.repo/'Plugin.cs').write_text('namespace GameHelper.Plugin { public class PCore<T> {} } public sealed class Fixture : GameHelper.Plugin.PCore<int> { public const int Version = 1; }')
  self.commit('first'); self.worker=w.Worker(self.app,allow_local=True)
  self.target=self.app/'Plugins/Fixture'
 def git(self, repo, *args):
  return subprocess.check_output(['git','-C',str(repo),*args],stderr=subprocess.DEVNULL,text=True).strip()
 def commit(self, message):
  self.git(self.repo,'add','.'); self.git(self.repo,'commit','-qm',message)
  return self.git(self.repo,'rev-parse','HEAD')
 def linked(self):
  self.target.parent.mkdir(exist_ok=True)
  subprocess.run(['git','clone','-q',str(self.repo),str(self.target)],check=True)
  (self.target/'Fixture.dll').write_bytes(b'MZold')
  return self.worker.discover()[0]
 def build(self):
  return self.worker.install(str(self.repo),True)
 def test_empty_ignores_stale_registry_and_pending(self):
  w.atomic(self.worker.home/'sources.json',[dict(id='a'*24,name='Ghost',url=str(self.repo),version='a'*40)])
  pending=self.worker.home/'pending'/('a'*24); pending.mkdir(parents=True); (pending/'Ghost.dll').write_bytes(b'MZ')
  worker=w.Worker(self.app,allow_local=True)
  with mock.patch.object(worker,'run',side_effect=AssertionError('network')):
   self.assertEqual([],worker.discover()); worker.activate(); worker.handle_request({'action':'check'})
  self.assertFalse((self.app/'Plugins/Ghost').exists()); self.assertTrue(pending.exists())
  self.assertTrue((worker.home/'sources.json').exists())
 def test_local_and_folder_git(self):
  source=self.linked()
  self.assertEqual(str(self.repo),source['url']); self.assertEqual(self.git(self.repo,'rev-parse','HEAD'),source['sourceHead'])
  self.assertNotIn('verifiedInstalledVersion',source)
  shutil.rmtree(self.target/'.git')
  self.assertEqual('local',self.worker.discover()[0]['revisionState'])
 def test_remove_disappears_and_update_does_not_clone(self):
  source=self.linked(); self.target.rename(self.root/'removed')
  with mock.patch.object(self.worker,'run',side_effect=AssertionError('network')):
   self.assertEqual([],self.worker.discover()); self.worker.activate()
   with self.assertRaises(w.InstallError): self.worker.handle_request(dict(action='update',id=source['id'],trust=True))
  self.assertFalse(self.target.exists())
 def test_source_ancestry_and_offline(self):
  source=self.linked(); self.assertEqual('unknown',self.worker.check_status(source))
  self.assertEqual('current',source['sourceRevisionState'])
  (self.repo/'advance').write_text('1'); self.commit('advance')
  self.worker.check_status(source); self.assertEqual('behind',source['sourceRevisionState'])
  self.git(self.target,'config','user.email','fixture@example.invalid'); self.git(self.target,'config','user.name','Fixture')
  (self.target/'local').write_text('1'); self.git(self.target,'add','local'); self.git(self.target,'commit','-qm','local')
  self.worker.check_status(source); self.assertEqual('diverged',source['sourceRevisionState'])
  self.git(self.repo,'reset','--hard','HEAD~1'); self.worker.check_status(source); self.assertEqual('ahead',source['sourceRevisionState'])
  self.repo.rename(self.root/'offline'); self.worker.check_status(source); self.assertEqual('unknown',source['sourceRevisionState'])
  self.assertEqual('unknown',source['revisionState'])
 def test_update_all_actual_linked_folders_only(self):
  source=self.linked(); local=self.target.parent/'Local'; local.mkdir(); (local/'Local.dll').touch()
  w.atomic(self.worker.home/'sources.json',[dict(name='Ghost',url='https://example.invalid/a/b',id='b'*24)])
  with mock.patch.object(self.worker,'install',return_value={'status':'staged'}) as build:
   self.worker.handle_request(dict(action='update-all',trust=True))
   self.assertEqual(1,build.call_count); self.assertEqual(str(self.repo),build.call_args.args[0])
 def test_normal_git_credentials_not_overridden(self):
  args,url=self.worker.git_transport('git@example.invalid:owner/project.git')
  self.assertEqual(['git'],args); self.assertEqual('git@example.invalid:owner/project.git',url)
  source=(ROOT/'scripts/git-plugin-worker.py').read_text()
  for forbidden in ('DEPLOY_REPOS','trackingOnly','YunaAUbot','AngeArbitrage','RunecraftHelper','deploy-keys.json','GIT_CONFIG_GLOBAL='):
   self.assertNotIn(forbidden,source)

@unittest.skipUnless(shutil.which(os.environ.get('DOTNET','dotnet')), 'requires .NET SDK')
class Builds(Folders):
 def test_fresh_add_after_deletion_and_pending_cannot_resurrect(self):
  first=self.build(); old=(self.target/'Fixture.dll').read_bytes()
  (self.target/'config').mkdir(); (self.target/'config/settings.json').write_text('personal')
  (self.repo/'Plugin.cs').write_text((self.repo/'Plugin.cs').read_text().replace('Version = 1','Version = 2')); self.commit('v2')
  update=self.build(); self.assertEqual(old,(self.target/'Fixture.dll').read_bytes())
  shutil.rmtree(self.target); self.worker.activate(); self.assertFalse(self.target.exists())
  fresh=self.build(); self.assertNotEqual(old,(self.target/'Fixture.dll').read_bytes())
  self.assertNotEqual(first['instance'],fresh['instance']); self.assertFalse((self.target/'config/settings.json').exists())
 def test_checkout_provenance_staging_config_and_backup(self):
  first=self.build(); head=first['version']; checkout=self.target/'.git-source'
  self.assertTrue((checkout/'.git').is_dir()); self.assertEqual('',self.git(checkout,'status','--porcelain'))
  self.assertEqual(head,w.proven_commit(self.target,str(self.repo),'Fixture'))
  (self.target/'config').mkdir(); (self.target/'config/settings.json').write_text('personal')
  (self.repo/'Plugin.cs').write_text((self.repo/'Plugin.cs').read_text().replace('Version = 1','Version = 2')); new=self.commit('v2')
  self.git(checkout,'pull','--ff-only')
  source=self.worker.discover()[0]; self.worker.check_status(source)
  self.assertEqual(new,source['sourceHead']); self.assertEqual(head,source['verifiedInstalledVersion']); self.assertEqual('behind',source['revisionState'])
  update=self.build(); self.assertEqual(head,w.proven_commit(self.target,str(self.repo),'Fixture'))
  self.worker.activate(); self.assertEqual(new,w.proven_commit(self.target,str(self.repo),'Fixture'))
  self.assertEqual('personal',(self.target/'config/settings.json').read_text())
  self.assertTrue(any((self.worker.home/'backups').rglob('Fixture.dll')))
 def test_deletion_during_build_never_publishes_update(self):
  self.build(); original=self.worker.run
  def run(args, **kwargs):
   result=original(args,**kwargs)
   if 'build' in args: shutil.rmtree(self.target)
   return result
  with mock.patch.object(self.worker,'run',side_effect=run), self.assertRaises(w.InstallError): self.build()
  self.assertFalse(self.target.exists())

if __name__=='__main__': unittest.main()
