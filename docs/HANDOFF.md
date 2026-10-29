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

## Uniform Studio page (built)

The card-grid texture viewer from the user's spec (matching the earlier "NHL Kit Uniform Studio"):
left panel with NHL 24 game-folder picker, team and uniform/set selectors (discovered from the index, nothing hard-coded),
replacement-texture folder picker (DDS/PNG, recursive) and "also offer the matched NHL 27 uniform". Main area: a card per texture
(array layers get one card each) with ORIGINAL (the game's own texture) and NEW previews side by side (aspect kept, checker
background), asset name, format/size, replacement file name, match score and reason, Replace checkbox, Choose…/Browse… buttons,
drop target. Click NEW or Choose → in-window chooser ranked by match; click ORIGINAL → full-size inspector with zoom.
Matching: exact asset name (and layer) 100%, same material role with team/brand/set words ignored 90%, same garment+role 70%,
plain↔base 45% (manual only). Coefficient maps are never auto-replaced. Tags per card: REPLACED / UNUSED / COEFF KEPT STOCK;
unmatched replacement files are listed. Save → mod library entry `Uniform Studio - <team> <brand> <set>`; reopening restores it.
Progress, error count and an expandable activity log at the bottom. Apply (incremental, preserving installed mods, skipping
unchanged assets, never a full rebuild) comes with the suite's own writer.

## User requirements so far

- Whole new program, named Center Ice Modding Suite, own logo, shows "Supports: NHL 27 (PS5) · NHL 24 (PS4)" with a Games panel.
- Everything hand-editable everywhere; nothing written to a game until the user presses Apply; close the game before applies.
- The asset index is the backbone (textures, meshes, materials, legacy files, sounds), fast to query, edits keyed by index id.
- Replace any texture in the game (162,796 NHL 24 slots), by hand or drag and drop.
- Convert 27: matcher and live viewer on the same page side by side; "Match textures", "Match all teams"; manual overrides win.
- Home/away paired through each team's default variants (stockteamjerseys isdefault/islightcolor), map stored hand-editable.
- Viewer: the **real NHL 24 skater geometry** (body, jersey, pants, socks, gloves, skates, helmet, other worn gear) on both sides
  with real textures, NHL 24 skeleton, neutral pose, correct proportions (~1.9 m), game UVs; look and controls of the old
  "NHL 24 Modding Suite" Uniform mannequin (camera presets Front/Back/Left/Right/3-4/Fit uniform/Jersey close-up/Skates close-up,
  Export PNG, material panel with Load colour texture, UV checker, sheen, lighting, head/helmet toggle). No placeholder unless
  extraction fails, and then say exactly where it failed.
- Uniform Studio card grid (above) as its own page.
- Arizona → Utah Mammoth: keep artid 22, overwrite t22 and every Arizona texture in place (slots in arizona_slots_full.json).
- adidas → Fanatics swap on branding arrays (needs Fanatics art).
- Scorebug editor and every gameplay tweakable in one place; sounds; UI scripts.
- Use all CPU cores for decode/convert; GPU for the viewer; GPU texture encode only if verified correct for BC7.
- Never let FMT write backups into the real game folder. No machine-specific paths (auto-detect + pickers).
- One distributable EXE.

## Rosters specs (Suite.Rosters; from the DB thread)

- **A RosterPort**: team 22 keeps artid 22 (and exhibitionarena.arenaartid 22), artabbr PHX, abbrname UTA; loc keys stay *_PHX and are
  updated in place (keep hashid, sorted). From NHL 27 team/arena rows copy only teamname, nickname, cityname, abbrname, state,
  colour fields (*primary/secondary/tertiarycolor, goalpostcolor, crowdlightcolor), avgattendance, arena name/city/maxattendance/
  yearopened; keep every other NHL 24 numeric (known bad NHL 27 Utah values listed in the DB thread). Meta-driven validator
  (ints within [rangelow, rangehigh], UTF-8 bytes+1 ≤ depth/8, drop columns not in NHL 24 meta) is the only write path; refuse,
  never clamp. stockteamcolorstable team 22 rows 76–81 values only. teams_overrides.json applied last to nhl_game,
  initial_experience, entry_draft, expansion_draft, threes_campaign and every loc db.
- **B Teams & Roster pages**: searchable teams, every field with validation, colour pickers, 8 loc strings per language, arena,
  NHL API code, logo source; Build; Add to mod list; Roster panel with NHL API roster, matching, headshots.
- **C Gear/masks/colours**: drop NHL 27-only columns, copy colour fields for players in both games, model ids only if used in NHL 24
  (else keep / most common of brand family), masks from the NHL 24 team row, Arizona goalie masks to the Mammoth list,
  stockteamequipment home/away through the variant map.
- **D X-Factors**: `data\xfactor_map.tsv` (27 → 24, editable), never write an undefined id.
- **E NHL API sync**: standings/roster/landing endpoints, `nhlapi_teams.tsv`, `nhlapi_matches.tsv`, diff + confirm, manual edits win.
- Format facts: entry_draft/expansion_draft/threes_campaign hold only the 11 player tables; initial_experience has teams and arenas
  but no stockteamcolorstable; services skip missing tables, never create them. Utah loc edits: `data\utah_loc_edits.tsv`.

## DB, roster, portraits and team colours (DB thread, 07:10 UTC)

Done:
- Mapped every Arizona location in NHL 24 data: all 5 roster dbs, the Franchise/Be a Pro league* tables (seeded from exhibition*;
  existing saves cannot be patched), loc keys, colours, eboot strings.
- NHL 27 Utah row vs NHL 24 meta: 4 columns to drop, goalie masks 515/516 exceed 511, about 20 NHL 27-only asset ids. Rule: copy names,
  colours, arena name/city/capacity/year only; keep NHL 24 asset ids. Seed `data\teams_overrides.json` (in the repo).
- `RosterConversion\arizona_slots_full.json`: 527 definite Arizona textures (texture side).
- 07:10: more-dumps checked; `data\utah_loc_edits.tsv` (29 eng_us keys including *_22) and `data\xfactor_map.tsv`;
  draft and 3v3 dbs hold only the 11 player tables; initial_experience has teams/arenas but no colour table.

Decisions: Utah keeps index 22, artid 22, arenaid 22, artabbr PHX, abbrname UTA; Mammoth art overwrites t22 in place. Rosters are
reimplemented in `src\Suite.Rosters` per specs A–E, every write validated against nhl_game-meta.xml.

Data: stock dbs + CSV dumps in `C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\RosterConversion\`
(nhl24-original, nhl24-csv, nhl27-csv, more-dumps). Game: `NHL24PC_V3.2\NHL24PC\game-UPDATE` (1.60, Kraken chunks in
`Patch\Gen4b\superbundlelayout`).

Blocked / next: other languages (fre_fr, ger_de, swe_se, fin_fi, cze_cz, rus_ru) not dumped yet, same keys apply; NHL 27 X-Factor
names not dumped (the map rests on co-occurrence); NHL API roster and headshots after RosterPortService.
Decided 07:41: Alumni (279) stays Arizona Coyotes Alumni; *_ARZL keys untouched (RosterPort/overrides never touch team 279's name/city).

## Status

| Area | Status |
|---|---|
| Shell, logo, splash, Home games panel, Settings/About | done |
| AssetIndex (textures, both games) | done; NHL 24 sizes/formats/references pending own reader |
| Convert 27 matcher (variant map, Match textures/team/all) | done |
| Uniform Studio card grid | done (verification screenshots pending) |
| Live 3D viewer | **real NHL 24 skater meshes on both sides** (LOD0, bind pose, 1.98 m); helmet/head toggles, camera presets, Export PNG |
| Path auto-detection | done |
| Single-file EXE publish | not done yet |
| Own PS4 reader/writer, Apply/Restore | not started |
| Texture browser, Arizona→Mammoth, adidas→Fanatics | not started |
| Rosters A–E | not started (data files ready) |
| Mesh index / ports | not started |

## Real NHL 24 meshes (working)

Working since 2026-10-29: `tools\MeshExport` is built into `%LOCALAPPDATA%\CenterIceModdingSuite\fmthost` (a copy of the Worker
runtime's FMT DLLs with junctions to Plugins, SDK, ThirdParty, FrostbiteProfiles, Resources). It opens the game through FMT via the
Worker's existing `gamelinks\nhl24view` (FMT refuses folders without an explicit modify ACL; changing ACLs was declined, so the
existing link is reused), parses MeshSets with FMT (NHL22PS4Plugin IMeshSetReader) and decodes LOD 0 itself: per section each
vertex stream is stored for all vertices (Pos Float3, Binormal/Normal/Tangent Half4, BoneIndices UShort4, BoneWeights UByte4N,
TexCoord0 Half2), 16/32-bit indices start at VertexBufferSize. Output: GLB per piece in
`%LOCALAPPDATA%\CenterIceModdingSuite\meshes\nhl24` + export-report.json. FMT's own FBX export fails on NHL 24 (skeleton LocalPose
empty; mesh assets have no BoneNames). Pieces are listed in `src\Suite.Core\Nhl24Meshes.cs`. Y-up, metres, feet at 0.
Next: composite NHL 27 branding layers on base textures, real helmet/glove/skate/head textures, material panel, pants/sock
variants, skeleton for re-posing, then the suite's own MeshSet reader.

Original notes:

Found in the NHL 24 game (EBX listing via the Worker `assets` command): 4,783 SkinnedMeshAsset, 1,582 RigidMeshAsset,
1,534 CompositeMeshAsset, 66 SkeletonAsset. Player skeleton: `content/rigging/skeleton/skeleton_player`. Skater pieces, e.g.
`.../player/jersey/adidas/components/player_jersey_adidas_{fulltuck|notuck|lefttuck|righttuck}_{large|small}_lod{0,1,2}_mesh`,
`.../player/pant/common/components/player_pant_common_{loose|tight}{long|short}_lod0_mesh`, `.../player/sock/common/components/*`,
`.../player/glove/common/components/player_glove_common_{left|right}_mesh`, `.../player/skate/bauer/vapor/components/*`,
`.../player/helmet/bauer/reakt/components/player_helmet_bauer_reakt_150_mesh` (+ visors/cages), `.../shared/hands/common`, heads.
Plan: a small export tool on the FMT assemblies already on the PC (NHL24PS4 profile has CanExportMeshes; NHL22PS4Plugin implements
IMeshSetReader; FrostySdk MeshSetToFbxExport + ThirdParty\libfbxsdk.dll) exports LOD0 + skeleton to FBX through the read view;
the viewer loads them with three.js FBXLoader, both sides, real textures. Then replace with the suite's own MeshSet reader.

## Open questions

- C++ vs .NET: the user's Uniform Studio spec says "one distributable C++ EXE"; asked whether a single-file self-contained .NET EXE
  is acceptable (recommended). Pending.
- Whether the 3D viewer stays as an extra next to the card grid: the user later said the 3D viewer stays with real meshes.
- `UNIFORM_CONVERSION.md` (referenced) and Fanatics art are not on the PC.
- NHL24Kit source and the old "NHL 24 Modding Suite" source are not on the PC (E: drive?).

## Next steps (priority order)

1. Viewer polish: NHL 27 branding layers on base textures, helmet/glove/skate/head textures, material panel (Load colour
   texture, UV checker, sheen, lighting), pants/sock variants. Known small bug: two simultaneous NHL 27 decodes of the same
   texture race on the temp file (harmless, the other wins); add in-flight de-duplication in TextureImages.
2. Single-file EXE publish; verify Uniform Studio behaviours with screenshots.
3. Own PS4 TOC/bundle/RES reader → complete NHL 24 index; texture browser page.
4. Own writer (Apply/Restore), incremental, verified byte for byte against the Worker.
5. Arizona → Mammoth, adidas → Fanatics as index queries + batch rules.
6. Rosters A–E.

Keep this file and the Desktop copy in sync: `powershell -File tools\sync-handoff.ps1` (copies docs\HANDOFF.md to
`<Desktop>\CIMS_HANDOFF.md`).
