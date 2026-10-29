# Center Ice Modding Suite (CIMS) — HANDOFF

Last updated: 2026-10-29. Repo: `C:\dev\NHL27Convert` (git, branch `convert27-slice`, not pushed anywhere).
Earlier survey notes (pre-decision): `C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\docs\HANDOFF.md`.

## Goal and decisions

- A **new program**, not an NHL24Kit tab: Center Ice Modding Suite (short CIMS), exe `CenterIceModdingSuite.exe`.
  Namespaces stay `Suite.*`.
- Converts NHL 27 (PS5, read only) content into NHL 24 (PS4 CUSA37933 on shadPS4 / NHL24PC, read/write).
- Everything is hand-editable (JSON/TSV in `data\` and `%APPDATA%\CenterIceModdingSuite\`); nothing touches a game until Apply.
- The **AssetIndex** (one per game) is the core data model. Matcher, viewer, texture browser, logo swaps, mesh ports and the
  writer read from it, and record edits against index ids (`<game>:<asset path>`).
- Own writer to come: reimplement the documented NHL24Kit approach as new code (append to CAS, repoint TOC chunk table,
  bundle entries, every RES copy and duplicate bundle copy, SHA1s; kept list + mod list; stock.json/stock TOC restore;
  the 15 in-game rules). The built NHL24Kit Worker is only a stopgap backend and a byte-for-byte reference; it is run as
  an external tool and never decompiled into this code base.
- Never let FMT write backups into a game folder (see Read view below).
- Utah keeps artid 22: Mammoth art overwrites t22 and every Arizona texture in place.
- Home/away are paired through default jersey variants (`data\variant_map.json`), never by folder name.

## Hardware

Ryzen 9 5900X (12 cores / 24 threads), Radeon RX 7900 GRE 16 GB, 32 GB RAM, .NET SDK 8.0.423, Python 3.14 (+numpy, Pillow), Edge WebView2.

## Paths

| What | Path |
|---|---|
| NHL 27 PS5 dump (PPSA34063) | `D:\Archives\PS5\PPSA34063\[DLPSGAME.COM]-PPSA34063\[DLPSGAME.COM]-PPSA34063\PPSA34063-app0` |
| NHL 27 texture reader (Python, proven) | `D:\Tools\NHL27TextureViewer` (frostbite\*.py, cache\ = full-game scan, 264 .pkl, 189k rows) |
| NHL 24 game (NHL24PC V3.2, 1.60) | `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC\game` (base, package.mft, Data\Gen4b) + `game-UPDATE` (Patch\Gen4b, no package.mft) |
| NHL 24 read view | `%LOCALAPPDATA%\CenterIceModdingSuite\nhl24view` (copies of package.mft, initfs, layout.toc; junctions to both Gen4b folders; pre-made `.fmtbak` and `__FMTBackup\backup.bin` so FMT has nothing to back up) |
| Stopgap Worker runtime | `%LOCALAPPDATA%\NHL24Kit\runtimes\b935118cf7c257434248dbe2` (needs FrostbiteProfiles) |
| Suite settings / projects / mods | `%APPDATA%\CenterIceModdingSuite\` (settings.json, projects\convert27.json, mods\*.json, data\variant_map.json) |
| Suite caches | `%LOCALAPPDATA%\CenterIceModdingSuite\` (index\<game>\assets.json.gz, nhl27png\, nhl24png\, nhl24refs\, webview2\, suite.log) |
| DB dumps (read-only) | `...\where-are-the-imported-rosters-located\RosterConversion\` (nhl24-csv, nhl27-csv, more-dumps\, arizona_slots_full.json) |
| NHL24Kit docs (only copies left) | `C:\Users\devin\Downloads\{TEXTURE_REPLACEMENT,LOGOS,ROSTER_PORT_NHL27,SCOREBUGS}.md` |

NHL24Kit source was **not found** on this PC (C:\dev\NHL24Kit missing). The old "NHL 24 Modding Suite" (Uniform mannequin,
PhotoCraft, Mesh library) was not found either: only `%APPDATA%\NHL24Suite\` and a Codex sandbox copy with settings, logs and
three material PNGs (`mannequin-materials\Jersey.png, Pants.png, Socks.png`). Its 1,386-vertex mesh and viewer code are gone.

## Repo layout

```
src\Suite.App         WPF shell (CenterIceModdingSuite.exe): sidebar pages, Home (supported games), Settings & about, planned pages; Assets\ logo/icon/splash
src\Suite.Core        AssetIndex, IndexStore (builders), TextureName, VariantMap, ModFile, Images (DDS/PNG), Worker + Nhl24ReadView (stopgap), settings, games
src\Suite.Convert27   Matcher, MatchProject, Catalog views over the index, TextureImages (decode), Convert27View (matcher + WebView2 viewer), viewer\ (three.js r160)
src\Suite.Textures | Rosters | Scorebug | Gameplay | Sounds   empty projects, to be filled
tests\Suite.Tests     xunit (9 tests: parsing, matching rules, variant pairing, index queries, mod file shape)
tools\nhl27_bridge.py NHL 27 index + parallel PNG decode through the NHL27 reader modules
tools\make_variant_map.py  builds data\variant_map.json from both games' stockteamjerseys/exhibitionteams CSV dumps
data\                 variant_map.json, xfactor_map.tsv, utah_loc_edits.tsv (hand-editable)
```

Build: `dotnet build NhlSuite.sln`; tests: `dotnet test tests\Suite.Tests`; run: `src\Suite.App\bin\Debug\net8.0-windows\CenterIceModdingSuite.exe [--page=convert27]`.

## AssetIndex schema (v1)

File: `%LOCALAPPDATA%\CenterIceModdingSuite\index\<game>\assets.json.gz` = gzip JSON
`{ Schema, Game, Fingerprint, Built, Entries[] }`. Rebuilt when the fingerprint changes:
NHL 24 = package.mft + Data/Patch layout.toc (size+time); NHL 27 = layout.toc files + the reader's cache meta files.

`AssetEntry`: `Game` ("nhl24"/"nhl27"), `Kind` (Texture, TextureArray, Mesh, Material, Legacy, Sound), `Path`, `Width`, `Height`,
`Slices`, `Mips`, `FirstMip`, `Format`, `Location { Package, Cas, Tree, Offset, Size, ChunkId, References[] }`.
Id = `Game:Path`. Derived (not stored): `TextureName` = Folder, Leaf, League, Team, Brand, Set, Garment, Role, Layer, IsBase,
IsArrayName, Canonical (leaf with team/brand/set replaced by placeholders).

Query indexes (in memory): by path, by folder, and a token index (path segments, leaf words, `team:`, `brand:`, `set:`, `role:`,
`garment:`, `kind:`, `layer:`, `base`). `Query("team:arizona role:color base")`, prefixes with `*`, substring fallback.

Current contents: NHL 27 144,993 textures with PS5 locations; NHL 24 162,796 textures/arrays (names + kind only; sizes,
formats, chunk ids and every TOC/bundle/RES/duplicate reference come with the suite's own PS4 reader).
Builds in ~15 s with warm caches (NHL 24 through the Worker, NHL 27 through the bridge).

Planned on the same model: Mesh/Material entries (MeshSet: LODs, vertex layout, skeleton, material texture slots) for both
games; `data\texture_matches.json` (all matches with confidence, rule, manual flag; today in projects\convert27.json);
`data\mesh_ports.json` (NHL 27 → NHL 24 mesh mapping, status not started/converted/tested, manual override); thumbnails.

## Convert 27 page (built)

- Pick NHL 24 team + folder; NHL 27 folder is proposed (confidence + reason) and editable.
- Folder pairing: variant map first (default home/away: NHL 24 mostly 0/1 → NHL 27 most 10/11, Boston 13/14, Utah 12/13 for
  Arizona, St. Louis 14/15), then same set word (0.6), then other sets.
- Slot matching on canonical leaf: same garment/layer/base/role. Rules: base coeff kept stock (rule 10), `_base_color` alpha
  `auto` (rule 11; `nodimples` option), arrays get one source per layer from NHL 27 per-layer textures (rule 12),
  plain↔base fallback at half confidence.
- Every row is editable (type an NHL 27 path or file, pick from the folder list, drop an image on a row or array layer, alpha,
  edited flag). Edited rows survive re-matching. Match textures / Match whole team / Match all teams (parallel).
- Save matches → `%APPDATA%\CenterIceModdingSuite\projects\convert27.json`; Add to mod list → `mods\<name>.json`
  (NHL24Kit plan shape: Slot, Source, Layers, Alpha; NHL 27 sources are decoded PNGs).
- Viewer: WebView2 + three.js, one WebGL renderer, two scissored viewports sharing an orbit camera; left NHL 27 set, right NHL 24
  result; Shaded / Flat texture / Fabric regions (alpha legend 64/127/191/255 vs 51/102/153/204) / Turntable. Updates live on
  every row change. **Mannequin is a procedural placeholder** (cylindrical UVs, not the game mesh).

## Status

| Area | Status |
|---|---|
| Shell, logo, splash, Home games panel, Settings/About | done |
| AssetIndex (textures, both games) | done; NHL 24 references/sizes pending own reader |
| Convert 27 matcher | done (variant map, rules 1/10/11/12, manual edits, drag-drop, mod list) |
| Live side-by-side viewer | done with placeholder mannequin; **real NHL 24 skater meshes not done** |
| Own PS4 reader/writer, Apply/Restore | not started |
| Texture browser page, Arizona→Mammoth, adidas→Fanatics | not started (slot list in arizona_slots_full.json) |
| Rosters specs A–E, Teams/Roster pages | not started (data files ready: xfactor_map.tsv, utah_loc_edits.tsv) |
| Mesh index / ports | not started |

## Next steps (priority order)

1. **Real NHL 24 skater meshes in the viewer** (user requirement): decode NHL 24 PS4 MeshSet + skinned mesh (highest LOD skater body,
   jersey, pants, socks, gloves, skates, helmet) and the skeleton; neutral A/T pose; apply `_base_` textures with fabric-region rules;
   both viewer sides use the NHL 24 mesh (left wears the mapped NHL 27 textures). Target look = the old suite's Uniform mannequin
   (game UVs, studio lighting, floor, head/helmet toggle, Front/Back/Left/Right/3-4/Fit/Jersey close-up/Skates close-up/Export PNG,
   material panel with Load colour texture, UV checker, sheen, lighting). Sanity checks: height ~1.85–1.95 m, shoulders ~0.5–0.55 m,
   highest LOD, no blend shapes at max, bind pose applied once. Format references: MMC Editor MeshSetPlugin (PC), FMT NHL24 plugin.
   Until then the placeholder is labelled as such.
2. Own PS4 TOC/bundle/RES reader → fill NHL 24 index sizes/formats/chunk ids/references; thumbnails; texture browser page.
3. Own writer (Apply/Restore), verified byte for byte against the Worker on the same plan.
4. Rosters (A–E) on Suite.Rosters; note: entry_draft/expansion_draft/threes_campaign have only the 11 player tables,
   initial_experience has teams+arenas but no stockteamcolorstable — services skip missing tables, never create them.
