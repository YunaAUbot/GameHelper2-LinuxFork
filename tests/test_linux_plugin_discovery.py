import importlib.util
from pathlib import Path
import tempfile
import unittest
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('discovery', ROOT / 'scripts/discover-linux-plugins.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DiscoveryTests(unittest.TestCase):
    def test_new_plugins_are_included_and_non_plugins_excluded(self):
        with tempfile.TemporaryDirectory(prefix='plugin discovery ') as temp:
            repo = Path(temp)
            for name in ('FreshPlugin/FreshPlugin', 'AutoHotKeyTrigger/AutoHotKeyTrigger',
                         'PickupHelper/PickupHelper', 'FreshPlugin/FreshPlugin.Tests/Test',
                         'SamplePluginTemplate/Changeme/Changeme', 'FreshPlugin/obj/Stale',
                         'FreshPlugin/bin/Stale', 'Other/Other.Tests'):
                p = repo / 'Plugins' / (name + '.csproj')
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text('<Project />')
            p = repo / 'Plugins/UnusualChecks/Checks.csproj'
            p.parent.mkdir(); p.write_text('<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>')
            (repo / 'Plugins/Linked').symlink_to(repo / 'Plugins/FreshPlugin', target_is_directory=True)
            solution = repo / 'generated.slnx'; manifest = repo / 'plugins.txt'
            subprocess.run(['python3', str(ROOT / 'scripts/discover-linux-plugins.py'), str(repo), str(solution), str(manifest)], check=True)
            self.assertEqual(manifest.read_text().splitlines(), ['AutoHotKeyTrigger', 'FreshPlugin', 'PickupHelper'])
            paths = [p.attrib['Path'] for p in ET.parse(solution).getroot()]
            self.assertIn(str(repo / 'GameHelper/GameHelper.csproj'), paths)
            self.assertIn(str(repo / 'Plugins/FreshPlugin/FreshPlugin.csproj'), paths)

    def test_duplicate_names_fail_instead_of_overwriting(self):
        with tempfile.TemporaryDirectory() as temp:
            repo = Path(temp)
            for name in ('A', 'B'):
                p = repo / 'Plugins' / name / 'Same.csproj'
                p.parent.mkdir(parents=True); p.write_text('<Project />')
            with self.assertRaisesRegex(ValueError, 'Duplicate'):
                module.discover(repo)


if __name__ == '__main__':
    unittest.main()
