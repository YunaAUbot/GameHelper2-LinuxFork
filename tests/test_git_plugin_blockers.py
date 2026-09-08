#!/usr/bin/env python3
"""Regression/integration checks using the installed self-contained host and local Git."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / 'dist/GameHelper2-linux'
SOURCE = Path(os.environ['GH2_CAMPAIGN_SOURCE']) if os.environ.get('GH2_CAMPAIGN_SOURCE') else None
DOTNET = os.environ.get('DOTNET', 'dotnet')
spec = importlib.util.spec_from_file_location('worker', ROOT / 'scripts/git-plugin-worker.py')
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)

class RecordingWorker(w.Worker):
    def run(self, args, cwd=None, timeout=180, capture=False):
        if len(args) > 2 and args[1] == 'build':
            self.references = [Path(r.findtext('HintPath')).name for r in ET.parse(args[2]).findall('.//Reference')]
        return super().run(args, cwd, timeout, capture)

class Blockers(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / 'app'
        self.app.mkdir()

    def host(self):
        self.assertTrue((HOST / 'System.Private.CoreLib.dll').is_file(), 'Build the self-contained package first')
        for file in HOST.iterdir():
            if file.is_file() and file.suffix in ('.dll', '.json'):
                shutil.copy2(file, self.app / file.name)

    def fixture(self, nuget=False):
        repo = self.root / 'repo'
        repo.mkdir()
        package = '<ItemGroup><PackageReference Include="System.Linq.Dynamic.Core" Version="1.6.0" /></ItemGroup>' if nuget else ''
        (repo / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>' + package + '</Project>')
        # The dependency check is independent of the installed-host reference regression.
        (repo / 'Plugin.cs').write_text('namespace GameHelper.Plugin { public class PCore<T> {} } public sealed class Fixture : GameHelper.Plugin.PCore<int> {}' + (' public static class Probe { public static int Run() => System.Linq.Dynamic.Core.DynamicExpressionParser.ParseLambda(new System.Linq.Expressions.ParameterExpression[0], typeof(int), "1 + 2").Compile().DynamicInvoke() is int n ? n : -1; }' if nuget else ''))
        subprocess.run(['git', 'init', '-q', str(repo)], check=True)
        self.commit(repo)
        return repo

    def commit(self, repo):
        subprocess.run(['git', '-C', str(repo), 'add', '.'], check=True)
        subprocess.run(['git', '-C', str(repo), '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-qm', 'fixture'], check=True)

    @unittest.skipUnless(SOURCE, 'Set GH2_CAMPAIGN_SOURCE to an inspected public CampaignHelper checkout')
    def test_published_campaign_against_self_contained_host(self):
        self.host()
        self.assertEqual('', subprocess.check_output(['git', '-C', str(SOURCE), 'status', '--porcelain'], text=True))
        worker = RecordingWorker(self.app, allow_local=True)
        record = worker.install(str(SOURCE), True)
        self.assertEqual(subprocess.check_output(['git', '-C', str(SOURCE), 'rev-parse', 'HEAD'], text=True).strip(), record['version'])
        manifest = json.loads((HOST / 'GameHelper.deps.json').read_text())
        framework = {Path(asset).name for entries in manifest['targets'].values() for name, entry in entries.items() if name.startswith('runtimepack.') for kind in ('runtime', 'native') for asset in entry.get(kind, {})}
        self.assertFalse(set(worker.references) & framework)
        self.assertTrue({'GameHelper.dll', 'GameOffsets.dll', 'ImGui.NET.dll', 'Newtonsoft.Json.dll', 'Coroutine.dll'} <= set(worker.references))
        staged = self.app / 'Plugins' / record['name']
        self.assertTrue((staged / 'CampaignHelper.dll').is_file())
        for asset in (SOURCE / 'CampaignHelper/Data').iterdir():
            self.assertEqual(asset.read_bytes(), (staged / 'Data' / asset.name).read_bytes())
        self.assertFalse({p.name for p in staged.glob('*.dll')} & {p.name for p in HOST.glob('*.dll')})
        worker.activate()
        self.assertIn('Reload all plugins', record['status'])
        artifacts = os.environ.get('GH2_BLOCKER_ARTIFACTS')
        if artifacts:
            dest = Path(artifacts); dest.mkdir(parents=True, exist_ok=True)
            shutil.copytree(self.app / 'Plugins/CampaignHelper', dest / 'CampaignHelper', dirs_exist_ok=True)
            (dest / 'campaign-proof.json').write_text(json.dumps({'source': str(SOURCE), 'commit': record['version'], 'host': str(HOST), 'host_sha256': hashlib.sha256((HOST / 'GameHelper.dll').read_bytes()).hexdigest(), 'references': sorted(worker.references), 'excluded_framework_count': len(framework), 'status': record['status']}, indent=2))

    def test_nuget_dependency_is_staged_and_invocable(self):
        worker = w.Worker(self.app, allow_local=True)
        record = worker.install(str(self.fixture(nuget=True)), True)
        staged = self.app / 'Plugins' / record['name']
        self.assertTrue((staged / 'System.Linq.Dynamic.Core.dll').is_file())
        worker.activate()
        target = self.app / 'Plugins/Fixture'
        probe = self.root / 'probe'; probe.mkdir()
        (probe / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
        (probe / 'Program.cs').write_text('using System; using System.IO; using System.Runtime.Loader; var dir = args[0]; AssemblyLoadContext.Default.Resolving += (c,n) => c.LoadFromAssemblyPath(Path.Combine(dir,n.Name+".dll")); var a = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir,"Fixture.dll")); var result = a.GetType("Probe").GetMethod("Run").Invoke(null,null); Console.WriteLine(result); return (int)result == 3 ? 0 : 1;')
        result = subprocess.run([DOTNET, 'run', '--project', str(probe), '-c', 'Release', '--', str(target)], check=True, capture_output=True, text=True)
        self.assertEqual('3', result.stdout.strip().splitlines()[-1])

    def test_root_configuration_survives_without_obsolete_assets(self):
        repo = self.fixture()
        (repo / 'settings.json').write_text('default v1')
        (repo / 'Fixture.csproj').write_text((repo / 'Fixture.csproj').read_text().replace('</Project>', '<ItemGroup><None Update="settings.json" CopyToOutputDirectory="Always" /></ItemGroup></Project>'))
        # Obsolete assets must originate in the v1 build. Injecting extra DLLs
        # after installation now correctly invalidates artifact provenance.
        obsolete = ('old.deps.json', 'old.runtimeconfig.json', 'old.py', 'old.dll', 'old.png', 'old.json', 'Data/old.json')
        for name in obsolete:
            asset = repo / name; asset.parent.mkdir(exist_ok=True); asset.write_text('obsolete')
        project = repo / 'Fixture.csproj'
        project.write_text(project.read_text().replace('</Project>', '<ItemGroup><None Update="old.*;Data/old.json" CopyToOutputDirectory="Always" /></ItemGroup></Project>'))
        self.commit(repo)
        worker = w.Worker(self.app, allow_local=True)
        record = worker.install(str(repo), True); worker.activate()
        target = self.app / 'Plugins/Fixture'
        for name in ('settings.json', 'config.json', 'preferences.ini'):
            (target / name).write_text('custom')
        for name in obsolete:
            self.assertTrue((target / name).is_file())
            (repo / name).unlink()
        (target / 'config').mkdir(); (target / 'config/custom.json').write_text('custom')
        (repo / 'settings.json').write_text('default v2'); self.commit(repo)
        worker.install(str(repo), True); worker.activate()
        self.assertEqual('custom', (target / 'settings.json').read_text())
        self.assertEqual('custom', (target / 'config.json').read_text())
        self.assertEqual('custom', (target / 'preferences.ini').read_text())
        self.assertEqual('custom', (target / 'config/custom.json').read_text())
        for name in ('old.deps.json', 'old.runtimeconfig.json', 'old.py', 'old.dll', 'old.png', 'old.json', 'Data/old.json'):
            self.assertFalse((target / name).exists(), name)

if __name__ == '__main__':
    unittest.main()
