#!/usr/bin/env python3
"""Exercise reviewed migration only in temporary runtime fixtures."""
import importlib.util
import json
import pathlib
import subprocess
import sys
from unittest import mock
import tempfile
import unittest

REPOS = {'CampaignHelper': 'campaignhelper2', 'NinjaPricer': 'GameHelper2-NinjaPricer',
         'AngeArbitrage': 'GameHelper2-AngeArbitrage', 'RunecraftHelper': 'RunecraftHelper'}
ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('migration', ROOT/'scripts/prepare-plugin-status-migration.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)

class Migration(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name); self.app = self.root/'runtime'; self.app.mkdir()
        self.payload = self.root/'host'; self.payload.mkdir(); self.plan = self.root/'review'
        for relative in m.HOST_FILES:
            for base, content in ((self.app, 'old'), (self.payload, 'new')):
                path = base/relative; path.parent.mkdir(exist_ok=True); path.write_text(content)
        for name in (*REPOS, 'LootValue', 'PersonalPlugin'):
            path = self.app/'Plugins'/name; path.mkdir(parents=True)
            (path/(name+'.dll')).write_text('personal binary')
            (path/'config').mkdir(); (path/'config/settings.json').write_text('personal settings')
        (self.app/'PluginSources').mkdir()
        registry = [{'id':'unrelated', 'name':'PersonalPlugin', 'url':'https://example.org/a/b'}]
        for name, repo in REPOS.items():
            url='https://github.com/YunaAUbot/'+repo
            registry.append(dict(id=m.hashlib.sha256(url.encode()).hexdigest()[:24],name=name,url=url,trackingOnly=True,update=False,status='updates held'))
        m.w.atomic(self.app/'PluginSources/sources.json', registry)
        self.before = m.w.file_hashes(self.app)
        self.sha = m.prepare(self.app, self.payload, self.plan)
    def test_prepare_apply_rollback_preserves_all_plugins_and_configs(self):
        self.assertEqual(self.before, m.w.file_hashes(self.app))
        m.apply(self.plan, self.sha, True, True)
        for relative, sha in self.before.items():
            if relative not in m.HOST_FILES+m.STATE_FILES:
                self.assertEqual(sha, m.digest(self.app/relative))
        registry = json.loads((self.app/'PluginSources/sources.json').read_text())
        self.assertEqual('unrelated', registry[0]['id'])
        self.assertEqual(self.before['PluginSources/sources.json'],m.digest(self.app/'PluginSources/sources.json'))
        m.apply(self.plan, self.sha, True, True, rollback=True)
        self.assertEqual(self.before, m.w.file_hashes(self.app))
    def test_existing_mappings_auth_and_startup_preferences_are_preserved(self):
        registry=json.loads((self.app/'PluginSources/sources.json').read_text())
        registry[1].update(update=True,auth='desktop-gh')
        m.w.atomic(self.app/'PluginSources/sources.json',registry)
        keys={'https://example.org/a/b': {'nonsecret': 'existing mapping'}}
        m.w.atomic(self.app/'PluginSources/deploy-keys.json',keys)
        plan=self.root/'preserve';m.prepare(self.app,self.payload,plan)
        self.assertFalse((plan/'payload/PluginSources').exists())
        sha=m.digest(plan/'plan.json');m.apply(plan,sha,True,True)
        self.assertEqual(registry,json.loads((self.app/'PluginSources/sources.json').read_text()))
        self.assertEqual(keys,json.loads((self.app/'PluginSources/deploy-keys.json').read_text()))
        worker=m.w.Worker(self.app,read_only=True)
        self.assertTrue(all(s['revisionState']=='local' for s in worker.sources))

    def test_review_and_changed_runtime_refused(self):
        for sha, closed, reconciled in (('wrong',True,True),(self.sha,False,True),(self.sha,True,False)):
            with self.assertRaises(m.w.InstallError): m.apply(self.plan,sha,closed,reconciled)
        (self.app/'Plugins/AngeArbitrage/AngeArbitrage.dll').write_text('newer personal work')
        before = m.w.file_hashes(self.app)
        with self.assertRaises(m.w.InstallError): m.apply(self.plan,self.sha,True,True)
        self.assertEqual(before,m.w.file_hashes(self.app))
    def test_interrupted_apply_can_rollback_only_known_bytes(self):
        import shutil
        shutil.copy2(self.plan/'payload/GameHelper.dll', self.app/'GameHelper.dll')
        m.apply(self.plan, self.sha, True, True, rollback=True)
        self.assertEqual(self.before, m.w.file_hashes(self.app))
    def assert_restored(self):
        self.assertEqual(self.before, m.w.file_hashes(self.app))
        self.assertEqual(self.before, m.w.file_hashes(self.plan/'backup'))
        plan = json.loads((self.plan/'plan.json').read_text())
        self.assertEqual(plan['payload'], m.w.file_hashes(self.plan/'payload'))
        self.assertEqual(self.sha, m.digest(self.plan/'plan.json'))
        self.assertFalse((self.plan/'copy-in-progress.json').exists())

    def interrupt_copy(self, folder='payload', relative='GameHelper.pdb'):
        code = f"""
import importlib.util, os, pathlib
spec = importlib.util.spec_from_file_location('migration', {str(ROOT/'scripts/prepare-plugin-status-migration.py')!r})
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
original = m.shutil.copy2
def interrupt(source, target, *args, **kwargs):
    if pathlib.Path(source) == pathlib.Path({str(self.plan/folder/relative)!r}):
        with open(target, 'wb') as stream:
            stream.write(pathlib.Path(source).read_bytes()[:1])
            stream.flush(); os.fsync(stream.fileno())
        os._exit(73)
    return original(source, target, *args, **kwargs)
m.shutil.copy2 = interrupt
m.apply(pathlib.Path({str(self.plan)!r}), {self.sha!r}, True, True, rollback={folder == 'backup'})
"""
        result = subprocess.run([sys.executable, '-c', code], capture_output=True, text=True)
        self.assertEqual(73, result.returncode, result.stderr)
        self.assertTrue((self.app/(relative+'.migration-tmp')).is_file())
        self.assertEqual(self.before, m.w.file_hashes(self.plan/'backup'))
        for name, sha in self.before.items():
            if name not in m.HOST_FILES+m.STATE_FILES:
                self.assertEqual(sha, m.digest(self.app/name))

    def rollback_process(self, success=True):
        result = subprocess.run([sys.executable, str(ROOT/'scripts/prepare-plugin-status-migration.py'),
                                 'rollback', str(self.plan), '--review-sha', self.sha,
                                 '--helper-worker-closed', '--personal-state-reconciled'],
                                capture_output=True, text=True)
        if success: self.assertEqual(0, result.returncode, result.stderr)
        else: self.assertNotEqual(0, result.returncode)

    def test_failed_partial_copy_automatically_restores(self):
        original = m.shutil.copy2
        def fail(source, target, *args, **kwargs):
            if pathlib.Path(source) == self.plan/'payload/README-LINUX.md':
                pathlib.Path(target).write_bytes(pathlib.Path(source).read_bytes()[:1])
                raise OSError('injected partial copy failure')
            return original(source, target, *args, **kwargs)
        with mock.patch.object(m.shutil, 'copy2', side_effect=fail):
            with self.assertRaisesRegex(OSError, 'injected partial copy failure'):
                m.apply(self.plan, self.sha, True, True)
        self.assert_restored()

    def test_interrupted_temp_copy_rolls_back_in_next_process(self):
        self.interrupt_copy()
        self.rollback_process()
        self.assert_restored()

    def test_interrupted_rollback_temp_copy_can_retry(self):
        m.apply(self.plan, self.sha, True, True)
        self.interrupt_copy('backup')
        self.rollback_process()
        self.assert_restored()

    def test_unowned_temp_is_never_deleted(self):
        temp = self.app/'GameHelper.pdb.migration-tmp'
        temp.write_bytes(b'n')  # Even a valid payload prefix is not ownership.
        before = m.w.file_hashes(self.app)
        self.rollback_process(success=False)
        self.assertEqual(before, m.w.file_hashes(self.app))

    def test_owned_temp_unexpected_content_is_never_deleted(self):
        self.interrupt_copy()
        (self.app/'GameHelper.pdb.migration-tmp').write_bytes(b'personal data')
        before = m.w.file_hashes(self.app)
        self.rollback_process(success=False)
        self.assertEqual(before, m.w.file_hashes(self.app))

    def test_owned_temp_replaced_inode_is_never_deleted(self):
        self.interrupt_copy()
        temp = self.app/'GameHelper.pdb.migration-tmp'
        temp.rename(self.root/'original-temp')
        temp.write_bytes(b'n')
        before = m.w.file_hashes(self.app)
        self.rollback_process(success=False)
        self.assertEqual(before, m.w.file_hashes(self.app))

    def test_owned_temp_symlink_is_never_followed_or_deleted(self):
        self.interrupt_copy()
        temp = self.app/'GameHelper.pdb.migration-tmp'
        temp.unlink()
        personal = self.root/'personal'; personal.write_bytes(b'personal data')
        temp.symlink_to(personal)
        self.rollback_process(success=False)
        self.assertTrue(temp.is_symlink())
        self.assertEqual(b'personal data', personal.read_bytes())
        self.assertEqual(self.before, m.w.file_hashes(self.plan/'backup'))

    def test_interrupted_copy_with_newer_config_refuses_all_cleanup(self):
        self.interrupt_copy()
        (self.app/'Plugins/AngeArbitrage/config/settings.json').write_text('newer personal work')
        before = m.w.file_hashes(self.app)
        self.rollback_process(success=False)
        self.assertEqual(before, m.w.file_hashes(self.app))

    def test_failed_automatic_restore_can_rollback_in_next_process(self):
        original = m.shutil.copy2
        def fail(source, target, *args, **kwargs):
            if pathlib.Path(source) in (self.plan/'payload/GameHelper.pdb',
                                        self.plan/'backup/GameHelper.dll'):
                pathlib.Path(target).write_bytes(pathlib.Path(source).read_bytes()[:1])
                raise OSError('injected copy failure')
            return original(source, target, *args, **kwargs)
        with mock.patch.object(m.shutil, 'copy2', side_effect=fail):
            with self.assertRaisesRegex(OSError, 'injected copy failure'):
                m.apply(self.plan, self.sha, True, True)
        self.rollback_process()
        self.assert_restored()

    def test_owned_empty_and_complete_temp_can_rollback(self):
        self.interrupt_copy()
        temp = self.app/'GameHelper.pdb.migration-tmp'
        temp.write_bytes(b'')
        self.rollback_process()
        self.assert_restored()
        self.interrupt_copy()
        temp.write_bytes((self.plan/'payload/GameHelper.pdb').read_bytes())
        self.rollback_process()
        self.assert_restored()

    def test_interruption_after_replace_can_rollback(self):
        self.interrupt_copy()
        (self.app/'GameHelper.pdb.migration-tmp').write_bytes(b'new')
        (self.app/'GameHelper.pdb.migration-tmp').replace(self.app/'GameHelper.pdb')
        self.rollback_process()
        self.assert_restored()

    def test_partial_journal_is_preserved_and_fails_closed(self):
        self.interrupt_copy()
        journal = self.plan/'copy-in-progress.json'
        journal.write_text('{')
        before = m.w.file_hashes(self.app)
        self.rollback_process(success=False)
        self.assertEqual(before, m.w.file_hashes(self.app))
        self.assertEqual('{', journal.read_text())

    def test_captured_user_temp_is_preserved(self):
        temp = self.app/'GameHelper.pdb.migration-tmp'; temp.write_bytes(b'n')
        self.plan = self.root/'new-review'
        self.before = m.w.file_hashes(self.app)
        self.sha = m.prepare(self.app, self.payload, self.plan)
        with self.assertRaises(m.w.InstallError): m.apply(self.plan, self.sha, True, True)
        self.rollback_process(success=False)
        self.assert_restored()

    def test_owned_temp_hardlink_is_preserved(self):
        import os
        self.interrupt_copy()
        temp = self.app/'GameHelper.pdb.migration-tmp'
        os.link(temp, self.root/'personal-link')
        before = m.w.file_hashes(self.app)
        self.rollback_process(success=False)
        self.assertEqual(before, m.w.file_hashes(self.app))
        self.assertEqual(b'n', (self.root/'personal-link').read_bytes())

    def test_tampered_backup_prevents_temp_cleanup(self):
        self.interrupt_copy()
        (self.plan/'backup/GameHelper.dll').write_bytes(b'changed')
        before = m.w.file_hashes(self.app)
        self.rollback_process(success=False)
        self.assertEqual(before, m.w.file_hashes(self.app))

    def test_payload_tamper_refused(self):
        (self.plan/'payload/GameHelper.dll').write_text('unreviewed')
        with self.assertRaises(m.w.InstallError): m.apply(self.plan,self.sha,True,True)
        self.assertEqual(self.before,m.w.file_hashes(self.app))

if __name__ == '__main__': unittest.main()
