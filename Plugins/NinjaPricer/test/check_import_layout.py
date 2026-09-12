#!/usr/bin/env python3
"""Offline discovery and SDK glob regression; never installs or runs the plugin."""
import ast
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
HOST = Path(os.environ['GAMEHELPER2_HOST_ROOT'])
DOTNET = os.environ.get('DOTNET', 'dotnet')
# The supplied host's current checkout predates its Git importer. Pin reviewed source.
IMPORTER_REV = '3fa782645c681737fe75a4f6fb6c45d74280d5d7'
source = subprocess.check_output(
    ['git', '-C', str(HOST), 'show', f'{IMPORTER_REV}:scripts/git-plugin-worker.py'], text=True)
install = next(n for n in ast.walk(ast.parse(source)) if isinstance(n, ast.FunctionDef) and n.name == 'install')
assignment = next(n for n in ast.walk(install) if isinstance(n, ast.Assign)
                  and any(isinstance(t, ast.Name) and t.id == 'projects' for t in n.targets))
discovery = compile(ast.Expression(assignment.value), '<actual host project discovery>', 'eval')

def discover(repo):
    return sorted(p.relative_to(repo).as_posix() for p in eval(discovery, {'repo': repo}))

assert discover(ROOT) == ['NinjaPricer.csproj'], discover(ROOT)
for path in ROOT.rglob('*.csproj'):
    parts = path.relative_to(ROOT).parts
    if any(part in ('obj', 'bin') for part in parts):
        continue
    assert path == ROOT / 'NinjaPricer.csproj' or parts[0] == 'test', path
project = ET.parse(ROOT / 'NinjaPricer.csproj').getroot()
assert project.findtext('./PropertyGroup/AssemblyName') == 'NinjaPricer'
# Actual importer recognizes and substitutes this host ProjectReference suffix.
assert any(r.get('Include', '').replace('\\', '/').endswith('/GameHelper/GameHelper.csproj')
           for r in project.findall('./ItemGroup/ProjectReference'))

asset_patterns = ['LICENSE', 'README.md']
assets = {p.relative_to(ROOT).as_posix() for pattern in asset_patterns
          for p in ROOT.glob(pattern) if p.is_file()
          and not any(part in ('obj', 'bin') for part in p.relative_to(ROOT).parts)}

with tempfile.TemporaryDirectory(prefix='ninjapricer-import-check-') as temporary:
    repo = Path(temporary)
    shutil.copytree(ROOT, repo, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns('.git', 'bin', 'obj', 'dist', '__pycache__'))
    for folder in ('test/probe', 'test/tools/probe', 'test/obj', 'test/bin',
                   'tools/probe', 'tools/obj', 'tools/bin', 'Domain/obj', 'Domain/bin', 'Data/obj', 'Data/bin', 'icons/obj', 'Localization/bin'):
        path = repo / folder
        path.mkdir(parents=True, exist_ok=True)
        for name in ('MustNotCompile.cs', 'MustNotEmbed.resx', 'MustNotInclude.txt', 'MustNotCopy.json', 'MustNotCopy.png'):
            (path / name).write_text('invalid sentinel; excluded from all default items')
    for folder in ('test/probe', 'test/tools/probe', 'test/obj', 'test/bin', 'obj', 'bin'):
        path = repo / folder
        path.mkdir(parents=True, exist_ok=True)
        (path / 'Probe.csproj').write_text('<Project />')
    assert discover(repo) == ['NinjaPricer.csproj']
    # Guard against reintroducing a standalone tool project outside the test tree.
    extra = repo / 'tools/probe/Probe.csproj'
    extra.write_text('<Project />')
    assert len(discover(repo)) == 2
    extra.unlink()
    output = subprocess.check_output([DOTNET, 'msbuild', str(repo / 'NinjaPricer.csproj'),
        '-p:EnableWindowsTargeting=true', '-getItem:Compile,EmbeddedResource,None,Content',
        '-getProperty:AssemblyName,RootNamespace'], text=True)
    evaluated = json.loads(output)
    assert evaluated['Properties'] == {'AssemblyName': 'NinjaPricer', 'RootNamespace': 'NinjaPricer'}
    for kind, items in evaluated['Items'].items():
        for item in items:
            parts = Path(item['Identity'].replace('\\', '/')).parts
            assert not any(p in ('test', 'tools', 'obj', 'bin') for p in parts), (kind, parts)
    compiled = {i['Identity'].replace('\\', '/') for i in evaluated['Items']['Compile']}
    expected = {p.relative_to(repo).as_posix() for p in repo.rglob('*.cs')
                if not any(part in ('test', 'tools', 'obj', 'bin') for part in p.relative_to(repo).parts)}
    assert compiled == expected, (compiled - expected, expected - compiled)
    copied = {item['Identity'].replace('\\', '/')
              for kind in ('None', 'Content') for item in evaluated['Items'][kind]
              if item.get('CopyToOutputDirectory') == 'PreserveNewest'}
    assert assets <= copied, ('Missing runtime assets', assets - copied)
print(f'Inventory: {len(compiled)} production C# files; {len(assets)} runtime assets; one root production project; auxiliary projects confined to test/.')
print(f'PASS: actual importer {IMPORTER_REV}: one root plugin; test/tool and nested obj/bin isolation; assembly identity.')
print('Offline discovery/MSBuild evaluation only; no GUI import, installation, or runtime validation.')
