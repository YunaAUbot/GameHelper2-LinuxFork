#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN="$ROOT/Plugins/HealthBars/HealthBars.cs"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

python3 - "$PLUGIN" <<'PY'
import re
import sys
from pathlib import Path

source = Path(sys.argv[1]).read_text()
if 'GAMEHELPER2_OVERLAY_BACKEND' not in source or 'native-gpu' not in source:
    raise SystemExit('FAIL: HealthBars has no native backend rendering path')
if source.count('if (this.nativeGpu)') < 3:
    raise SystemExit('FAIL: not all HealthBars fill types branch for native GPU')
if source.count('ptr.AddRectFilled(start,') < 4:
    raise SystemExit('FAIL: native health, mana, and ES fills are not all untextured geometry')
for name in ('healthEnd', 'manaEnd', 'esEnd'):
    pattern = rf'if\s*\(this\.nativeGpu\).*?AddRectFilled\(start,\s*{name}.*?else.*?AddImage\([^;]*{name}'
    if not re.search(pattern, source, re.S):
        raise SystemExit(f'FAIL: {name} does not keep paired native-geometry and Windows-texture paths')
PY

printf 'PASS: HealthBars uses coloured geometry on native GPU and keeps Windows textures\n'
