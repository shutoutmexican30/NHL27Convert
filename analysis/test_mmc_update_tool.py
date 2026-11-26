import copy
import json
import tempfile
import unittest
from pathlib import Path

import mmc_update_tool as tool

BASE = Path(__file__).resolve().parent


class UpdateToolTests(unittest.TestCase):
    def setUp(self):
        self.config = tool.load_config(tool.DEFAULT_CONFIG)
        self.launcher = tool.DEFAULT_LAUNCHER
        self.temp = tempfile.TemporaryDirectory(dir=BASE)
        self.dir = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def make_game(self, comparison=None, include_current_message=True):
        data = bytearray(b"\0" * 64)
        if comparison:
            tokens = tool.parse_pattern(comparison)
            data.extend(b if b is not None else 0 for b in tokens)
        data.extend(b"\0" * 16)
        if include_current_message:
            sig = self.config["signatures"]["offline_message_current"]
            data.extend(b if b is not None else 0 for b in tool.parse_pattern(sig["pattern"]))
            data.extend(b"\0" * 160)
        path = self.dir / "new_game.exe"
        path.write_bytes(data)
        return path

    def test_base_integrity_and_slots(self):
        text = tool.inspect_launcher(self.launcher, self.config)
        self.assertIn("integrity checks passed", text)
        self.assertEqual(text.count("slot matches=1"), 5)

    def test_scan_unique_required_patterns(self):
        game = self.make_game(self.config["signatures"]["comparison"]["pattern"])
        _, results = tool.scan_game(game, self.config, "cfb27")
        required = [r for r in results if r.required]
        self.assertEqual(len(required), 2)
        self.assertTrue(all(r.valid for r in required))

    def test_build_preserves_original_and_payload(self):
        game = self.make_game(self.config["signatures"]["comparison"]["pattern"])
        output = self.dir / "updated.exe"
        original_hash = tool.sha256_file(self.launcher)
        text, manifest = tool.build_launcher(
            self.launcher, game, output, tool.DEFAULT_CONFIG,
            self.config, "cfb27",
        )
        self.assertIn("Built:", text)
        self.assertEqual(tool.sha256_file(self.launcher), original_hash)
        self.assertEqual(tool.sha256_file(output), original_hash)
        self.assertTrue(manifest.is_file())
        payload = self.config["embedded_payload"]
        written = output.read_bytes()
        embedded = written[payload["offset"]:payload["offset"] + payload["size"]]
        self.assertEqual(tool.sha256_bytes(embedded), payload["sha256"])

    def test_changed_signature_is_written_and_verified(self):
        config = copy.deepcopy(self.config)
        new_pattern = "48 3B F8 74 71"
        config["signatures"]["comparison"]["pattern"] = new_pattern
        config_path = self.dir / "config.json"
        config_path.write_text(json.dumps(config), encoding="utf-8")
        game = self.make_game(new_pattern)
        output = self.dir / "changed.exe"
        tool.build_launcher(self.launcher, game, output, config_path, config, "cfb27")
        data = output.read_bytes()
        slot = config["signatures"]["comparison"]["slot"].encode("ascii")
        self.assertNotIn(slot, data)
        self.assertIn(new_pattern.encode("ascii") + b"\0", data)
        payload = config["embedded_payload"]
        embedded = data[payload["offset"]:payload["offset"] + payload["size"]]
        self.assertEqual(tool.sha256_bytes(embedded), payload["sha256"])

    def test_missing_required_pattern_blocks_build(self):
        game = self.make_game(comparison=None)
        with self.assertRaises(tool.UpdateError):
            tool.build_launcher(
                self.launcher, game, self.dir / "blocked.exe",
                tool.DEFAULT_CONFIG, self.config, "cfb27",
            )
        self.assertFalse((self.dir / "blocked.exe").exists())

    def test_overlong_pattern_is_rejected(self):
        config = copy.deepcopy(self.config)
        config["signatures"]["comparison"]["pattern"] = "AA " * 20
        path = self.dir / "bad.json"
        path.write_text(json.dumps(config), encoding="utf-8")
        with self.assertRaises(tool.UpdateError):
            tool.load_config(path)


if __name__ == "__main__":
    unittest.main()
