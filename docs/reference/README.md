# NHL24Kit docs

How everything in NHL24Kit is made, for NHL 24 PS4 (CUSA37933) on shadPS4.

| Doc | Covers |
|---|---|
| [APPLYING.md](APPLYING.md) | Mod list and kept list, incremental applies, rebuild/restore, checking an apply, speed |
| [TEXTURE_REPLACEMENT.md](TEXTURE_REPLACEMENT.md) | How a texture is converted, compressed and written (TOCs, bundles, RES headers, FirstMip, arrays) |
| [UNIFORM_CONVERSION.md](UNIFORM_CONVERSION.md) | NHL 26 → NHL 24 uniforms: one set in the app, or many teams with the batch scripts |
| [LOGOS.md](LOGOS.md) | Team logo textures (replacing existing art, adding new art) and team colours |
| [DATABASE_EDITING.md](DATABASE_EDITING.md) | The t3db roster and text databases, and writing them back |
| [custom-leagues.md](custom-leagues.md) | Adding a league (the USHL): team slots, players, names, colours, logos |
| [PORTRAITS.md](PORTRAITS.md) | Player portraits from NHL.com headshots (matching, cropping, new portrait slots) |
| [ROSTER_PORT_NHL27.md](ROSTER_PORT_NHL27.md) | Bringing NHL 27 rosters into NHL 24 (players, X-Factors, Utah, gear colours) |
| [SCOREBUGS.md](SCOREBUGS.md) | Custom scorebugs (ESPN+, Prime): the game's interface, building, settings, packs |
| [SOUNDS.md](SOUNDS.md) | Audio format, replacing sounds and goal horns |
| [GAMEPLAY_TWEAKS.md](GAMEPLAY_TWEAKS.md) | Changing gameplay values by patching the eboot |
| [UI_SCRIPTS.md](UI_SCRIPTS.md) | Editing UI scripts, the debug menus |
| [EMULATOR.md](EMULATOR.md) | The shadPS4 build and settings, reading crash addresses |

## How the pieces fit

```
NHL24Kit.exe (WPF app, App\)  ──plan.json──▶  NHL24Kit.Worker.exe (Worker\)  ──▶  game folder (CUSA37933)
NHL24RosterEditor.exe (RosterEditor\)  ─┘          uses FMT's libraries only to read
                                                   the asset index; writes everything itself
```

- **App** (`App\`): tabs for Uniforms, Textures, Sounds, Overlays, Scorebug, Gameplay, Rosters & text, and Setup.
  The mod list is on the right. `ModPlan` builds the plan from the kept list and the mod list.
- **Worker** (`Worker\`): every game-side job. It's run from `%LOCALAPPDATA%\NHL24Kit\runtime`, which holds
  FMT 25.17's libraries, extracted from the user's own FMT install. FMT isn't redistributed. Main commands:
  `update` / `apply` / `restore` / `status`, `list` / `refs` / `refs-batch` / `match`, `snapshot` / `dryrun`,
  database tools (`db-dump`, `db-import`, ...), `gnf-png` / `png-gnf` / `gfx-swf`, `scorebug-settings`.
- **Plugin sources** (`C:\dev\nhl24-fmt-plugin\NHL24PS4Plugin`): PS4 tiling (`Ps4Tiling.cs`), block compression
  (`FrostbiteBlocks.cs`) and image conversion (`NHL24PS4TextureImporter.cs`), linked into the worker.
- **Mods** (`mods\`): the inputs and build scripts for each piece of work (`ushl`, `roster27`, `uniforms26`,
  `espnplus`, `prime`, `debugmenu`).
- **Release:** `dist\NHL24Kit` (run the app from there). Build with
  `dotnet publish -c Release -o ../dist/NHL24Kit -p:BaseOutputPath=../obj-pub/` in `App` (and `../obj-pub-re/` in
  `RosterEditor`), then copy the worker build into `dist\NHL24Kit\worker`.

## Ground rules

- Close the game before any apply, restore or rebuild.
- Anything applied by hand goes in the kept list, not the mod list.
- Don't reuse textures for new teams (no other league's logos or jerseys).
- Edit both roster databases.
