#!/usr/bin/env python3
"""Real local Git + compiler pipeline. Never downloads or executes third-party code."""
from unittest import mock
import importlib.util, json, pathlib, tempfile, subprocess, unittest, os, sys, time
V0, V1, V2 = '0'*40, '1'*40, '2'*40
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
   git('add','.'); git('commit','-qm',V1)
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
   (repo/'Plugin.cs').write_text('namespace GameHelper.Plugin { public class PCore<T> {} } public sealed class Fixture : GameHelper.Plugin.PCore<int> { public const int Version = 2; }'); git('add','.'); git('commit','-qm',V2)
   updated=worker.install(str(repo),True); self.assertNotEqual(source['version'],updated['version']); self.assertEqual(old,(target/'Fixture.dll').read_bytes())
   self.assertEqual('1:1', reload(probe))  # old ALC remains strongly retained
   self.assertEqual(old,(target/'Fixture.dll').read_bytes())
   close_probe(probe)  # startup activation is only legal after host exit
   with mock.patch.object(w,'exchange',side_effect=w.InstallError('fixture activation failure')), self.assertRaises(w.InstallError): worker.activate()
   self.assertEqual(old,(target/'Fixture.dll').read_bytes())
   worker.activate(); self.assertNotEqual(old,(target/'Fixture.dll').read_bytes()); self.assertEqual('keep',(target/'config/settings.json').read_text())
   restarted=root/'restarted'; restarted.mkdir()
   after=reload_probe(restarted,app)
   try: self.assertEqual('1:2',reload(after))
   finally: close_probe(after)
   good=(target/'Fixture.dll').read_bytes()
   self.assertTrue(any((worker.home/'backups').rglob('Fixture.dll')))
   worker.sources[0]['update']=False; worker.save_state(worker.sources[0]); worker.save()
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
 def test_timeout(self):
  with tempfile.TemporaryDirectory() as tmp:
   worker=w.Worker(tmp); start=time.monotonic()
   with self.assertRaises(w.InstallError): worker.run([sys.executable,'-c','import time; time.sleep(60)'],timeout=0.1)
   self.assertLess(time.monotonic()-start,3); self.assertIsNone(worker.child)

if __name__=='__main__': unittest.main()
