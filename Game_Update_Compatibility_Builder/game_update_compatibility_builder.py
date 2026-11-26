#!/usr/bin/env python3
"""Build a game-update-compatible copy of an MMC replacement launcher.

Run without arguments for the Windows interface. The bundled launcher is the
verified current template. You may instead select an older MMC replacement
launcher when its embedded online-block DLL and signature slots are recognized.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import game_update_builder_core as core

BASE = Path(__file__).resolve().parent
DEFAULT_BASE = BASE / "EAAntiCheat.GameServiceLauncher.exe"
DEFAULT_CONFIG = BASE / "game_update_builder_config.json"
DEFAULT_OUTPUT = BASE / "EAAntiCheat.GameServiceLauncher.compatibility.exe"
TITLE = "Game Update Compatibility Builder"

HOW_IT_WORKS = """GAME UPDATE COMPATIBILITY BUILDER

This tool does not update EA's anti-cheat service. It rebuilds a local MMC
replacement launcher for a newly updated game executable.

WHAT THE BASE LAUNCHER DOES
1. Launches the selected game.
2. Patches the running game process using signatures stored in the launcher.
3. Extracts and injects its bundled EAAntiCheat.GameServiceLauncher.dll.
4. That DLL hooks WS2_32!connect. Non-local destinations return success without
   contacting their server; only 0.0.0.0 reaches the real connect function.
5. Its CryptQueryObject hook normalizes the queried module name.
6. The outer launcher changes live-content-update strings to Boot override
   values and can replace EA's online sign-in error with MMC's offline message.

WHY A GAME UPDATE CAN BREAK IT
The replacement launcher identifies game code by byte signatures. When EA
updates the game, those bytes may move or change. This tool scans the updated
game executable and writes a new launcher copy with replacement signature
strings. It never modifies the game executable or base launcher.

BASE LAUNCHER CHOICE
The bundled current MMC build is selected by default. To use an older MMC
replacement launcher, browse to that file. It is accepted only when the tool
recognizes its embedded online-block DLL and signature slots.

CFB27 / MADDEN27 REQUIREMENTS
The functional comparison signature must have exactly one match. The offline
message signature is informational because replacing that text is cosmetic.
Legacy-only signatures are retained for older Madden builds.
"""


def run_gui() -> None:
    import tkinter as tk
    from tkinter import filedialog, messagebox, scrolledtext

    root = tk.Tk()
    root.title(TITLE)
    root.geometry("980x740")

    base_var = tk.StringVar(value=str(DEFAULT_BASE))
    game_var = tk.StringVar()
    profile_var = tk.StringVar(value="cfb27")
    output_var = tk.StringVar(value=str(DEFAULT_OUTPUT))

    top = tk.Frame(root)
    top.pack(fill="x", padx=12, pady=8)
    fields = [
        ("Base MMC launcher", base_var, "Choose base"),
        ("Updated game executable", game_var, "Choose game"),
        ("Default output name", output_var, "Choose output"),
    ]
    for row_index, (label, variable, button_label) in enumerate(fields):
        tk.Label(top, text=label, width=25, anchor="w").grid(row=row_index, column=0, sticky="w", pady=3)
        tk.Entry(top, textvariable=variable, width=78).grid(row=row_index, column=1, sticky="ew", pady=3)
        tk.Button(top, text=button_label, command=lambda v=variable, i=row_index: choose_file(v, i)).grid(row=row_index, column=2, padx=6, pady=3)
    tk.Label(top, text="Game profile", width=25, anchor="w").grid(row=3, column=0, sticky="w", pady=3)
    tk.OptionMenu(top, profile_var, *core.PROFILES).grid(row=3, column=1, sticky="w", pady=3)
    top.columnconfigure(1, weight=1)

    banner = tk.Label(
        root,
        text="Build an MMC replacement launcher whose hardcoded signatures match the updated game. "
             "EA services, the game executable, and the selected base launcher remain untouched.",
        anchor="w",
        justify="left",
        bg="#eef4ff",
        padx=10,
        pady=8,
    )
    banner.pack(fill="x", padx=12)

    log = scrolledtext.ScrolledText(root, wrap="word", font=("Consolas", 9))
    log.pack(fill="both", expand=True, padx=12, pady=8)

    buttons = tk.Frame(root)
    buttons.pack(fill="x", padx=12, pady=8)
    tk.Button(buttons, text="How it works", command=lambda: show(log, HOW_IT_WORKS)).pack(side="left", padx=3)
    tk.Button(buttons, text="Inspect base", command=lambda: inspect_base(log)).pack(side="left", padx=3)
    tk.Button(buttons, text="1. Scan update", command=lambda: scan(log), bg="#dff0d8").pack(side="left", padx=3)
    tk.Button(buttons, text="2. Build compatible launcher", command=lambda: build(log), bg="#d9edf7").pack(side="left", padx=3)
    tk.Button(buttons, text="Clear", command=lambda: show(log, "Select the updated game executable, then scan.")).pack(side="left", padx=12)

    def choose_file(variable: tk.StringVar, row_index: int) -> None:
        if row_index == 0:
            selected = filedialog.askopenfilename(title="Select base MMC launcher", filetypes=[("Executable", "*.exe")])
        elif row_index == 1:
            selected = filedialog.askopenfilename(title="Select updated game executable", filetypes=[("Game executable", "*.exe")])
        else:
            selected = filedialog.asksaveasfilename(
                title="Choose default output name",
                initialfile=Path(output_var.get()).name,
                defaultextension=".exe",
                filetypes=[("Executable", "*.exe")],
                confirmoverwrite=False,
            )
        if selected:
            variable.set(selected)

    def show(widget: scrolledtext.ScrolledText, text: str) -> None:
        widget.delete("1.0", "end")
        widget.insert("1.0", text)

    def inspect_base(widget: scrolledtext.ScrolledText) -> None:
        try:
            config = core.load_config(DEFAULT_CONFIG)
            show(widget, core.inspect_launcher(Path(base_var.get()), config))
        except Exception as exc:
            messagebox.showerror("Base inspection failed", str(exc))

    def scan(widget: scrolledtext.ScrolledText) -> None:
        try:
            config = core.load_config(DEFAULT_CONFIG)
            game = Path(game_var.get())
            game_hash, results = core.scan_game(game, config, profile_var.get())
            show(widget, core.format_scan(game, game_hash, results, profile_var.get()))
        except Exception as exc:
            messagebox.showerror("Scan failed", str(exc))

    def build(widget: scrolledtext.ScrolledText) -> None:
        try:
            selected = filedialog.asksaveasfilename(
                title="Save compatible MMC replacement launcher",
                initialdir=str(Path(output_var.get()).parent),
                initialfile=Path(output_var.get()).name,
                defaultextension=".exe",
                filetypes=[("Executable", "*.exe")],
                confirmoverwrite=True,
            )
            if not selected:
                return
            config = core.load_config(DEFAULT_CONFIG)
            text, _ = core.build_launcher(
                Path(base_var.get()),
                Path(game_var.get()),
                Path(selected),
                DEFAULT_CONFIG,
                config,
                profile_var.get(),
                overwrite=True,
            )
            show(widget, text)
            messagebox.showinfo(
                "Build complete",
                "A verified compatible launcher was written.\n"
                "Copy it into the game folder as EAAntiCheat.GameServiceLauncher.exe when you are ready.",
            )
        except Exception as exc:
            messagebox.showerror("Build failed", str(exc))

    show(log, HOW_IT_WORKS)
    root.mainloop()


def main() -> int:
    parser = argparse.ArgumentParser(description=TITLE)
    parser.add_argument("--base-launcher", type=Path, default=DEFAULT_BASE)
    parser.add_argument("--game-exe", type=Path, required=True)
    parser.add_argument("--profile", choices=core.PROFILES, required=True)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    try:
        config = core.load_config(DEFAULT_CONFIG)
        text, _ = core.build_launcher(
            args.base_launcher, args.game_exe, args.output,
            DEFAULT_CONFIG, config, args.profile, overwrite=True,
        )
        print(text)
        return 0
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    run_gui()
