# Running NHL 24 on shadPS4

## 1. Which build

The game runs on **nhl24pc** (github.com/HajDEO/nhl24pc, an NHL-only shadPS4 fork), built from source to
`C:\dev\ext\nhl24pc\build\nhl24.exe` and set up in `C:\Games\NHL24-PC`. `user\game_path.txt` points at the extracted
`CUSA37933`. It fixes the team-select flicker of plain shadPS4: fence writes page-fault into readbacks, which makes
the dynamic resolution jitter and churns the texture cache. Upstream shadPS4 plus the NHL patch broke gameplay
(static noise, missing ice).

## 2. Settings that matter

- `windows_guest_red_zone_protection_mode` must be **StaticPatching** (per-game config
  `%APPDATA%\shadPS4\custom_configs\CUSA37933.json`). With it Disabled, loading into any mode crashed: the game
  reloads a pointer from the SysV red zone (`[rsp-0x28]`), and Windows exception handlers overwrite it.
- Never leave a `commandline.txt` in `/app0` (the game folder) or `/data`: the game reads it, and even an empty one
  made it loop. The game does honour `-dataPath` from it.
- `<game>-mods` is shadPS4's per-file overlay of `/app0`. 24Kit writes into the game directly and refuses to apply
  while that folder exists.

## 3. Reading a crash

The shadPS4 log names the exception address. Subtract the eboot's load base, which the log prints as
"Loading module eboot.bin to 0x..." (0x5240000 on the nhl24pc build), to get the address in the eboot image. Then
look it up in the disassembly. Crashes found this way:
- null top player in Play Now: player ids reused from the custom-player range;
- null TeamJerseys in lines view: wrong roster keys, or added jersey rows;
- a crash on the first loading screen: a stale size record in a legacy index chunk.

## 4. Mods outside the game data

- **Gameplay values:** the patch file (see [GAMEPLAY_TWEAKS.md](GAMEPLAY_TWEAKS.md)).
- **New logo art:** loose GNFs under the emulator's `user/` data (see [LOGOS.md](LOGOS.md)).
- **Everything else** goes into the game files through 24Kit (see [APPLYING.md](APPLYING.md)).
