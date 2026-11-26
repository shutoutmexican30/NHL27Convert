import copy
import json
import tempfile
import unittest
from pathlib import Path

import game_update_builder_core as core

BASE = Path(__file__).resolve().parent


class GameUpdateBuilderTests(unittest.TestCase):
    def setUp(self):
        self.config = core.load_config(core.DEFAULT_CONFIG)
        self.launcher = core.DEFAULT_LAUNCHER
        self.temp = tempfile.TemporaryDirectory(dir=BASE)
        self.dir = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def make_game(self, comparison=None, include_current_message=True):
        data = bytearray(b"\0" * 64)
        if comparison:
            data.extend(b if b is not None else 0 for b in core.parse_pattern(comparison))
        data.extend(b"\0" * 16)
        if include_current_message:
            sig = self.config["signatures"]["offline_message_current"]
            data.extend(b if b is not None else 0 for b in core.parse_pattern(sig["pattern"]))
            data.extend(b"\0" * 160)
        path = self.dir / "new_game.exe"
        path.write_bytes(data)
        return path

    def test_base_integrity_and_slots(self):
        text = core.inspect_launcher(self.launcher, self.config)
        self.assertIn("online-block payload checks passed", text)
        self.assertEqual(text.count("slot matches=1"), 5)
        self.assertEqual(self.config["signatures"]["offline_message_current"]["required_for"], [])

    def test_scan_unique_required_pattern(self):
        game = self.make_game(self.config["signatures"]["comparison"]["pattern"])
        _, results = core.scan_game(game, self.config, "cfb27")
        required = [r for r in results if r.required]
        self.assertEqual([r.name for r in required], ["comparison"])
        self.assertTrue(all(r.valid for r in required))

    def test_build_preserves_original_and_payload(self):
        game = self.make_game(self.config["signatures"]["comparison"]["pattern"])
        output = self.dir / "compatible.exe"
        original_hash = core.sha256_file(self.launcher)
        text, manifest = core.build_launcher(
            self.launcher, game, output, core.DEFAULT_CONFIG,
            self.config, "cfb27",
        )
        self.assertIn("Built:", text)
        self.assertEqual(core.sha256_file(self.launcher), original_hash)
        self.assertEqual(core.sha256_file(output), original_hash)
        self.assertTrue(manifest.is_file())
        payload = self.config["embedded_payload"]
        written = output.read_bytes()
        embedded = written[payload["offset"]:payload["offset"] + payload["size"]]
        self.assertEqual(core.sha256_bytes(embedded), payload["sha256"])

    def test_changed_signature_is_written_and_verified(self):
        config = copy.deepcopy(self.config)
        new_pattern = "48 3B F8 74 71"
        config["signatures"]["comparison"]["pattern"] = new_pattern
        config_path = self.dir / "config.json"
        config_path.write_text(json.dumps(config), encoding="utf-8")
        game = self.make_game(new_pattern)
        output = self.dir / "changed.exe"
        core.build_launcher(self.launcher, game, output, config_path, config, "cfb27")
        data = output.read_bytes()
        slot = config["signatures"]["comparison"]["slot"].encode("ascii")
        self.assertNotIn(slot, data)
        self.assertIn(new_pattern.encode("ascii") + b"\0", data)
        payload = config["embedded_payload"]
        embedded = data[payload["offset"]:payload["offset"] + payload["size"]]
        self.assertEqual(core.sha256_bytes(embedded), payload["sha256"])

    def test_missing_required_pattern_blocks_build(self):
        game = self.make_game(comparison=None)
        with self.assertRaises(core.UpdateError):
            core.build_launcher(
                self.launcher, game, self.dir / "blocked.exe",
                core.DEFAULT_CONFIG, self.config, "cfb27",
            )
        self.assertFalse((self.dir / "blocked.exe").exists())

    def test_missing_informational_message_does_not_block_build(self):
        game = self.make_game(
            self.config["signatures"]["comparison"]["pattern"],
            include_current_message=False,
        )
        output = self.dir / "message_optional.exe"
        text, _ = core.build_launcher(
            self.launcher, game, output, core.DEFAULT_CONFIG,
            self.config, "cfb27",
        )
        self.assertIn("Built:", text)
        self.assertTrue(output.is_file())

    def test_recognized_legacy_base_is_accepted(self):
        legacy = self.dir / "legacy_base.exe"
        data = bytearray(self.launcher.read_bytes())
        data[-1] ^= 1
        legacy.write_bytes(data)
        self.assertNotEqual(core.sha256_file(legacy), self.config["base_launcher_sha256"])
        game = self.make_game(
            self.config["signatures"]["comparison"]["pattern"],
            include_current_message=False,
        )
        output = self.dir / "legacy_output.exe"
        text, manifest_path = core.build_launcher(
            legacy, game, output, core.DEFAULT_CONFIG,
            self.config, "cfb27",
        )
        self.assertIn("Base profile: recognized-legacy", text)
        self.assertTrue(output.is_file())
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        self.assertEqual(manifest["base_launcher_profile"], "recognized-legacy")
        self.assertEqual(manifest["base_launcher_sha256"], core.sha256_file(legacy))

    def test_overlong_pattern_is_rejected(self):
        config = copy.deepcopy(self.config)
        config["signatures"]["comparison"]["pattern"] = "AA " * 20
        path = self.dir / "bad.json"
        path.write_text(json.dumps(config), encoding="utf-8")
        with self.assertRaises(core.UpdateError):
            core.load_config(path)


if __name__ == "__main__":
    unittest.main()
