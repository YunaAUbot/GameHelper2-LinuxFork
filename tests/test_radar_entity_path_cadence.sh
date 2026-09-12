#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
python3 - "$ROOT/Plugins/Radar/Radar.cs" <<'PY'
from pathlib import Path
import sys
src=Path(sys.argv[1]).read_text()
draw=src[src.index('public override void DrawUI()'):src.index('public override void OnDisable()')]
if 'this.CollectEntityPaths();' in draw:
    raise SystemExit('FAIL: Radar rescans every awake entity in every DrawUI frame')
rebuild=src[src.index('private void RebuildEntityPaths()'):src.index('private void DrawEntityPaths(')]
positions=[
    rebuild.index('now < this.nextEntityRecomputeTime'),
    rebuild.index('this.entityWork.IsBusy || this.tileWork.IsBusy'),
    rebuild.index('this.CollectEntityPaths();'),
    rebuild.index('this.entityPathSnapshot.Count == 0'),
]
if positions != sorted(positions):
    raise SystemExit('FAIL: Radar entity snapshot is not collected after cadence/task gates')
print('PASS: Radar entity-path snapshot scans are bounded by the recompute cadence')
PY
