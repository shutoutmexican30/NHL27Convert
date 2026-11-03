# Gameplay tweaks (eboot patching)

NHL 24 has about 25,500 built-in tuning values ("tweakables"), for example `gWristShotPowerMultiplier` or the
sliders under `AI/Sliders/Hitting`. 24Kit's Gameplay tab ("Edit gameplay...") changes their defaults by patching
the eboot through the emulator's patch file. Confirmed in game: wrist shot power 0.88 → 3.0 made wrist shots much
harder.

## 1. Finding them

The eboot is a fake-signed SELF (`ET_SCE_DYNEXEC`, position independent). Convert it to a flat image indexed by
virtual address (scratchpad `self2elf.py` → `test\eboot\image.bin`).

Static initializers link every tweakable object into a list (head at `0xAB83918`). Object layout:
- `+0x10` alias, `+0x18` name, `+0x20` kind (1 bool, 2 int, 4 float, 12 curve, 13/14 other)
- value block from `+0x38`: float or int = value, min, max (`+0x40`); bool = one byte; curve = count plus a
  pointer to a float array

The full list is `docs/gameplay_tweakables.csv`. The app's catalog is `App\gameplay_catalog.json` (21,666 entries).

## 2. Changing a default

Defaults are set by the initializer code, so patch the instruction that loads them:
- **floats** load with `vmovss xmm0, [rip+const]`. The constants are **shared** with other code, so don't change
  the constant: repoint the instruction's displacement at existing read-only bytes that already hold the wanted
  float.
- **ints** are an imm32 in the instruction; **bools** an imm8. Only instructions that carry the immediate
  themselves are in the catalog (a store from a register would patch the wrong thing).

Patches are shadPS4 `<Line Type="bytes">` lines, with Address = `0x400000` + image address. Patches apply before
the static initializers run, so the new default sticks.

## 3. In 24Kit

Edits live in `%APPDATA%\NHL24Kit\gameplay.json`. Save writes an "NHL24Kit gameplay" block between begin/end comments
in the patch file chosen in Setup (`nhl24.xml`; a backup is made as `.before-nhl24kit`). The nhl24pc build loads
`nhl24.xml` by default. Code: `GameplayWindow`, `GameplayPatcher.cs`; test harness `test\GameplayTest`.
