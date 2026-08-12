# GameHelper2 DevBridge

DevBridge is a minimal read-only plugin. It writes `runtime-status.json` and, on request,
`game-snapshot.json` beside `DevBridge.dll`. The local MCP writes one atomic
`snapshot-request.json` in the same directory. Supported roots are `overview`, `player`,
`current-area`, and `ui`.

The snapshots contain selected values already exposed by `GameHelper.Core`. They do not
contain memory addresses, general entity listings, game mutation, input, process control,
networking, trade automation, or code evaluation.

Install the MCP package from the repository root, then point it at the packaged plugin:

```bash
uv tool install --editable .
GH2_DEVBRIDGE_DIR="$PWD/dist/GameHelper2-linux/Plugins/DevBridge" gamehelper2-devbridge
```

The MCP exposes `read_runtime_status`, `request_snapshot`, `read_snapshot`,
`read_snapshot_path`, and `inspect_snapshot`. Enable DevBridge in GameHelper2 before
requesting a snapshot.
