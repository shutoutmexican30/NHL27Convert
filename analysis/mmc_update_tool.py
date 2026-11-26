#!/usr/bin/env python3
"""MMC online-block launcher update tool for legitimate game installations.

The current analyzed MMC build is verified exactly. Optionally, older MMC
online-block builds are accepted when their embedded DLL and signature slots
are recognizable. The tool only edits signature strings in a copy and never
modifies the game executable or base launcher.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import mmap
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

APP_NAME = "MMC Launcher Update Tool"
BASE_DIR = Path(__file__).resolve().parent
DEFAULT_LAUNCHER = BASE_DIR / "EAAntiCheat.GameServiceLauncher.exe"
DEFAULT_CONFIG = BASE_DIR / "launcher_update_config.json"
DEFAULT_OUTPUT = BASE_DIR / "EAAntiCheat.GameServiceLauncher.updated.exe"
PROFILES = ("cfb27", "madden27", "madden-legacy")
CURRENT_BASE_PROFILE = "current-analyzed"
LEGACY_BASE_PROFILE = "recognized-legacy"


class UpdateError(Exception):
    pass


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_config(path: Path) -> dict[str, Any]:
    try:
        config = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise UpdateError(f"Config not found: {path}")
    except json.JSONDecodeError as exc:
        raise UpdateError(f"Invalid JSON in {path}: {exc}")
    if config.get("format") not in (1, 2):
        raise UpdateError("Unsupported config format; expected format 1 or 2")
    if not isinstance(config.get("signatures"), dict):
        raise UpdateError("Config is missing the signatures object")
    signature_names = set(config["signatures"])
    for name, sig in config["signatures"].items():
        for field in ("slot", "pattern", "required_for"):
            if field not in sig:
                raise UpdateError(f"Signature {name!r} is missing {field!r}")
        parse_pattern(sig["pattern"])
        if len(sig["pattern"].encode("ascii")) > len(sig["slot"].encode("ascii")):
            raise UpdateError(
                f"Signature {name!r} is longer than its launcher slot. "
                "Use a shorter, still-unique pattern or rebuild from source."
            )
        unknown = set(sig["required_for"]) - set(PROFILES)
        if unknown:
            raise UpdateError(f"Signature {name!r} has unknown profiles: {sorted(unknown)}")
    unknown_optional = set(config.get("legacy_optional_slots", [])) - signature_names
    if unknown_optional:
        raise UpdateError(f"Unknown legacy optional slots: {sorted(unknown_optional)}")
    return config


def parse_pattern(text: str) -> list[int | None]:
    tokens = text.split()
    if not tokens:
        raise UpdateError("Empty signature pattern")
    result: list[int | None] = []
    for token in tokens:
        if token in ("?", "??"):
            result.append(None)
            continue
        if len(token) != 2:
            raise UpdateError(f"Invalid byte token {token!r}; use two hex digits or ??")
        try:
            result.append(int(token, 16))
        except ValueError:
            raise UpdateError(f"Invalid byte token {token!r}")
    return result


def find_pattern(data: mmap.mmap, pattern: str) -> list[int]:
    """Find all wildcard-aware matches, anchored by its longest fixed run."""
    tokens = parse_pattern(pattern)
    runs: list[tuple[int, bytes]] = []
    start = 0
    while start < len(tokens):
        if tokens[start] is None:
            start += 1
            continue
        end = start
        while end < len(tokens) and tokens[end] is not None:
            end += 1
        runs.append((start, bytes(tokens[start:end])))  # type: ignore[arg-type]
        start = end
    anchor_index, anchor = max(runs, key=lambda item: len(item[1]))
    matches: list[int] = []
    pos = data.find(anchor)
    while pos >= 0:
        candidate = pos - anchor_index
        if candidate >= 0 and candidate + len(tokens) <= len(data):
            if all(b is None or data[candidate + i] == b for i, b in enumerate(tokens)):
                matches.append(candidate)
        pos = data.find(anchor, pos + 1)
    return matches


def pe_rva_from_offset(data: bytes, offset: int) -> int | None:
    """Map a raw file offset to an RVA using a minimal PE parser."""
    try:
        if data[:2] != b"MZ":
            return None
        pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe_offset:pe_offset + 4] != b"PE\0\0":
            return None
        number_of_sections = struct.unpack_from("<H", data, pe_offset + 6)[0]
        optional_size = struct.unpack_from("<H", data, pe_offset + 20)[0]
        section_table = pe_offset + 24 + optional_size
        for i in range(number_of_sections):
            s = section_table + i * 40
            virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from("<IIII", data, s + 8)
            if raw_offset <= offset < raw_offset + raw_size:
                rva = virtual_address + offset - raw_offset
                if rva < virtual_address + max(virtual_size, raw_size):
                    return rva
    except (IndexError, struct.error):
        return None
    return None


@dataclass
class ScanResult:
    name: str
    label: str
    required: bool
    matches: list[int]
    rvas: list[int | None]
    patch_offset: int
    patch_bytes: str | None
    write_length: int | None

    @property
    def valid(self) -> bool:
        return len(self.matches) == 1

    def as_dict(self) -> dict[str, Any]:
        return {
            "label": self.label,
            "required": self.required,
            "match_file_offsets": [hex(x) for x in self.matches],
            "match_rvas": [hex(x) if x is not None else None for x in self.rvas],
            "patch_offset": hex(self.patch_offset),
            "patch_bytes": self.patch_bytes,
            "write_length": self.write_length,
            "unique": self.valid,
        }


@dataclass
class BaseInfo:
    data: bytes
    sha256: str
    profile: str
    payload_offset: int | None
    payload_sha256: str | None

    @property
    def is_exact_base(self) -> bool:
        return self.profile == CURRENT_BASE_PROFILE


ONLINE_BLOCK_MARKERS = (
    b"EAAntiCheat.GameServiceLauncher.dll",
    b"WS2_32.dll",
    b"CRYPT32.dll",
    b"CryptQueryObject",
    b"connect",
    b"0.0.0.0",
)


def find_online_block_dll(data: bytes) -> int | None:
    """Locate a recognizable embedded MMC online-block DLL by scanning MZ files."""
    search_end = max(0, len(data) - 512)
    pos = data.find(b"MZ")
    while 0 <= pos < search_end:
        try:
            pe_offset = struct.unpack_from("<I", data, pos + 0x3C)[0]
            pe_pos = pos + pe_offset
            if pe_offset >= 0x40 and pe_pos + 24 <= len(data) and data[pe_pos:pe_pos + 4] == b"PE\0\0":
                section_count = struct.unpack_from("<H", data, pe_pos + 6)[0]
                optional_size = struct.unpack_from("<H", data, pe_pos + 20)[0]
                section_table = pe_pos + 24 + optional_size
                if section_table + section_count * 40 <= len(data):
                    section_names = b"".join(data[section_table + i * 40:section_table + i * 40 + 8]
                                             for i in range(section_count))
                    window_end = min(len(data), pos + 0x20000)
                    window = data[pos:window_end]
                    if (b".detourc" in section_names and
                            all(marker in window for marker in ONLINE_BLOCK_MARKERS)):
                        return pos
        except (IndexError, struct.error):
            pass
        pos = data.find(b"MZ", pos + 1)
    return None


def slot_offsets(data: bytes, config: dict[str, Any], *, allow_missing: bool = False) -> dict[str, int]:
    optional = set(config.get("legacy_optional_slots", [])) if allow_missing else set()
    offsets: dict[str, int] = {}
    for name, sig in config["signatures"].items():
        slot = sig["slot"].encode("ascii")
        count = data.count(slot)
        if count == 0 and name in optional:
            continue
        if count != 1:
            detail = "not found" if count == 0 else f"found {count} times"
            raise UpdateError(f"Launcher slot for {name!r} was {detail}, expected exactly once")
        offsets[name] = data.find(slot)
    return offsets


def verify_base_launcher(launcher: Path, config: dict[str, Any]) -> BaseInfo:
    if not launcher.is_file():
        raise UpdateError(f"Launcher not found: {launcher}")
    data = launcher.read_bytes()
    actual_hash = sha256_bytes(data)

    if actual_hash == config["base_launcher_sha256"]:
        payload = config["embedded_payload"]
        offset, size = int(payload["offset"]), int(payload["size"])
        embedded = data[offset:offset + size]
        if len(embedded) != size or sha256_bytes(embedded) != payload["sha256"]:
            raise UpdateError("Embedded DLL failed its integrity check")
        return BaseInfo(data, actual_hash, CURRENT_BASE_PROFILE, offset, payload["sha256"])

    if not config.get("allow_legacy_bases", False):
        raise UpdateError(
            "The selected launcher is not the analyzed base file. "
            f"Expected SHA-256 {config['base_launcher_sha256']}, got {actual_hash}. "
            "Legacy-base support is disabled in the config."
        )

    payload_offset = find_online_block_dll(data)
    if payload_offset is None:
        raise UpdateError(
            "The launcher is not the exact current build and does not contain a recognizable "
            "MMC online-block DLL (connect/CryptQueryObject/Detours markers were not found)."
        )
    slot_offsets(data, config, allow_missing=True)
    return BaseInfo(data, actual_hash, LEGACY_BASE_PROFILE, payload_offset, None)


def scan_game(game_path: Path, config: dict[str, Any], profile: str) -> tuple[str, list[ScanResult]]:
    if profile not in PROFILES:
        raise UpdateError(f"Unknown profile {profile!r}")
    if not game_path.is_file():
        raise UpdateError(f"Game executable not found: {game_path}")
    game_hash = sha256_file(game_path)
    results: list[ScanResult] = []
    with game_path.open("rb") as f:
        data = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            header = data[: min(len(data), 4096)]
            for name, sig in config["signatures"].items():
                matches = find_pattern(data, sig["pattern"])
                rvas = [pe_rva_from_offset(header, m) for m in matches]
                results.append(ScanResult(
                    name=name,
                    label=sig.get("label", name),
                    required=profile in sig["required_for"],
                    matches=matches,
                    rvas=rvas,
                    patch_offset=int(sig.get("patch_offset", 0)),
                    patch_bytes=sig.get("patch_bytes"),
                    write_length=sig.get("write_length"),
                ))
        finally:
            data.close()
    return game_hash, results


def format_scan(game_path: Path, game_hash: str, results: list[ScanResult], profile: str) -> str:
    lines = [f"Game: {game_path}", f"SHA-256: {game_hash}", f"Profile: {profile}", ""]
    for result in results:
        requirement = "required" if result.required else "informational"
        if not result.matches:
            status = "NOT FOUND"
        elif len(result.matches) == 1:
            status = "UNIQUE MATCH"
        else:
            status = f"AMBIGUOUS ({len(result.matches)} matches)"
        lines.append(f"[{status}] {result.name} ({requirement})")
        lines.append(f"  {result.label}")
        for offset, rva in list(zip(result.matches, result.rvas))[:20]:
            rva_text = f", RVA {rva:#x}" if rva is not None else ""
            lines.append(f"  file offset {offset:#x}{rva_text}; patch target {offset + result.patch_offset:#x}")
        if len(result.matches) > 20:
            lines.append(f"  ... {len(result.matches) - 20} more matches")
    failures = [r for r in results if r.required and not r.valid]
    lines.extend(["", "READY TO BUILD" if not failures else "NOT READY: every required signature needs exactly one match"])
    return "\n".join(lines)


def inspect_launcher(launcher: Path, config: dict[str, Any]) -> str:
    info = verify_base_launcher(launcher, config)
    offsets = slot_offsets(info.data, config, allow_missing=not info.is_exact_base)
    lines = [
        f"Launcher: {launcher}",
        f"SHA-256: {info.sha256}",
        f"Size: {len(info.data)} bytes",
        f"Base profile: {info.profile}",
        f"Online-block DLL offset: {info.payload_offset:#x}" if info.payload_offset is not None else "Online-block DLL offset: unknown",
        "",
    ]
    for name, sig in config["signatures"].items():
        slot = sig["slot"].encode("ascii")
        offset = offsets.get(name)
        if offset is None:
            lines.append(f"{name}: slot absent (optional on recognized legacy bases)")
        else:
            lines.append(f"{name}: slot matches=1, file offset={offset:#x}")
        lines.append(f"  current pattern: {sig['pattern']}")
    if info.payload_sha256:
        lines.extend(["", f"Embedded DLL SHA-256: {info.payload_sha256}"])
    lines.append("Base launcher and online-block payload checks passed.")
    return "\n".join(lines)


def verify_output(base: BaseInfo, written: bytes, replacements: list[tuple[int, bytes, bytes]]) -> None:
    if len(written) != len(base.data):
        raise UpdateError("Output verification failed: file length changed")
    cursor = 0
    for offset, old, new in replacements:
        if written[cursor:offset] != base.data[cursor:offset]:
            raise UpdateError(f"Output verification failed: unexpected data before offset {offset:#x}")
        if written[offset:offset + len(new)] != new:
            raise UpdateError(f"Output verification failed at edited offset {offset:#x}")
        cursor = offset + len(new)
    if written[cursor:] != base.data[cursor:]:
        raise UpdateError("Output verification failed: unexpected data after edits")
    if base.is_exact_base:
        payload = json.loads(json.dumps({}))  # placate static analyzers; never used
        del payload
        # The exact-base caller already validates the fixed payload range below.


def build_launcher(
    launcher: Path,
    game_path: Path,
    output: Path,
    config_path: Path,
    config: dict[str, Any],
    profile: str,
    overwrite: bool = False,
) -> tuple[str, Path]:
    base = verify_base_launcher(launcher, config)
    offsets = slot_offsets(base.data, config, allow_missing=not base.is_exact_base)
    game_hash, results = scan_game(game_path, config, profile)
    by_name = {r.name: r for r in results}
    failures = [r for r in results if r.required and not r.valid]
    # A deliberately changed optional signature also needs to be unambiguous.
    for name, sig in config["signatures"].items():
        if name in offsets and sig["pattern"] != sig["slot"] and not by_name[name].valid:
            failures.append(by_name[name])
    if failures:
        raise UpdateError(
            "Build blocked; fix these signatures first: "
            + ", ".join(f"{r.name} ({len(r.matches)} matches)" for r in failures)
        )
    output = output.resolve()
    if output == launcher.resolve():
        raise UpdateError("Refusing to overwrite the base launcher")
    if output.exists() and not overwrite:
        raise UpdateError(f"Output already exists: {output}. Choose another name or enable overwrite.")

    patched = bytearray(base.data)
    replacements: list[tuple[int, bytes, bytes]] = []
    changed: list[str] = []
    for name, offset in offsets.items():
        sig = config["signatures"][name]
        slot = sig["slot"].encode("ascii")
        desired = sig["pattern"].encode("ascii")
        old = slot + b"\0"
        new = desired + b"\0" + bytes(len(slot) - len(desired))
        if new != old:
            patched[offset:offset + len(old)] = new
            replacements.append((offset, old, new))
            changed.append(name)

    if base.is_exact_base:
        payload = config["embedded_payload"]
        p_offset, p_size = int(payload["offset"]), int(payload["size"])
        if patched[p_offset:p_offset + p_size] != base.data[p_offset:p_offset + p_size]:
            raise UpdateError("Internal error: embedded DLL would be changed")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(patched)
    try:
        written = output.read_bytes()
        verify_output(base, written, replacements)
        if base.is_exact_base:
            payload = config["embedded_payload"]
            p_offset, p_size = int(payload["offset"]), int(payload["size"])
            if sha256_bytes(written[p_offset:p_offset + p_size]) != payload["sha256"]:
                raise UpdateError("Output verification failed: embedded DLL changed")
    except Exception:
        output.unlink(missing_ok=True)
        raise

    manifest = {
        "tool": APP_NAME,
        "profile": profile,
        "base_launcher": str(launcher.resolve()),
        "base_launcher_profile": base.profile,
        "base_launcher_sha256": base.sha256,
        "base_online_block_dll_offset": base.payload_offset,
        "game_executable": str(game_path.resolve()),
        "game_sha256": game_hash,
        "output_launcher": str(output),
        "output_launcher_sha256": sha256_bytes(written),
        "config": str(config_path.resolve()),
        "changed_signature_slots": changed,
        "embedded_payload_sha256": base.payload_sha256,
        "scan": {r.name: r.as_dict() for r in results},
    }
    manifest_path = output.with_suffix(output.suffix + ".manifest.json")
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    text = format_scan(game_path, game_hash, results, profile)
    text += f"\n\nBase profile: {base.profile}"
    text += f"\nBuilt: {output}\nSHA-256: {manifest['output_launcher_sha256']}"
    text += f"\nChanged slots: {', '.join(changed) if changed else 'none (patterns already current)'}"
    text += f"\nManifest: {manifest_path}"
    return text, manifest_path


def make_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=APP_NAME)
    sub = parser.add_subparsers(dest="command", required=True)
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--launcher", type=Path, default=DEFAULT_LAUNCHER)
    common.add_argument("--config", type=Path, default=DEFAULT_CONFIG)
    sub.add_parser("inspect", parents=[common], help="Verify the base launcher and show signature slots")
    for command, help_text in (("scan", "Scan a new game executable"), ("build", "Validate and build an updated launcher copy")):
        p = sub.add_parser(command, parents=[common], help=help_text)
        p.add_argument("--game-exe", type=Path, required=True)
        p.add_argument("--profile", choices=PROFILES, required=True)
        if command == "build":
            p.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
            p.add_argument("--force", action="store_true", help="Overwrite an existing output, never the base launcher")
    return parser


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if not argv:
        # The focused GUI is implemented by mmc_online_block_updater.py.
        print("Use mmc_online_block_updater.py for the GUI, or run this file with inspect/scan/build commands.")
        return 2
    args = make_parser().parse_args(argv)
    try:
        config_path = args.config.resolve()
        config = load_config(config_path)
        if args.command == "inspect":
            print(inspect_launcher(args.launcher.resolve(), config))
            return 0
        game_hash, results = scan_game(args.game_exe.resolve(), config, args.profile)
        if args.command == "scan":
            print(format_scan(args.game_exe.resolve(), game_hash, results, args.profile))
            return 0 if all(r.valid for r in results if r.required) else 2
        text, _ = build_launcher(
            args.launcher.resolve(), args.game_exe.resolve(), args.output,
            config_path, config, args.profile, args.force,
        )
        print(text)
        return 0
    except UpdateError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
