#!/usr/bin/env python3
"""Build an updated MMC online-block launcher for a newly updated game.

This is the simple interface. The detailed CLI implementation lives in
mmc_update_tool.py. The bundled launcher/config are fixed and hidden from the
normal workflow. Run this script without arguments to open the window.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import mmc_update_tool as core

BASE = Path(__file__).resolve().parent
LAUNCHER = BASE / "EAAntiCheat.GameServiceLauncher.exe"
CONFIG = BASE / "launcher_update_config.json"

HOW_IT_WORKS = """WHAT THE BUNDLED LAUNCHER DOES

This is MMC's replacement EAAntiCheat.GameServiceLauncher.exe. It is not EA's
normal online anti-cheat service.

1. It launches the selected game executable.
2. It patches the game process with signatures stored in this launcher.
3. It extracts and injects the bundled EAAntiCheat.GameServiceLauncher.dll.
4. That DLL hooks WS2_32!connect. For every non-0.0.0.0 destination it returns
   success immediately; only a 0.0.0.0 destination reaches the real connect.
5. Its CryptQueryObject hook normalizes the queried module name.
6. The outer launcher also changes the live-content-update LCU strings to Boot
   override values and replaces the EA online sign-in error with MMC's offline
   mod message.

Net effect: EA sign-in/online traffic is neutralized so game files can be
modded safely without entering public online play. No game or anti-cheat file
is modified on disk; all changes are made in the running game process.

HOW TO UPDATE AFTER A GAME PATCH

Select the UPDATED game executable, scan it, and build a new launcher copy.
For CFB 27, these signatures must each have exactly one match:
  - comparison
  - offline_message_current

For Madden 27, the active requirements are the same. The other three entries
are retained for legacy Madden builds.

If a required signature is missing or ambiguous, this tool blocks the build.
Send the displayed report to identify the replacement signature rather than
guessing. Edit only the signature's "pattern" in launcher_update_config.json;
do not change its "slot".
"""


def run_gui() -> None:
    import tkinter as tk
    from tkinter import filedialog, messagebox, scrolledtext

    root = tk.Tk()
    root.title("EA Online-Block Launcher Updater")
    root.geometry("980x720")

    game_var = tk.StringVar()
    profile_var = tk.StringVar(value="cfb27")
    output_var = tk.StringVar(value=str(BASE / "EAAntiCheat.GameServiceLauncher.updated.exe"))

    top = tk.Frame(root)
    top.pack(fill="x", padx=12, pady=8)
    tk.Label(top, text="Updated game executable", width=25, anchor="w").grid(row=0, column=0, sticky="w")
    tk.Entry(top, textvariable=game_var, width=75).grid(row=0, column=1, sticky="ew")
    tk.Button(top, text="Browse…", command=lambda: choose_game()).grid(row=0, column=2, padx=6)
    tk.Label(top, text="Game profile", width=25, anchor="w").grid(row=1, column=0, sticky="w", pady=(6, 0))
    tk.OptionMenu(top, profile_var, *core.PROFILES).grid(row=1, column=1, sticky="w", pady=(6, 0))
    top.columnconfigure(1, weight=1)

    banner = tk.Label(
        root,
        text="This builds an MMC online-block launcher for a legitimate game update. "
             "The game executable and bundled original launcher remain untouched.",
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
    tk.Button(buttons, text="1. Scan update", command=lambda: scan(log), bg="#dff0d8").pack(side="left", padx=3)
    tk.Button(buttons, text="2. Build updated launcher", command=lambda: build(log), bg="#d9edf7").pack(side="left", padx=3)
    tk.Button(buttons, text="Clear", command=lambda: show(log, "Select the updated game executable, then scan.")).pack(side="left", padx=12)

    def choose_game() -> None:
        selected = filedialog.askopenfilename(
            title="Select the UPDATED game executable",
            filetypes=[("Game executable", "*.exe")],
        )
        if selected:
            game_var.set(selected)

    def show(widget: scrolledtext.ScrolledText, text: str) -> None:
        widget.delete("1.0", "end")
        widget.insert("1.0", text)

    def scan(widget: scrolledtext.ScrolledText) -> None:
        try:
            config = core.load_config(CONFIG)
            game = Path(game_var.get())
            game_hash, results = core.scan_game(game, config, profile_var.get())
            show(widget, core.format_scan(game, game_hash, results, profile_var.get()))
        except Exception as exc:
            messagebox.showerror("Scan failed", str(exc))

    def build(widget: scrolledtext.ScrolledText) -> None:
        try:
            output = Path(output_var.get())
            selected = filedialog.asksaveasfilename(
                title="Save the updated online-block launcher",
                initialdir=str(output.parent),
                initialfile=output.name,
                defaultextension=".exe",
                filetypes=[("Executable", "*.exe")],
                confirmoverwrite=True,
            )
            if not selected:
                return
            config = core.load_config(CONFIG)
            text, manifest = core.build_launcher(
                LAUNCHER,
                Path(game_var.get()),
                Path(selected),
                CONFIG,
                config,
                profile_var.get(),
                overwrite=True,
            )
            show(widget, text)
            messagebox.showinfo(
                "Build complete",
                "The updated launcher was written and verified.\n"
                "Copy it over the existing game-folder launcher if its signature patterns still match.",
            )
        except Exception as exc:
            messagebox.showerror("Build failed", str(exc))

    show(log, HOW_IT_WORKS)
    root.mainloop()


def main() -> int:
    parser = argparse.ArgumentParser(description="Build an updated MMC online-block launcher")
    parser.add_argument("--game-exe", type=Path, required=True, help="Updated game executable")
    parser.add_argument("--profile", choices=core.PROFILES, required=True)
    parser.add_argument("--output", type=Path, help="Updated launcher output path")
    args = parser.parse_args()
    try:
        config = core.load_config(CONFIG)
        output = args.output or BASE / "EAAntiCheat.GameServiceLauncher.updated.exe"
        text, _ = core.build_launcher(
            LAUNCHER, args.game_exe, output, CONFIG, config, args.profile,
            overwrite=True,
        )
        print(text)
        return 0
    except Exception as exc:
        print(f"ERROR: {exc}", file=__import__("sys").stderr)
        return 2


if __name__ == "__main__":
    run_gui()
