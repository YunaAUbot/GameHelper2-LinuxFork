"""stdio MCP exposing only bounded DevBridge snapshot operations."""

from __future__ import annotations

import json
import os
from pathlib import Path

from mcp.server.fastmcp import FastMCP

from .core import BridgeStore

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_PLUGIN_DIR = Path(os.environ.get(
    "GH2_DEVBRIDGE_DIR",
    str(REPOSITORY_ROOT / "dist" / "GameHelper2-linux" / "Plugins" / "DevBridge"),
))
store = BridgeStore(DEFAULT_PLUGIN_DIR)
mcp = FastMCP("GameHelper2 DevBridge", instructions="Read-only PoE2 runtime snapshots; no game automation or memory addresses.")


@mcp.tool()
def read_runtime_status() -> str:
    """Read the bridge heartbeat/status JSON."""
    return json.dumps(store.read_status(), indent=2)


@mcp.tool()
def request_snapshot(root: str, request_id: str) -> str:
    """Request one allowlisted snapshot root from the in-process plugin."""
    return json.dumps(store.request_snapshot(root, request_id), indent=2)


@mcp.tool()
def read_snapshot(request_id: str, root: str) -> str:
    """Read the latest bounded snapshot."""
    return json.dumps(store.read_snapshot(request_id, root), indent=2)


@mcp.tool()
def read_snapshot_path(request_id: str, root: str, path: str) -> str:
    """Read one dotted path from the latest snapshot."""
    return json.dumps(store.read_snapshot_path(request_id, root, path), indent=2)


@mcp.tool()
def inspect_snapshot(request_id: str, root: str, max_entries: int = 200, query: str = "") -> str:
    """Inspect a bounded flattened index of the latest snapshot."""
    return json.dumps(store.inspect_snapshot(request_id, root, max_entries, query), indent=2)


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
