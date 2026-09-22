#!/usr/bin/env python3
"""Generate a build solution and package manifest from plugin projects."""
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


def discover(repo):
    projects = []
    names = set()
    for directory, dirs, files in os.walk(repo / 'Plugins', followlinks=False):
        dirs[:] = sorted(d for d in dirs if not (
            d.lower() in ('bin', 'obj', 'sampleplugintemplate')
            or d.startswith('.') or re.search(r'(^|[._-])tests?$', d, re.I)
            or (Path(directory) / d).is_symlink()))
        for filename in sorted(files):
            path = Path(directory) / filename
            if path.suffix != '.csproj' or re.search(r'(^|[._-])tests?$', path.stem, re.I):
                continue
            root = ET.parse(path).getroot()
            if any(element.tag.rsplit('}', 1)[-1] == 'IsTestProject'
                   and (element.text or '').strip().lower() == 'true'
                   or element.tag.rsplit('}', 1)[-1] == 'PackageReference'
                   and element.get('Include', '').lower() == 'microsoft.net.test.sdk'
                   for element in root.iter()):
                continue
            name = path.stem
            if not re.fullmatch(r'[A-Za-z][A-Za-z0-9_.-]*', name):
                raise ValueError(f'Unsupported plugin project name: {path}')
            if name.lower() in names:
                raise ValueError(f'Duplicate plugin project name: {name}')
            names.add(name.lower())
            projects.append((name, path.resolve()))
    return sorted(projects)


def main():
    repo, solution, manifest = map(Path, sys.argv[1:])
    projects = discover(repo.resolve())
    if not projects:
        raise ValueError('No plugin projects found')
    root = ET.Element('Solution')
    # Include shared projects explicitly so solution builds retain Release mapping.
    for core in ('GameHelper/GameHelper.csproj', 'GameOffsets/GameOffsets.csproj',
                 'renderer-src/ClickableTransparentOverlay/ClickableTransparentOverlay/ClickableTransparentOverlay.csproj'):
        ET.SubElement(root, 'Project', Path=str((repo / core).resolve()))
    for _, path in projects:
        ET.SubElement(root, 'Project', Path=str(path))
    ET.indent(root)
    ET.ElementTree(root).write(solution, encoding='unicode')
    manifest.write_text(''.join(name + '\n' for name, _ in projects))


if __name__ == '__main__':
    main()
