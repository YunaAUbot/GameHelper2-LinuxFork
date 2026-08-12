import json
import os
import tempfile
import unittest
from pathlib import Path


class DevBridgeMcpTests(unittest.TestCase):
    def setUp(self):
        from devbridge_mcp.core import BridgeStore

        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.store = BridgeStore(self.root)

    def tearDown(self):
        self.temporary.cleanup()

    def test_status_and_snapshot_reads_are_bounded_and_contained(self):
        (self.root / "runtime-status.json").write_text('{"ready":true}\n', encoding="utf-8")
        self.assertTrue(self.store.read_status()["content"]["ready"])
        with self.assertRaises(ValueError):
            self.store.read_json("../runtime-status.json")
        (self.root / "game-snapshot.json").write_bytes(b"x" * (self.store.max_file_bytes + 1))
        with self.assertRaises(ValueError):
            self.store.read_json("game-snapshot.json")
        outside = self.root.parent / "outside-devbridge.json"
        outside.write_text('{"secret":true}', encoding="utf-8")
        (self.root / "runtime-status.json").unlink()
        os.symlink(outside, self.root / "runtime-status.json")
        with self.assertRaises(ValueError):
            self.store.read_status()

    def test_non_regular_exchange_file_fails_closed(self):
        fifo = self.root / "game-snapshot.json"
        os.mkfifo(fifo)
        with self.assertRaises(ValueError):
            self.store.read_snapshot("one", "overview")

    def test_root_replacement_with_symlink_fails_closed(self):
        outside = self.root / "outside-target"
        outside.mkdir()
        replacement = self.root.with_name(self.root.name + "-original")
        self.root.rename(replacement)
        os.symlink(replacement / "outside-target", self.root)
        with self.assertRaises(ValueError):
            self.store.read_status()
        with self.assertRaises(ValueError):
            self.store.request_snapshot("player", "blocked")
        self.root.unlink()
        replacement.rename(self.root)

    def test_request_is_atomic_bounded_and_allowlisted(self):
        result = self.store.request_snapshot("player", "abc-123")
        request = json.loads((self.root / "snapshot-request.json").read_text(encoding="utf-8"))
        self.assertEqual(request["root"], "player")
        self.assertEqual(request["requestId"], "abc-123")
        self.assertEqual(result["requestId"], "abc-123")
        with self.assertRaises(ValueError):
            self.store.request_snapshot("ui", "second")
        (self.root / "snapshot-request.json").unlink()
        self.assertFalse(any(self.root.glob(".snapshot-request.json.*.tmp")))
        with self.assertRaises(ValueError):
            self.store.request_snapshot("entities", "x")

    def test_snapshot_inspection_and_path_read_are_bounded(self):
        snapshot = {
            "schemaVersion": 1,
            "requestId": "one",
            "root": "overview",
            "data": {"player": {"name": "Ranger", "position": {"x": 1, "y": 2}}},
        }
        (self.root / "game-snapshot.json").write_text(json.dumps(snapshot), encoding="utf-8")
        entries = self.store.inspect_snapshot("one", "overview", max_entries=3)
        self.assertLessEqual(len(entries["entries"]), 3)
        self.assertEqual(self.store.read_snapshot_path("one", "overview", "data.player.name")["value"], "Ranger")
        with self.assertRaises(ValueError):
            self.store.read_snapshot("stale", "overview")
        with self.assertRaises(ValueError):
            self.store.read_snapshot("one", "player")
        with self.assertRaises(ValueError):
            self.store.inspect_snapshot("one", "overview", max_entries=0)
        with self.assertRaises(ValueError):
            self.store.read_snapshot_path("one", "overview", "data.player.missing")


if __name__ == "__main__":
    unittest.main()
