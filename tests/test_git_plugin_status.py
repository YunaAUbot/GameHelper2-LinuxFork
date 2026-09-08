#!/usr/bin/env python3
"""Real checkout ancestry, artifact provenance and ordinary user authentication."""
import hashlib, importlib.util, json, pathlib, subprocess, tempfile, unittest
from unittest import mock
ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('worker', ROOT/'scripts/git-plugin-worker.py')
w = importlib.util.module_from_spec(spec); spec.loader.exec_module(w)

class Status(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
  self.root=pathlib.Path(self.tmp.name); self.repo=self.root/'repo'; self.repo.mkdir()
  self.git('init','-q'); self.git('config','user.name','Fixture'); self.git('config','user.email','fixture@example.invalid')
  self.a=self.commit('a'); self.b=self.commit('b'); self.c=self.commit('c')
  self.app=self.root/'app'; self.app.mkdir(); self.worker=w.Worker(self.app,allow_local=True)
  self.target=self.app/'Plugins/Fixture'; self.target.mkdir(parents=True)
  (self.target/'Fixture.dll').write_bytes(b'MZfixture'); (self.target/'config').mkdir(); (self.target/'config/settings.txt').write_text('personal')
  self.source={'id':hashlib.sha256(str(self.repo).encode()).hexdigest()[:24], 'url':str(self.repo),'name':'Fixture','version':self.a,'installedVersion':self.a,'update':False}
  subprocess.run(['git','clone','-q',str(self.repo),str(self.target/'.git-source')],check=True)
  self.source=self.worker.discover()[0]
  w.write_build_manifest(self.target,self.source['url'],'Fixture',self.a)
 def git(self,*args):return subprocess.check_output(['git','-C',str(self.repo),*args],stderr=subprocess.DEVNULL,text=True).strip()
 def commit(self,value):
  (self.repo/'source').write_text(value); self.git('add','.'); self.git('commit','-qm',value); return self.git('rev-parse','HEAD')
 def installed(self,sha):
  self.source['installedVersion']=sha; w.write_build_manifest(self.target,self.source['url'],'Fixture',sha)
 def test_behind_noop_ahead_diverged(self):
  self.worker.check_status(self.source); self.assertEqual(('behind',2),(self.source['revisionState'],self.source['behind']))
  self.installed(self.c); self.worker.check_status(self.source); self.assertEqual('current',self.source['revisionState'])
  self.git('reset','--hard',self.a); self.worker.check_status(self.source); self.assertEqual('ahead',self.source['revisionState'])
  self.commit('branch'); self.worker.check_status(self.source); self.assertEqual('diverged',self.source['revisionState'])
 def test_auth_is_opt_in_scoped_and_tokens_are_not_inherited(self):
  self.assertEqual(['git'],self.worker.git_args())
  self.assertIn('credential.https://github.com.helper=!gh auth git-credential',self.worker.git_args('desktop-gh'))
  with self.assertRaises(w.InstallError):self.worker.git_args('arbitrary-helper')
  import os,sys
  with mock.patch.dict(os.environ,{'GH_TOKEN':'fixture-do-not-forward','GITHUB_TOKEN':'fixture-do-not-forward','GIT_ASKPASS':'fixture','GIT_CONFIG_COUNT':'1','GIT_CONFIG_KEY_0':'credential.helper','GIT_CONFIG_VALUE_0':'fixture'}):
   result=self.worker.run([sys.executable,'-c',"import os;print(','.join(k for k in ('GH_TOKEN','GITHUB_TOKEN','GIT_CONFIG_COUNT','GIT_CONFIG_KEY_0','GIT_CONFIG_VALUE_0') if k in os.environ))"],capture=True)
  self.assertEqual('',result)
 def test_normal_user_ssh_environment_survives(self):
  import os,sys
  with mock.patch.dict(os.environ,{'SSH_AUTH_SOCK':'fixture-agent','GIT_SSH_COMMAND':'fixture-ssh','GIT_CONFIG_GLOBAL':'fixture-config'}):
   result=self.worker.run([sys.executable,'-c',"import os;print('|'.join(os.environ[k] for k in ('SSH_AUTH_SOCK','GIT_SSH_COMMAND','GIT_CONFIG_GLOBAL')))"],capture=True)
  self.assertEqual('fixture-agent|fixture-ssh|fixture-config',result)
 def test_generic_ssh_transport_uses_existing_user_command(self):
  import os,shlex
  ssh=self.root/'ssh';trace=self.root/'ssh-used'
  ssh.write_text('#!/bin/sh\ncase " $* " in *" -G "*) exit 0;; esac\nprintf used > '+shlex.quote(str(trace))+'\nexec git-upload-pack '+shlex.quote(str(self.repo))+'\n')
  ssh.chmod(0o755)
  with mock.patch.dict(os.environ,GIT_SSH_COMMAND=str(ssh)):
   args,url=self.worker.git_transport('git@fixture-alias:owner/repository.git')
   self.worker.run(args+['clone','--',url,str(self.root/'ssh-clone')])
  self.assertTrue(trace.exists());self.assertEqual(self.c,self.worker.git_output(['rev-parse','HEAD'],self.root/'ssh-clone'))
 def test_untrusted_checkout_commands_never_run_manual_or_automatic(self):
  import os,shlex
  checkout=self.target/'.git-source'; marker=self.root/'untrusted-command'
  trusted=self.root/'user-ssh'; trace=self.root/'user-auth-used'
  trusted.write_text('#!/bin/sh\ncase " $* " in *" -G "*) exit 0;; esac\nprintf used > '+shlex.quote(str(trace))+'\nexec git-upload-pack '+shlex.quote(str(self.repo))+'\n')
  trusted.chmod(0o755)
  user_config=self.root/'user.gitconfig'
  subprocess.run(['git','config','--file',str(user_config),'core.sshCommand',str(trusted)],check=True)
  subprocess.run(['git','config','--file',str(user_config),'credential.helper','user-fixture'],check=True)
  command='touch '+shlex.quote(str(marker))+'; exit 1'
  def config(key,value):
   subprocess.run(['git','-C',str(checkout),'config',key,value],check=True)
  config('remote.origin.url','git@fixture-alias:owner/repository.git')
  config('core.sshCommand',command)
  config('core.fsmonitor',command)
  config('credential.helper','!'+command)
  config('filter.fixture.smudge',command)
  include=self.root/'untrusted-include'
  include.write_text('[core]\n sshCommand = '+command+'\n')
  config('include.path',str(include))
  hooks=checkout/'.git/hooks'; hooks.mkdir(exist_ok=True)
  hook=hooks/'reference-transaction'; hook.write_text('#!/bin/sh\n'+command+'\n'); hook.chmod(0o755)
  before=w.file_hashes(checkout)
  with mock.patch.dict(os.environ,GIT_CONFIG_GLOBAL=str(user_config)):
   with self.worker.check_context(checkout) as context:
    self.assertEqual('user-fixture',self.worker.git_output(['config','--get','credential.helper'],context))
    self.assertEqual('false',self.worker.git_output(['config','--get','core.fsmonitor'],context))
    self.assertEqual('/dev/null',self.worker.git_output(['config','--get','core.hooksPath'],context))
    for key in ('include.path','filter.fixture.smudge'):
     with self.assertRaises(w.InstallError): self.worker.git_output(['config','--get',key],context)
   # Both entry points must use user authentication and never run local commands.
   for automatic in (False,True):
    with self.subTest(automatic=automatic):
     marker.unlink(missing_ok=True); trace.unlink(missing_ok=True)
     if automatic:
      original_sleep=w.time.sleep
      def stop_loop(seconds):
       if seconds==1: raise KeyboardInterrupt()
       original_sleep(seconds)
      with mock.patch.object(w.time,'sleep',side_effect=stop_loop), self.assertRaises(KeyboardInterrupt): self.worker.serve()
     else: self.worker.handle_request({'action':'check'})
     self.assertFalse(marker.exists(),'repository-local executable config ran')
     self.assertTrue(trace.exists(),'trusted user SSH authentication was lost')
     self.assertEqual('current',self.worker.sources[0]['sourceRevisionState'])
     self.assertEqual(before,w.file_hashes(checkout),'read-only check changed checkout')
 def test_worktree_git_file_and_packed_refs_still_check(self):
  import shutil
  checkout=self.target/'.git-source'; shutil.rmtree(checkout)
  external=self.root/'external'
  subprocess.run(['git','clone','-q',str(self.repo),str(external)],check=True)
  subprocess.run(['git','-C',str(external),'pack-refs','--all'],check=True)
  subprocess.run(['git','-C',str(external),'worktree','add','--quiet','--detach',str(checkout)],check=True)
  self.assertTrue((checkout/'.git').is_file())
  source=self.worker.discover()[0];self.worker.check_status(source)
  self.assertEqual('current',source['sourceRevisionState'])
  self.assertEqual(self.c,source['sourceHead'])
 def test_read_only_prepare_does_not_create_runtime_state(self):
  app=self.root/'untouched';app.mkdir();w.Worker(app,read_only=True)
  self.assertEqual([],list(app.iterdir()))
 def test_offline_preserves_last_check_and_bytes(self):
  self.worker.check_status(self.source); checked=self.source['checkedAt']; before=w.file_hashes(self.target,True)
  self.repo.rename(self.root/'offline'); self.worker.check_status(self.source)
  self.assertEqual('unknown',self.source['revisionState']); self.assertEqual(checked,self.source['checkedAt']); self.assertEqual(before,w.file_hashes(self.target,True))
 def test_legacy_and_modified_are_unknown(self):
  (self.target/'.git-build-manifest.json').unlink(); self.worker.check_status(self.source); self.assertEqual('unknown',self.source['revisionState'])
  self.installed(self.a); (self.target/'Fixture.dll').write_bytes(b'MZpersonal'); self.worker.check_status(self.source); self.assertEqual('unknown',self.source['revisionState'])
 def test_configuration_does_not_invalidate_provenance(self):
  (self.target/'config/settings.txt').write_text('new personal'); self.worker.check_status(self.source); self.assertEqual('behind',self.source['revisionState'])
 def test_private_failure_is_unknown_and_does_not_leak(self):
  self.source['auth']='desktop-gh'
  with mock.patch.object(self.worker,'git_output',side_effect=w.InstallError('Repository unavailable.')):
   self.worker.check_status(self.source)
  self.assertEqual('unknown',self.source['revisionState'])

if __name__=='__main__':unittest.main()
