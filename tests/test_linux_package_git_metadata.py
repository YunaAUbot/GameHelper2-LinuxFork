"""Exercise the real package script with real checkouts and stubbed compilers."""
import os, pathlib, shutil, subprocess, tempfile, unittest
ROOT=pathlib.Path(__file__).resolve().parents[1]
META=('.git-source','.git-plugin.json','.git-status.json','.git-build-manifest.json','.git-source-owner.json')
META+=tuple(str(pathlib.Path(name).with_suffix('.tmp')) for name in META[1:])
class PackageGitMetadata(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
  self.root=pathlib.Path(self.tmp.name); self.repo=self.root/'host'; self.repo.mkdir()
  shutil.copy2(ROOT/'build-linux-package.sh',self.repo)
  shutil.copy2(ROOT/'.gitignore',self.repo)
  for name in ('scripts/plugin-assembly-check/PluginAssemblyCheck.csproj','scripts/plugin-assembly-check/Program.cs','scripts/steam-proton-env.sh','scripts/git-plugin-worker.py','run-gamehelper2-linux.sh','README-LINUX.md','README.md','renderer-src/ClickableTransparentOverlay/LICENSE'):
   p=self.repo/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text('fixture')
  self.plugins=self.repo/'GameHelper/bin/Release/net10.0-windows/win-x64/Plugins'
  for name in ('PreloadAlert','Radar','HealthBars','Atlas2','PlayerBuffBar','LootValue','NinjaPricer'):
   p=self.plugins/name;p.mkdir(parents=True);(p/(name+'.dll')).write_bytes(b'MZfixture')
  self.fixture=self.root/'private'; self.fixture.mkdir(); self.git(self.fixture,'init','-q')
  (self.fixture/'PRIVATE-SOURCE.cs').write_text('private fixture source')
  self.git(self.fixture,'add','.');self.git(self.fixture,'-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','-qm','fixture')
  self.git(self.repo,'init','-q')  # Legitimate host source metadata must remain allowed.
  self.bin=self.root/'bin';self.bin.mkdir()
  for name,body in {'dotnet':'if [ "$1" = publish ]; then\n for arg; do dest="$arg"; done\n mkdir -p "$dest"\n printf MZ > "$dest/GameHelper.dll"\nfi', 'pkg-config':'exit 0','cc':'for arg; do dest="$arg"; done\nprintf native > "$dest"'}.items():
   p=self.bin/name;p.write_text('#!/bin/sh\nset -eu\n'+body+'\n');p.chmod(0o755)
  self.env=dict(os.environ,PATH=str(self.bin)+':'+os.environ['PATH'],DOTNET=str(self.bin/'dotnet'))
  self.output=self.root/'package'
 def git(self,repo,*args):
  return subprocess.run(['git','-C',str(repo),*args],check=True,capture_output=True,text=True)
 def package(self):
  return subprocess.run([str(self.repo/'build-linux-package.sh'),str(self.output)],env=self.env,capture_output=True,text=True)
 def test_embedded_checkout_stripped_with_and_without_reflogs(self):
  target=self.plugins/'Radar'
  for logs in (True,False):
   with self.subTest(reflogs=logs):
    checkout=target/'.git-source';shutil.rmtree(checkout,ignore_errors=True)
    self.git(self.repo,'clone','-q',str(self.fixture),str(checkout))
    self.git(checkout,'config','fixture.private','private fixture config')
    if not logs: shutil.rmtree(checkout/'.git/logs')
    for name in META[1:]: (target/name).write_text('private fixture state')
    (target/'config').mkdir(exist_ok=True);(target/'config/settings.json').write_text('private settings')
    result=self.package();self.assertEqual(0,result.returncode,result.stderr)
    self.assertTrue((self.repo/'.git').is_dir())
    for p in self.output.rglob('*'):
     self.assertNotIn(p.name,(*META,'.git','PRIVATE-SOURCE.cs','settings.json'))
     if p.is_file(): self.assertNotIn(b'private fixture',p.read_bytes())
 def test_direct_plugin_checkout_rejected_without_relying_on_reflogs(self):
  target=self.plugins/'Radar'
  shutil.copytree(self.fixture,target,dirs_exist_ok=True)
  for logs in (True,False):
   with self.subTest(reflogs=logs):
    if not logs: shutil.rmtree(target/'.git/logs')
    result=self.package();self.assertNotEqual(0,result.returncode,'direct private plugin source packaged')
    self.assertFalse(self.output.exists())
 def test_plugin_metadata_is_ignored_but_host_sources_are_not(self):
  for name in META:
   path='Plugins/Fixture/'+name+('/PRIVATE-SOURCE.cs' if name=='.git-source' else '')
   result=subprocess.run(['git','-C',str(self.repo),'check-ignore','--no-index',path],capture_output=True)
   self.assertEqual(0,result.returncode,path)
  result=subprocess.run(['git','-C',str(self.repo),'check-ignore','--no-index','GameHelper/Core.cs'],capture_output=True)
  self.assertEqual(1,result.returncode)
if __name__=='__main__': unittest.main()
