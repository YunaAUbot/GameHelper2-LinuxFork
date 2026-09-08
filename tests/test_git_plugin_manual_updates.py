#!/usr/bin/env python3
"""Pending updates remain bound to the actual original folder."""
import json, pathlib, shutil, unittest, uuid
from unittest import mock
import test_git_plugin_folders as fixtures
w=fixtures.w

class ManualUpdates(fixtures.Folders):
 def stage(self):
  source=self.linked(); w.write_build_manifest(self.target,str(self.repo),'Fixture',self.git(self.repo,'rev-parse','HEAD'))
  token=uuid.uuid4().hex; stage=self.worker.home/'pending'/token; stage.parent.mkdir(exist_ok=True)
  shutil.copytree(self.target,stage)
  source['instance']=uuid.uuid4().hex
  w.atomic(stage/w.STATE,dict(instance=source['instance']))
  source['pending']=dict(token=token,files=w.file_hashes(self.target,True),version=self.git(self.repo,'rev-parse','HEAD'),folderKey=source['folderKey'])
  self.worker.save_state(source)
  return source,stage
 def test_single_non_origin_remote_and_deleted_upstream(self):
  source=self.linked(); self.git(self.target,'remote','rename','origin','upstream')
  self.git(self.target,'checkout','--detach')
  source=self.worker.discover()[0];self.assertEqual('upstream',source['remote'])
  self.worker.check_status(source);self.assertEqual('current',source['sourceRevisionState'])
  self.git(self.target,'checkout','-b','topic')
  self.git(self.target,'config','branch.topic.remote','upstream')
  self.git(self.target,'config','branch.topic.merge','refs/heads/deleted')
  source=self.worker.discover()[0];self.worker.check_status(source)
  self.assertEqual('unknown',source['sourceRevisionState'])
 def test_replacement_same_name_cannot_activate(self):
  source,stage=self.stage(); self.target.rename(self.root/'removed')
  shutil.copytree(self.root/'removed',self.target)
  before=w.file_hashes(self.target,True); self.worker.activate()
  self.assertEqual(before,w.file_hashes(self.target,True)); self.assertTrue(stage.exists())
 def test_staging_or_active_tamper_refuses(self):
  source,stage=self.stage()
  for target in (stage,self.target):
   old=(target/'Fixture.dll').read_bytes(); (target/'Fixture.dll').write_bytes(b'MZchanged')
   with self.assertRaises(w.InstallError): self.worker.activate()
   (target/'Fixture.dll').write_bytes(old)
 def test_preference_changed_after_staging_survives(self):
  source,stage=self.stage()
  self.worker.handle_request(dict(action='toggle',id=source['id'],update=False))
  self.worker.activate()
  self.assertFalse(self.worker.discover()[0]['update'])
 def test_exchange_failure_retains_active(self):
  source,stage=self.stage(); before=w.file_hashes(self.target)
  with mock.patch.object(w,'exchange',side_effect=w.InstallError('injected')),self.assertRaises(w.InstallError):self.worker.activate()
  self.assertEqual(before,w.file_hashes(self.target)); self.assertTrue(stage.exists())
 def test_finalization_failure_cannot_revert_later_config(self):
  source,stage=self.stage(); original=pathlib.Path.rename
  def fail(path,destination):
   if path==stage: raise OSError('injected finalization')
   return original(path,destination)
  with mock.patch.object(pathlib.Path,'rename',fail),self.assertRaises(OSError):self.worker.activate()
  self.assertTrue(stage.exists()); self.assertNotIn('pending',json.loads((self.target/w.STATE).read_text()))
  (self.target/'config').mkdir(); (self.target/'config/settings.json').write_text('later')
  w.Worker(self.app,allow_local=True).activate()
  self.assertEqual('later',(self.target/'config/settings.json').read_text())
 def test_refresh_worktree_git_file_and_no_parent_adoption(self):
  self.target.parent.mkdir(exist_ok=True)
  self.git(self.repo,'remote','add','origin',str(self.repo))
  self.git(self.repo,'worktree','add','--detach',str(self.target),'HEAD')
  (self.target/'Fixture.dll').write_bytes(b'MZ')
  source=self.worker.discover()[0]; self.assertEqual(str(self.repo),source['url'])
  self.assertEqual(self.git(self.repo,'rev-parse','HEAD'),source['sourceHead'])
  local=self.target.parent/'Local';local.mkdir();self.git(self.target.parent,'init','-q')
  self.assertEqual('local',next(s for s in self.worker.discover() if s['name']=='Local')['revisionState'])

if __name__=='__main__':unittest.main()
