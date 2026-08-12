"""Bounded local-file exchange used by the GameHelper2 DevBridge MCP."""

from __future__ import annotations

import json
import os
import re
import secrets
import stat
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOTS = frozenset({"overview", "player", "current-area", "ui"})
REQUEST_ID = re.compile(r"[A-Za-z0-9_-]{1,64}\Z")


class BridgeStore:
    """Access only the fixed bridge exchange files below one plugin directory."""

    max_file_bytes = 1_048_576

    def __init__(self, plugin_directory: Path):
        candidate = plugin_directory.expanduser().absolute()
        if candidate.is_symlink():
            raise ValueError("DevBridge root must not be a symbolic link.")
        self.root = candidate

    def _validate_root(self) -> None:
        if self.root.is_symlink() or (self.root.exists() and not self.root.is_dir()):
            raise ValueError("DevBridge root must be a real directory.")

    def _ensure_root(self) -> None:
        self._validate_root()
        self.root.mkdir(parents=True, exist_ok=True)
        self._validate_root()

    def _open_root(self) -> int:
        flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
        try:
            return os.open(self.root, flags)
        except OSError as error:
            raise ValueError("DevBridge root is unavailable or unsafe.") from error

    def _path(self, filename: str) -> Path:
        self._validate_root()
        if Path(filename).name != filename or filename not in {
            "runtime-status.json", "game-snapshot.json", "snapshot-request.json"
        }:
            raise ValueError("File is not an allowed DevBridge exchange file.")
        path = self.root / filename
        if path.is_symlink():
            raise ValueError("Exchange files must not be symbolic links.")
        if path.parent != self.root:
            raise ValueError("Exchange path escapes the DevBridge plugin directory.")
        return path

    def read_json(self, filename: str) -> dict[str, Any]:
        path = self._path(filename)
        directory = self._open_root()
        try:
            flags = os.O_RDONLY | os.O_NONBLOCK | getattr(os, "O_NOFOLLOW", 0)
            try:
                descriptor = os.open(path.name, flags, dir_fd=directory)
            except OSError as error:
                raise ValueError(f"Exchange file does not exist or is unsafe: {filename}") from error
            metadata = os.fstat(descriptor)
            if not stat.S_ISREG(metadata.st_mode):
                os.close(descriptor)
                raise ValueError("Exchange file must be a regular file.")
            with os.fdopen(descriptor, "r", encoding="utf-8") as stream:
                payload = stream.read(self.max_file_bytes + 1)
        finally:
            os.close(directory)
        if len(payload.encode("utf-8")) > self.max_file_bytes:
            raise ValueError(f"Exchange file exceeds {self.max_file_bytes} bytes.")
        value = json.loads(payload)
        if not isinstance(value, dict):
            raise ValueError("Exchange file must contain a JSON object.")
        return value

    def read_status(self) -> dict[str, Any]:
        directory = self._open_root()
        try:
            try:
                metadata = os.stat("runtime-status.json", dir_fd=directory, follow_symlinks=False)
            except FileNotFoundError:
                return {"exists": False, "modifiedAt": None, "content": None}
        finally:
            os.close(directory)
        if not stat.S_ISREG(metadata.st_mode):
            raise ValueError("Runtime status must be a regular file.")
        content = self.read_json("runtime-status.json")
        return {
            "exists": True,
            "modifiedAt": datetime.fromtimestamp(metadata.st_mtime, timezone.utc).isoformat(),
            "content": content,
        }

    def request_snapshot(self, root: str, request_id: str) -> dict[str, Any]:
        if root not in ROOTS:
            raise ValueError(f"root must be one of: {', '.join(sorted(ROOTS))}")
        if not REQUEST_ID.fullmatch(request_id):
            raise ValueError("request_id must be 1-64 ASCII letters, numbers, '_' or '-'.")
        request = {"schemaVersion": 1, "requestId": request_id, "root": root}
        target = self._path("snapshot-request.json")
        self._ensure_root()
        payload = json.dumps(request, separators=(",", ":")) + "\n"
        directory = self._open_root()
        temporary_name = f".{target.name}.{secrets.token_hex(16)}.tmp"
        try:
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
            descriptor = os.open(temporary_name, flags, 0o600, dir_fd=directory)
            with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
            try:
                os.link(
                    temporary_name,
                    target.name,
                    src_dir_fd=directory,
                    dst_dir_fd=directory,
                    follow_symlinks=False,
                )
            except FileExistsError as error:
                raise ValueError("A snapshot request is already pending.") from error
            os.fsync(directory)
        finally:
            try:
                os.unlink(temporary_name, dir_fd=directory)
            except FileNotFoundError:
                pass
            os.close(directory)
        return request

    def read_snapshot(self, request_id: str, root: str) -> dict[str, Any]:
        if not REQUEST_ID.fullmatch(request_id) or root not in ROOTS:
            raise ValueError("A valid request_id and allowlisted root are required.")
        snapshot = self.read_json("game-snapshot.json")
        if snapshot.get("schemaVersion") != 1 or snapshot.get("requestId") != request_id or snapshot.get("root") != root:
            raise ValueError("Latest snapshot does not match the requested request_id and root.")
        return snapshot

    def read_snapshot_path(self, request_id: str, root: str, path: str) -> dict[str, Any]:
        if not path or len(path) > 256:
            raise ValueError("path must contain 1-256 characters.")
        value: Any = self.read_snapshot(request_id, root)
        for segment in path.split("."):
            if not segment or not isinstance(value, dict) or segment not in value:
                raise ValueError(f"Path is not present in the latest snapshot: {path}")
            value = value[segment]
        return {"path": path, "value": value}

    def inspect_snapshot(self, request_id: str, root: str, max_entries: int = 200, query: str = "") -> dict[str, Any]:
        if not 1 <= max_entries <= 2_000:
            raise ValueError("max_entries must be between 1 and 2000.")
        if len(query) > 128:
            raise ValueError("query must be at most 128 characters.")
        entries: list[dict[str, Any]] = []
        needle = query.casefold()

        def visit(value: Any, path: str, depth: int) -> None:
            if len(entries) >= max_entries or depth > 16:
                return
            kind = "object" if isinstance(value, dict) else "list" if isinstance(value, list) else type(value).__name__
            if not needle or needle in path.casefold():
                entry = {"path": path, "kind": kind}
                if not isinstance(value, (dict, list)):
                    entry["value"] = str(value)[:160]
                entries.append(entry)
            if isinstance(value, dict):
                for key, child in list(value.items())[:200]:
                    visit(child, f"{path}.{key}" if path else key, depth + 1)
            elif isinstance(value, list):
                for index, child in enumerate(value[:200]):
                    visit(child, f"{path}.{index}", depth + 1)

        visit(self.read_snapshot(request_id, root), "", 0)
        return {"entries": entries, "truncated": len(entries) >= max_entries}
