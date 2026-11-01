# Center Ice Modding Suite (CIMS) — HANDOFF

Last updated: 2026-10-29. Repo: `C:\dev\NHL27Convert` (git, branch `convert27-slice`, not pushed anywhere).
Earlier survey notes (pre-decision): `C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\docs\HANDOFF.md`.

## Catch-up 2026-10-31 (read-only review, no code changed)

- Last commit is still `c6b0568` (2026-10-29). Everything from "session 1/2" below (logo-shape matcher, team colours, Mod list +
  Apply, replacement folder in Convert 27, Textures & logos page, numbers panel) is **uncommitted** in the working tree.
- **Repo problem:** `.gitignore` line 427 `*.app` matches the `src\Suite.App` folder (core.ignorecase=true), so **no file of the
  app project has ever been committed** (0 tracked files: shell, pages, TexturesPage, logo/icon/splash). Fix: add
  `!src/Suite.App/` (or remove `*.app`) and commit. `run\` (an old copy of the build output, 36 files) is also untracked and
  should be ignored, not committed.
- **Apply safety problem:** `ModApply` runs the Worker's `apply`/`restore` against the *read view*. In the view, `Data\Gen4b` and
  `Patch\Gen4b` are junctions into the real game but `initfs`/`layout.toc`/`package.mft` are copies, so an apply would write CAS/TOC
  changes into the game while the Worker's own state and the view's copies diverge. **Do not press Apply** until it targets the
  real game folder (game closed, backup taken) or the suite's own writer replaces it.
- Build: `dotnet build NhlSuite.sln` OK, 0 errors; tests 15/15 pass. App runs: Convert 27 shows the real NHL 24 meshes both sides
  (9 GLBs, 1.98 m), NHL 27 crest now visible, team-coloured helmet/gloves; screenshot `docs\screenshots\2026-10-31-convert27.png`.
- Desktop and repo HANDOFF copies were identical before this note.

### Done 2026-10-31 (steps 1–2 of the agreed order)

1. **Repo fixed.** `*.app` removed from `.gitignore` (the app project is now tracked, 25 files), `/run/` ignored. Commits
   `bba6e8b` (app project + ignore fix + Uniform Studio nav entry removed), `17eab66` (10/29 session work, Apply disabled),
   `f486874` (docs). Not pushed.
2. **Apply made safe** (`src\Suite.Core\ApplyTarget.cs`, `src\Suite.Convert27\ModApply.cs`):
   - Writes only to `settings.ApplyTargetDir` (empty by default → Apply/Restore refuse with the reason). Never the read view.
   - The target must be one real game folder: `package.mft` + `Data\` + `Patch\`, no junctions/symlinks anywhere, TOC/CAS not
     read-only and openable for writing, no `<target>-mods`, and none of `shadps4`, `nhl24`, `nhl24_new`, `nhl24pc` running
     (the Worker itself only checks `shadps4`/`nhl24`, missing NHL24PC's `nhl24_new.exe`).
   - Before every apply: suite backup of every TOC + every CAS length + TOC SHA-256 in
     `%LOCALAPPDATA%\CenterIceModdingSuite\backups\<timestamp>\`; the first backup of a target is its stock baseline.
   - Restore: Worker `restore`, then compare with the baseline; any difference is fixed from the suite backup and re-checked.
   - FMT markers (`initfs_*.fmtbak`, `__FMTBackup\backup.bin`) are pre-created so FMT does not copy the 5 GB Patch folder into
     the target.
   - Buttons: toolbar Apply and the Mod list window's Apply/Restore are enabled only when the guard reports no problems; the
     tooltip/dialog lists the problems otherwise.
   - **Test on a full copy only** (`D:\CIMS-test\NHL24-merged` = `game\` + `game-UPDATE\Patch`, 38 GB; real game untouched),
     tool `tools\ApplyTest`: guard refused the real `NHL24PC` folder, `NHL24PC\game` and the read view (links); accepted the
     copy. Applied `arizona/adidas/home/sock_adidas_arizona_home_base_color` ← Utah twelve sock: 1 replacement, 2 references,
     `cas_05.cas` +4,287 bytes, `Data\Gen4b\contentsb.toc` changed. Restore: Worker restored 2 files, verified identical to the
     stock backup; full SHA-256 of all 125 files before vs after: **125/125 identical. PASS.**
   - Slots no TOC references in 1.60 are refused by the Worker ("no TOC refers to its data"), e.g. Boston `home/jersey_*_color`
     and `eight/sock_*_base_color`; the matcher should flag such slots (next step for the index/reader).
   - **Open decision for the real game:** NHL24PC keeps the base in `game\` and the update in `game-UPDATE\`, so there is no
     single real folder to apply to. Options: (a) point the emulator at one merged folder (like the old `CUSA37933`) and set
     that as ApplyTargetDir; (b) teach the suite's own writer to handle the split layout. Until then Apply on the real game is
     refused by design.
   - **Decided 2026-10-31: one merged game folder (option 2, a separate copy).** `nhl24_new.exe` takes no path arguments: it
     runs `<root>\game` and mounts `<root>\game-UPDATE` over it. So a second root was built:
     `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC-CIMS` = NHL24PC's emulator, tools and `user\` (saves, settings, shader
     cache) + `game\` = base `game\` with the whole `game-UPDATE\` laid over it (1.60 eboot.bin, sce_sys, Patch), no
     `game-UPDATE`. The original `NHL24PC\game` and `game-UPDATE` are untouched (clean fallback). CIMS settings:
     `ApplyTargetDir = ...\NHL24PC-CIMS\game` (backup `settings.json.before-cims-merged`); `Nhl24GameDir` still points at the
     original so ORIGINAL previews stay stock. The folder meets every guard rule (no links, Data+Patch, writable).
     **Waiting for the user to boot the game once from `NHL24PC-CIMS\Play NHL 24.bat`; nothing is applied until then.**
     `D:\CIMS-test\NHL24-merged` (test copy, no update eboot) can be deleted.

## NHL27Kit (friend's tool, reviewed 2026-11-01)

`C:\Users\devin\Downloads\NHL27Kit-1.0.0\NHL27Kit-1.0.0\NHL27Kit.exe` (1.0.0, "Made By Sonku"): self-contained single-file
.NET WPF app (NHL27Kit.dll + NHL27Kit.Core.dll + BCnEncoder, no FMT) for **NHL 27 PS5 on KytyPS5**; edits the NHL 27 game
files in place, keeps original bytes in `%LOCALAPPDATA%\NHL27Kit\mods` for Restore, needs `oo2core_9_win64.dll` (Oodle) from FMT.
Reviewed from its README and bundle structure only (not run, not decompiled; reusing its code needs the author's source and
permission). Pages: Play (launch emulator + settings + builds), Explorer (all assets, previews, hex, export), Textures (gallery,
full size, PNG/DDS export, replace incl. array layers), Sounds (play/export/replace; ATRAC9 decode, Opus encode), Mods (apply,
restore, export/import `.nhl27mod` packs), Roster (players, bios, ratings, portraits, NHL API sync, lines), Database (every
table + text DBs, length limits), Uniforms (3D on the game's jersey/pants/sock models, kit/logo replace, helmet/glove colours,
jersey slots add/move/rename/remove, legacy jersey converter, Jersey Studio from create-a-team templates), Teams (names, abbrs,
colours, arena, rivals, affiliates, logos all styles, centre ice incl. TheFaceoff.net converter), Scorebug (preview; its saving
crashed the game when loading a match). Ideas for CIMS: Play page, mod packs, export everywhere, hex/explorer, jersey slots,
Jersey Studio, rink/centre-ice tools, NHL API sync (spec E). Its Core (own Frostbite reader/writer without FMT) is what CIMS
step 5 needs, but for PS5/NHL 27; NHL 24 is PS4 (different tiling, Kraken).

## Legacy jerseys and slot mapping (investigated 2026-11-01, read only)

- NHL 27 has no textures named `*legacy*` for uniforms (only arena props: dashers, concrete, stairs...). Its `rbk` and
  `nineties` sets have the same 11 slots as NHL 24's. Every NHL 27 set also ships a **plain `jersey_*_color`** (crest and
  branding baked in) next to the layered `_base_` + branding textures. Plain NHL 27 textures share NHL 24's UV layout (crest,
  hem, sleeves, collar in the same places; designs differ), see `docs\screenshots\2026-11-01-legacy-layout.png`.
- Branding arrays: 6 jersey layers and 3 pant layers in both games. **Layer meaning is per jersey, not global**
  (`docs\screenshots\2026-11-01-slot-layers.png`):
  - Boston `twentythree` is the same jersey in both games: layers identical 1:1 (bear, orange shield, stripes, EPW, BRUINS x2).
  - St. Louis NHL 24 home [crest, shield, adidas, EPW, Stifel, shield] vs NHL 27 `fourteen` [crest, Stifel gold, Fanatics,
    shield, Stifel, shield purple]; pants 24 [crest, blank, shield] vs 27 [shield, "SL", shield purple].
  - Arizona NHL 24 home [crest, shoulder, shoulder, adidas, shield, EPW] vs Utah `twelve` [crest, shoulder, shoulder,
    Fanatics, shield, SME Steel]; pants 24 [blank, blank, shield] vs 27 [U logo, shield].
  - So the slot map must be **per jersey by role** (crest, shoulder L/R, brand, shield, patch, sponsor, wordmark), not by
    index. Proposed: auto-tag roles by shape against reference logos (NHL shield, adidas, Fanatics, EPW are the same image
    league-wide; crest = layer 0; repeated image = shoulder pair), store per-folder role tables in `data\slot_map.json`,
    matcher maps role-to-role at 100%, unmapped NHL 27 roles listed as "no NHL 24 slot", editable table with thumbnails.
- Jersey sets: NHL 27 has 3–6 more sets per team (`ten`–`fifteen`, the new 2026–27 kits incl. new default home/away);
  NHL 24 cannot get new texture folders (no assets), so extra NHL 27 kits must replace existing NHL 24 sets (via the variant
  map / jersey rows). Arizona exists only in NHL 24 and Utah only in NHL 27.

### Role-based slot map (built 2026-11-01)

- `src\Suite.Convert27\SlotRoles.cs`: labels each branding layer (jersey 0–5, pants 0–2) as crest, shoulder, brand, shield,
  patch, sponsor or blank. League-wide logos by 32×32 alpha silhouette against `data\logo_refs.json` (shield, adidas,
  adidas stripes, Fanatics, End Plastic Waste; same logo = 0.0 apart, different logos ≥ 46, accept ≤ 24); blank = empty or
  single-colour placeholder; layer 0 = crest; repeated image = shoulder pair; rest = sponsor/wordmark. Map = role to role,
  k-th to k-th, at 100%; brand adidas → Fanatics; NHL 24 layers without a counterpart stay stock; NHL 27 layers without a
  slot are listed (`noSlot27`).
- Stored per (NHL 24 set, NHL 27 set, garment) in `%APPDATA%\CenterIceModdingSuite\data\slot_map.json`; labels and links
  edited by hand are marked manual and kept on re-match.
- Matcher: new pass 0 in `MatchLogoShapes` uses the slot map first (confidence 100%, reason lists every link); the old shape
  and index passes only fill what is left.
- UI: Convert 27 → "Slot map…" window: per garment, every NHL 24 layer with thumbnail, role (editable), the NHL 27 layer
  that fills it (editable, or "keep NHL 24") with thumbnail, and the NHL 27 layers with no NHL 24 slot. "Save and re-match".
- Verified on real layers (`tests\Suite.Tests\SlotRolesTests.cs`, runs where the decoded images exist): St. Louis NHL 24 home =
  crest, shield, brand(adidas), patch, sponsor, shield; NHL 27 Fanatics found; adidas→Fanatics, shield→NHL 27 shield (layer 3),
  EPW stays stock, NHL 27's second Stifel = no slot. Arizona home = crest, shoulder, shoulder, brand, shield, patch → Utah
  crest→crest, brand→Fanatics, shield→shield; Utah's SME sponsor has no slot. Each NHL 24 layer has a fixed placement on
  the jersey (user, 2026-11-01: End Plastic Waste is the tag on the very back of the jersey), so a logo put into another
  role's layer appears at that layer's spot: never move a sponsor into the patch layer. The patch layer stays stock.
- Variant map format 2 (`tools\make_variant_map.py`): `extraKits` per team = NHL 27 adidas sets with no NHL 24 folder (besides
  the default home/away), each with `replaces24` = suggested old NHL 24 adidas alternate (never home/away/retro, sets NHL 27
  still ships last, each used once). Shown in the matcher's variant line; nothing is applied from it yet. User copies of
  format 1 are backed up as `variant_map.json.before-format2` and replaced.

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
  result; Shaded / Flat texture / Fabric regions (alpha legend 64/127/191/255 vs 51/102/153/204) / Turntable / Numbers panel.
  Updates live on every row change. **No placeholder mannequin**: only the real NHL 24 meshes are drawn (if the GLBs fail the
  viewport stays empty and the note says so).
  - Jersey/pant/sock colour uses the plain `_color` art (crest and branding are baked into it for the viewer); the
    `_base_color` map is sent separately as the fabric-region map so "Fabric regions" keeps working.
  - Base slots get a **composited PNG** (plain `_color` RGB + `_base_color` alpha) written to
    `%APPDATA%\CenterIceModdingSuite\images\composite\`, so a sponsor that only exists in the colour map (Utah's SME) is still
    written and the fabric-region alpha survives.
  - Helmet and gloves use the team's **NHL 27 palette** (`data\team_colors_27.tsv`, fallback `data\team_colors.tsv`), so
    Arizona→Utah dresses in Utah black/blue instead of Arizona red/green. Skates stay near-black.
  - Helmet sponsor (`helmetlogo_*_<n>_color`, e.g. meijer for Detroit) is drawn as two curved spherical caps high on the
    left/right of the helmet shell, so it wraps instead of floating on a card.
  - Numbers + name on the back: a **0.38 × 0.46 m** panel with the name above a two-digit number whose glyphs are copied from
    the game's font atlas (so a replaced font shows). The name uses a bold system face until the atlas letter cells are mapped.

Fixed 2026-10-29 (session 1): picking an NHL 27 folder from the proposal dropdown commits the folder path (WPF was pushing the
`FolderProposal`'s ToString()); the right-side viewer re-pushes after an NHL 27 decode so it shows new art instead of stale
stock; a dropped file updates the Source thumbnail; NHL 27 decodes are de-duplicated in-flight (no more .tmp.png race).

## 2026-10-29 session 2 — matcher intelligence, viewer, apply

**Matcher**
- Numbered logo layers are no longer matched by name/index blindly. The branding **arrays are matched first** (the game reads
  logos from the arrays, not the individual `_N_color` textures), each NHL 27 layer is **claimed at most once**, and the
  individual `_N_color` rows copy their array layer's answer.
- Inside an array layer: **logo shape** match first (32×32 alpha silhouette, mean absolute difference ≤ 24 — recolours still
  match), then a **manufacturer/logo swap** by layer index only when exactly one layer is left over (that is the adidas →
  Fanatics case; adidas is layer 2 and Fanatics is layer 2).
- `Matcher.Propose` leaves numbered logo slots stock with the reason "logo layer: layer order differs between games" until the
  shape pass fills them. `LogoMatcher` (new, `src\Suite.Convert27\LogoMatcher.cs`) does the silhouette comparison.
- Picking a different NHL 24 folder **commits on selection** (WPF editable ComboBox previously waited for LostFocus, so the grid
  looked like it ignored the new folder).

**Mod list / Apply (new)**
- Convert 27 toolbar: **Mod list…** opens `ModListWindow` (every saved mod, each slot → source, delete, apply, restore) and
  **Apply…** writes the whole mod list.
- `ModApply` builds `apply-plan.json` from `mods\*.json` and runs the **NHL24Kit worker** `apply`/`restore` through the read view
  (`Worker.RunRawAsync` added; the read-only helpers still refuse writes). The runtime worker in
  `%LOCALAPPDATA%\NHL24Kit\runtimes\…` only supports `list|refs|apply|restore|status` — no `update`/`dryrun`/`snapshot`.
  **Not yet exercised against the real game.**

**Replacement folder**
- The Uniform Studio's one unique feature was folded into Convert 27: a **Replacement folder (DDS/PNG)** picker +
  **Match from folder**, using the same name/role rules (exact 100%, same role 90%, same garment+role 70%; numbered layers
  exact only).

**Textures & logos page (new)**
- `src\Suite.App\Pages\TexturesPage.xaml`: browse any folder of **either** game from the index, thumbnails, "Choose
  replacement…" per slot, "Add replacements to mod list". Status/progress at the bottom. (Fixed a cross-thread crash: the
  Worker's per-file progress callback now marshals to the UI thread.)

**Viewer**
- Real meshes only (placeholder mannequin removed); `viewer {"type":"meshes","count":9,"height":1.98,"width":1.24}` is logged
  when three.js actually loads the GLBs, so a mesh-load failure is visible in `suite.log`.
- Numbers panel (both sides' font atlas), curved helmet sponsor caps, team colours from the NHL 27 palette, name+number on the
  back (see the bullets above).

**Build gotcha (important)**
- The app loads the viewer from **its own** `bin\Debug\net8.0-windows\viewer`, which is only refreshed when **Suite.App** builds.
  Building only `Suite.Convert27` leaves the running app on a stale viewer — always `dotnet build NhlSuite.sln` after viewer edits.

**Data added**
- `data\team_colors_27.tsv` (NHL 27 stockteamcolorstable, 68 teams; index 22 = Utah Mammoth `1B1B1B 4F86BC`) and
  `data\team_colors.tsv` (NHL 24, 67 teams). Both hand-editable; the 27 table wins.

## Uniform Studio page (built, then retired)

**Superseded by Convert 27** (session 2): the replacement-folder matching moved onto the Convert 27 page, so this page is no
longer part of the workflow. It still compiles and the sidebar entry is still there; delete the nav entry when convenient.
Kept for reference:
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
| Convert 27 matcher (variant map, Match textures/team/all) | done; logo layers by alpha shape + manufacturer swap |
| Uniform Studio card grid | **retired** — its replacement-folder matching moved into Convert 27 (page still compiles, nav keeps it) |
| Live 3D viewer | real NHL 24 skater meshes both sides; team colours (NHL 27 palette), helmet sponsor caps, numbers+name on the back, Numbers panel, camera presets, Export PNG |
| Textures & logos browser | done (browse either game by folder, previews, replace, save to mod list) |
| Mod list window + Apply/Restore | done, run through the NHL24Kit worker (stopgap); not yet exercised on the real game |
| Path auto-detection | done |
| Single-file EXE publish | not done yet |
| Own PS4 reader/writer | not started |
| Arizona→Mammoth, adidas→Fanatics | texture side covered (team map + logo shape/index matching); roster side not started |
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

1. **Verify Apply/Restore on the real game** (back the game up first). The mod list + worker path is wired but has never been
   run end to end; the runtime worker has no `dryrun`/`snapshot`, so verification means comparing game files before/after.
2. **Viewer polish**: map the font atlas A–Z cells so the back name uses the real font; sleeve numbers; real helmet/glove/skate
   textures (currently team colours); material panel (Load colour texture, UV checker, sheen, lighting); pants/sock variants
   (`{fulltuck|notuck|lefttuck|righttuck}_{large|small}`, `{loose|tight}{long|short}` all exist, differing only in pose/fit).
3. **Single-file EXE publish** (`dotnet publish -r win-x64 /p:PublishSingleFile=true`), verify it runs on a clean profile.
4. **Own PS4 TOC/bundle/RES reader** → complete NHL 24 index (sizes, formats, chunk ids, every TOC/bundle/RES/duplicate
   reference) so the writer stops depending on the Worker.
5. **Own writer** (Apply/Restore), incremental, byte-for-byte against the Worker, all 15 in-game rules; replace `ModApply`'s
   worker call with it.
6. **Rosters A–E** (specs below; data files ready).
7. Sponsors: expose the `helmetlogo_*` texture as its own matcher row, and flag colour-map-only sponsors (Utah SME) so nothing
   looks silently missing.

Keep this file and the Desktop copy in sync: `powershell -File tools\sync-handoff.ps1` (copies docs\HANDOFF.md to
`<Desktop>\CIMS_HANDOFF.md`). If script execution is blocked, `Copy-Item docs\HANDOFF.md <Desktop>\CIMS_HANDOFF.md -Force`.
