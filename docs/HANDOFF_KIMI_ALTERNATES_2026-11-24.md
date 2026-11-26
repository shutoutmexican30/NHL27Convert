# Handoff for Kimi: NHL 27 alternate uniforms in NHL 24 (2026-11-24)

Owner: Devin Sutton. Self-contained. Read this before touching uniforms. It replaces sections 2.2, 4 and 6 (NHL part) of
`docs/HANDOFF_KIMI_2026-11-22.md`. The older long history is in `docs/HANDOFF.md`.

## 1. Ground rules (owner's, do not break)

1. **Never write to the main game**: `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC`. (`D:\NHL24PC_V3.2` from older notes
   does not exist.) Never write to `D:\CIMS-test\prospects-copy` (the DB/mesh thread's copy; the Suite's settings point
   there). Work only on `D:\CIMS-test\alts-copy\NHL24PC`. The tools refuse other targets.
2. **The game must be closed** before any write. If the owner has the copy open, wait for the process to exit
   (`nhl24_new.exe` under the copy). Never close it for him.
3. **NHL only for now.** AHL, CHL, ECHL, Europe and the 7 new empty-slot teams are parked until the owner asks.
4. **No change to existing rows**: never touch defaults, `isdefault`, `variantleadingscorer`, `jerseyindex` of NHL 24's
   own rows. New kits are extra variants only. The Fanatics-as-default swap is cancelled.
5. **Kits look like NHL 27, not like Anaheim**: nothing of the donor (Anaheim home) may show. Logo slots NHL 27 has no
   layer for are cleared.
6. **Sponsors**: an alternate gets a sponsor patch **only if NHL 27 has a sponsor spot on that kit**, at NHL 27's spot
   (owner, 2026-11-24: "if the alternate uniforms do not have sponsor slots do not add them"). Do not change the 64
   Fanatics kits' sponsors without asking (15 of them have no sponsor spot in NHL 27; owner has not decided).
7. **Names are NHL 27's own** (exact English strings, see 4). Shared labels where NHL 27 uses them.
8. **Numbers are NHL 27's own** for each alternate, fitted into NHL 24's font layout and verified per glyph; when the fit
   fails, the team's NHL 24 sheet stays (never a half-fitted sheet).
9. Every write: backup first, read back every copy, restore on failure, KIT CHECK on every kit. Never FMT.
10. Salary cap patch: do not apply or copy it anywhere (the copy has `user\patches\nhl24.xml` = the `.off` version).
11. Report plainly: what was verified, what was not. Keep owner reports short.

## 2. Current state

| Item | State |
|---|---|
| Test copy | `D:\CIMS-test\alts-copy\NHL24PC` (full copy of the main game made 2026-11-24, shader cache copied) |
| Pre-kit backup | `D:\CIMS-test\fullkit-backups\20261124-031541` (copy before any alternate) |
| Installed | 46 NHL alternates (27 teams), KIT CHECK 46/46 on the last run, NHL 27 names, audit of 110 kits clean of donor art |
| Done 2026-11-24 10:33 | The sponsor-rule reinstall finished: KIT CHECK 46/46, names written and read back, cross-check of 110 kits: no donor art, alternates carry only NHL 27 logos + hem tag. Fanatics report-only: LA home/away and Pittsburgh away carry a separate pants-logo layer (LA on purpose in the earlier recipe; Pittsburgh not reviewed). Backups: `20261124-095753` (before the kits of this run), `20261124-102159` (before names). |
| Was pending | A reinstall that applies the sponsor rule (9 kits had sponsors NHL 27 does not show: Boston 15 "2025-26 Stadium", Chicago 13 "2024-25 Classic", Dallas 13, Edmonton 13, Florida 13, Minnesota 13 "25th Anniversary", Ottawa 12, San Jose 12, Tampa 21 "2023-24 Alternate"). It was queued to start when the owner closes the game; logs `Debugging\altport\{restore4,apply4,names4,check4}.log`. Check them first. |
| Space | Kit data is in **new** CAS files: Data `nhl_installpackage_05\cas_17.cas` (~1.3 GB), Data `nhl_installpackage_02\cas_08.cas`, Patch `nhl_installpackage_05\cas_13.cas`. `cas_05.cas` stays 3,690,459,199 bytes. |
| Main game | Untouched. Not installed there yet (owner has not approved). |
| Code | **Not committed**: the working tree also holds another thread's uncommitted edits. Commit only your own files, with care. |

## 3. Tools (repo `D:\dev\NHL27Convert`, build with `dotnet build tools\<Name> -c Release`)

- `tools\AltPort`: plans, builds and installs the alternates.
  `AltPort --target <copy> [--teams a,b] [--plan-only] [--apply] [--restore-first <backup> [--restore-only]]`.
  Plan = NHL 27 `stockteamjerseys` (`reference\nhl27-full-csv`) style 2 active rows of teams 0-31 that NHL 24 lacks,
  minus NHL 27's Fanatics home/away (`variant_map.json`). Target variant = NHL 27's variant when both the row and the
  asset folder are free, else the highest free 2-25 (Tampa's "four" had an unused NHL 24 folder: it went to 21). Writes
  `Debugging\altport\plan.tsv` and `alternates.mod.json`. `--restore-first` restores a suite backup and deletes kit CAS
  files the backup does not know.
- `tools\AltNames`: NHL 27 names onto the installed kits. `--names Debugging\altport\names27.tsv [--apply]`. Changes only
  the kits' `locstring` (both DBs) and their `VINTAGE_2_<teamartid>_<variant>` English entries (in place; adds only what
  is missing). Also `--check` (read-only KIT CHECK of all alternates + 64 Fanatics kits) and `--fix-sponsor team/word`
  (moves an installed stamp's sponsor spot to NHL 27's).
- `tools\LocDump27\extract_loc27.py` + `loc27_lookup.py` (Python, run with `-I`): read NHL 27's PS5 English table
  read-only from `D:\Archives\PS5\PPSA34063\...\PPSA34063-app0` using the NHL 27 viewer's readers
  (`D:\Tools\NHL27TextureViewer\frostbite`). NHL 27's legacy index is a `globals.toc` chunk (28-byte records, chunk-id
  table, string pool); its loc DB has two tables with rows (key, text, hash). Never write to the dump.
- `tools\FontDiag`: read-only diagnostics: why NHL 27 numbers do not fit (`<game> team/word...`), `--dump` installed
  textures to PNG, `--loc KEY...` / `--loc text=...` to look up English strings.
- `tools\JerseyDump <game> nhl_game [--team N]`: jersey rows, for before/after comparisons with the main game.

### Full redo on the copy (game closed)
```
AltPort  --target D:\CIMS-test\alts-copy\NHL24PC --restore-first D:\CIMS-test\fullkit-backups\20261124-031541 --restore-only --apply
AltPort  --target D:\CIMS-test\alts-copy\NHL24PC            (dry run: must end "DRY RUN: 46 kits ... Nothing was written")
AltPort  --target D:\CIMS-test\alts-copy\NHL24PC --apply     (~25 min; ends with KIT CHECK n of 46)
AltNames --target D:\CIMS-test\alts-copy\NHL24PC --names Debugging\altport\names27.tsv --apply
AltNames --target D:\CIMS-test\alts-copy\NHL24PC --names Debugging\altport\names27.tsv --check
```
The installer refuses a kit folder that already exists, so a change to installed kits means this full redo.

## 4. How the pipeline makes an alternate (what each fix guards)

- Built like the 64 Fanatics kits: Anaheim home donor (`KitDonor.Prefix`), NHL 27's finished jersey/pants textures
  painted on, NHL 24 fabric zones and fold shading, NHL 24 stock shine (NHL 27 coefficient maps only if the owner asks),
  End Plastic Waste back-hem tag, team helmet logo from NHL 24.
- `NewUniformExport.BuildAlternatesAsync`: the structure is the team's NHL 24 home (dark kit) or away (light kit).
- `NewUniformAddition.KitLabel` (e.g. `alt-twelve`): per-kit settings key, so the Fanatics kits' `font_colours.json`
  entries and temp files never apply to alternates.
- `Nhl27Numbers`: NHL 27 number sheet via `FontRemap` + `Verify`; fallback logged as `NUMBERS FALLBACK`.
- Sponsor (`KitDonor`): NHL 27 spot names vary (`sponsor`, `right_shoulder_sponsor`, `front_sponsor`): normalised by
  `Nhl27SponsorXml`. No NHL 27 spot on an alternate means no sponsor. `UniformHotspots.CopyBrandSpotInPlace` pads values
  to the same length ("90.0000" over "180.0000" becomes "090.0000"), so no EBX offset moves.
- Donor logo arrays: every jersey/pants branding array of the donor is explicitly filled or cleared (19 kits showed the
  Ducks logo on the pants before this fix).
- `ZoneMap` never maps to the Adidas shoulder-yoke zone (dimples on NJ/Tampa).
- Equipment rows: `NewUniformEquipmentPlanner` allows any new variant 2-25 (was 24/25 only).
- Names: `AddJerseyNames` honours `LocString`/`JerseyName` per kit (2 = shared "Alternate", no entry).
- **KIT CHECK** (`FanaticsKitCheck`) fails on: Adidas yoke zone, missing back-hem tag, captain letter on the sponsor,
  crest slot with art, sock/hem past NHL 24 limits, a number sheet that is not the team's (unless `nhl27Numbers`),
  **any logo slot still showing the donor's Anaheim art**, **a sponsor on an alternate whose NHL 27 kit has none**.
- Space (`FullKitInstall.KitCas`): kit payloads and manifests go to a new `cas_NN.cas` per install-package folder (next
  free number, limit 4 GiB - 64 MB); bundle records are rebuilt with explicit location words (`BuildRecord`). Proven
  in game: the V3.2 Russia/Belarus mod also adds a new CAS (Patch package 00 `cas_02.cas`). A rollback deletes the new
  files. External/loose mods are not possible in this emulator build (whole-file overlay only; see the 11-24 report).

## 5. Names (from NHL 27's English table; `Debugging\altport\names27.tsv`)

NHL 27 label codes: 0 Home, 1 Away, 2 `ALTERNATEJERSEY` "Alternate", 6 per-kit `VINTAGE_2_<art>_<variant>`,
10/11/12 `OLD_HOME_ADIDAS` / `OLD_AWAY_ADIDAS` / `OLD_ALTERNATE_ADIDAS` "2023-24 Home/Away/Alternate" (NHL 24 does not
know 10-12: use code 6 with the text). 21 kits use "Alternate", Chicago's variant 10 uses "Home" (as NHL 27), 24 have
their own names (23 NHL 27 per-kit names plus Tampa's code-12 label) (e.g. Detroit 12 "2025-26 Centennial", Minnesota 13 "2025-26 25th Anniversary", San Jose 13 "2025-26
Heritage", Tampa 21 "2023-24 Alternate"). English table: 65,535-row cap per language file; about 13 rows free now.

## 6. Open items, in order

1. Check the pending reinstall's logs (section 2). Expect KIT CHECK 46/46 and `check4.log` "0 with donor art left".
   Note `--check` compares a light alternate's socks to the team's *home* kit (Carolina 11 shows a false sock line).
2. Four kits keep the team's NHL 24 numbers: Columbus 13, Dallas 12, Edmonton 13 (FontRemap.Verify: one glyph box is a
   few px larger because `Glyphs()` merges ink across the band edge; fix the detection, not the check, and prove it on
   Florida first, whose sliver the check was made for), Detroit 13 (NHL 27's sheet has no letters: keep NHL 24's).
3. The owner boots the copy and reviews. Only then, and only when he says so, plan the main-game install (fresh backup;
   the kit CAS design applies; read back; KIT CHECK).
4. Owner's open decision: same sponsor rule for the 15 Fanatics kits without an NHL 27 sponsor spot?
5. Parked: AHL/CHL/ECHL/Europe (NHL 24 uses `league/<league>/<team>/rbk/<set>`, styles 0/1; the pipeline is NHL/adidas/
   Fanatics only: ~36 hard-coded spots and a same-team rbk donor needed) and the 7 new teams.
6. Commit the work (own files only) when the tree allows.

## 7. Boot test to give the owner

1. Close the game. Run `D:\CIMS-test\alts-copy\NHL24PC\Play NHL 24.bat`, PLAY.
2. Play Now, pick a team (Detroit, Boston, Chicago, Tampa, Buffalo), press X for the jersey picker, scroll to the new
   kits: check the name, the numbers, the pants (no Ducks logo), the sponsor (only where NHL 27 has one), the C/A.
3. If a new kit does not show: a saved roster may be overriding the default one.

## 8. Update 2026-11-24 ~12:45 (Kimi, afternoon)

- **Detroit alt-twelve shoulders — RESOLVED 16:55**: the NHL 27 sheet was correct as-is. The first "fix" (patch swap) was
  wrong because of a wrong texture-mirroring assumption; the owner's in-game test settled it: **texture x-left renders on
  the player's LEFT shoulder** (no mirror). Correct layout (now installed, verified via TextureSlot export): hexagon
  "P"/Priority Health sponsor at texture x≈0.40 = player's LEFT, "100" winged wheel at x≈0.60 = player's RIGHT. The export
  cache `%LOCALAPPDATA%\CenterIceModdingSuite\nhl27png\..._detroit_adidas_twelve_jersey_...` is back to the untouched NHL 27
  originals (my swapped sheets kept at `D:\CIMS-test\alts-copy\work\detroit-patch\swapped-full\`, originals as before in
  `...\originals\`); no durable override needed. Full cycle re-run 16:12–16:53: restore from
  `D:\CIMS-test\fullkit-backups\20261124-031541` (0 diffs), `AltPort --apply` → **46/46 KIT CHECK pass**, then
  `AltNames --apply` → "NAMES WRITTEN AND READ BACK" (backup `20261124-164321`). Verify exports: `work\detroit-patch\verify2`
  (swapped, wrong) vs `verify3` (originals, correct).
  Reinstall rule: AltPort only installs *missing* kits, so a same-copy refresh needs the restore-first cycle:
  `AltPort --restore-first <pre-kit backup> --restore-only --apply`, then `AltNames --apply/--check`.
- **Detroit alt-twelve overlap fix (17:05, after in-game report)**: the real defect was a DUPLICATE small
  "100" in the jersey color/normal/coeff sheets at abs box (1134,913)-(1324,1133) (next to the P hexagon at
  ~(811,1027)); NHL 24's shoulder UV renders both onto the left shoulder over the Priority Health patch. The
  RIGHT-shoulder "100" is the big centennial patch in the **branding layer 1** (`..._branding_jersey_..._1_color`),
  untouched. Fix: erased the small 100 (fill copied from clean red below) from the three jersey sheets and wrote
  them straight into the installed game with `TextureSlot applymany` (14 s, backup `... CenterIceModdingSuite
  \backups\20261124-170234`, read-back identical); the cache copies in `%LOCALAPPDATA%\CenterIceModdingSuite
  \nhl27png` were updated to match, so a future full AltPort cycle keeps the fix. Verified by re-export
  (`work\detroit-patch\verify4`). NOTE: if a kit is ever rebuilt from a FRESH NHL 27 re-export (not the cache),
  the duplicate 100 can come back — the durable fix is stopping the exporter from laying the small 100 there.
- **Detroit alt-twelve patch fix — FINAL (17:25)**: owner ruling: the Anaheim donor's logo layers are redundant because
  the logos are already painted in the colour map, so the baked-in patches are the bug, not the layers. The jersey has
  TWO sheet sets: `jersey_..._color/normal/coeff` and `jersey_..._base_color/base_normal/base_coeff` — the pipeline
  bakes adidas mark + NHL shield into the installed **base** sheet (the raw cache export lacks them), so any base-sheet
  edit must start from an export of the *installed* texture, never the cache file. Final state: P hexagon box
  (736,947)-(886,1107) and small-100 box (1134,913)-(1324,1133) erased from color/normal/coeff AND base_color/base_normal
  (fill = fabric copied from just below each box); `base_coeff` refused by TextureSlots ("base coeff sheets stay stock")
  and the cache's base_coeff was re-synced to stock. Applied via `TextureSlot applymany` (10 s; backups
  `...backups\20261124-172052` (rollback point used) and `20261124-172515`; verify6 export confirms both sheets clean,
  adidas/shield/crest intact). Expected in-game: both Centennial shoulders bare red.
- **Detroit alt-twelve patches — SUPERSEDED, see FINAL below (18:18)**: this entry had the owner's left/right backwards
  (the stamp's `right_shoulder` id 12 at tx=0.391 renders on the side the OWNER calls left). The placement described here
  (P centred x1247, 100 centred x801) was WRONG in-game; the owner boot-tested and reported it flipped. Correct final
  state: **P hexagon on the owner's LEFT = sheet box (736,947)-(886,1107), centre ~(811,1027); the 100 on the owner's
  RIGHT = sheet box (1134,913)-(1324,1133), centre ~(1229,1023)**. Kept for the stamp-reading lesson:
  `right_shoulder` hotspot id 12 at tx=0.391 (x≈801), `left_shoulder` id 13 at tx=0.609 (x≈1247), both y≈1024
  (rot 90°/270°). NHL 27's sheet has them the other way round (P at x811, 100 at x1235), which is why the straight port
  put the 100 on the left. The 16:58 overlap happened because the two stacked sheets (color vs base_color) briefly held
  OPPOSITE layouts. Final fix: P hexagon painted centred on (1247,1024) and the 100 centred on (801,1024) — the
  original NHL 27 patch art boxes moved — on all five writable sheets (color/normal/coeff + base_color/base_normal;
  base_coeff stays stock by rule). Written via TextureSlot applymany (backup `20261124-181839`, read-back identical,
  verify8 confirms). Cache color/normal/coeff mirrored to match so a full rebuild keeps it; a rebuild bakes base sheets
  from cache art, so if the pipeline re-bakes base from the RAW cache export this placement must be re-checked.
  (Read the stamp any time with UniformPhysicalLayouts.ReadStamp + UniformHotspots.Capture.)
- **Patches page added to the suite** (`src/Suite.App/Pages/PatchStudioPage.cs`, nav "Patches" after "Texture suite"):
  pick team + set → jersey colour sheet on a zoomable canvas; drag a box, then Erase (click clean fabric to copy over
  it), Move (click new spot; old spot filled from below), or Add picture (PNG/DDS); every edit mirrors onto the
  normal + coeff sheets; Undo (30 steps); Export PNG; "Apply to game" writes through `TextureSlots.Apply`
  (backup + read-back, refuses a running game). Built clean 17:30; page constructor is compile-verified but not yet
  UI-tested. Known limits: added pictures paint the colour sheet only; box ops are straight rectangles.
- **"Sync failed: oo2core_9_win64.dll not found" fixed** (an outside user hit it on the Teams & roster page). `Oodle.cs`
  now also searches: the `CIMS_OODLE_DLL` env var, EA/Origin/Epic/Steam game-store folders (EA-looking names; Steam name-
  filtered), every registered game's install folder (uninstall registry, 64+32-bit views, dll at root or one level down),
  then plain Windows search (exe folder + PATH). The not-found message now tells the user exactly what to copy and where to
  put it (next to `CenterIceModdingSuite.exe`). Verified on this machine: finds `D:\Tools\FMT`, Steam War Thunder, and
  College Football copies; `Ensure()` loads and resolves both exports. Suite rebuilt: Release exe 2026-11-24 12:25.
- **Dallas / Cleveland Barons question answered**: NHL 27 has `VINTAGE_0_9_9` = "1976-77 Away" for Dallas (the Barons'
  1976-77 season; the Barons merged into the North Stars, whose successor is Dallas). **NHL 24 already has the same slot
  (style 0 variant 9) with the same name — it is a stock NHL 24 vintage jersey, not one of ours.** Our Dallas adds are only
  style 2 variants 12 ("2024-25 Alternate") and 13 ("Alternate"), correctly named per NHL 27's own table. No wrong-folder
  kit. (Art-identity of stock slots not texture-compared.)
- Sponsor work confirmed done: `apply4.log` KIT CHECK 46/46 at 10:21, names read back 10:30 (`names4.log`), `check4.log`:
  0 donor art; the 4 FAILs are known (Carolina 11 false sock line, see §6.1; LA/Pittsburgh Fanatics pants-logo layers =
  owner decision §6.4).
- Helpers in `D:\CIMS-test\alts-copy\work\`: `loc27\eng_us.db` (NHL 27 extract) + `loc24\eng_us.db` (alts-copy extract),
  `loc24_lookup.py` (same as `tools\LocDump27\loc27_lookup.py` but NHL 24's row order is text,hash,key — the stock script
  only handles NHL 27's order), `oodle-test\` (Candidates/Ensure harness), `build-suite.cmd` (build helper: the Kimi Bash
  session lacks the `ProgramFiles`/`ProgramFiles(x86)` env vars, which kills NuGet restore with "Value cannot be null
  (Parameter 'path1')"; the script sets them and shuts down stale build nodes first).
- **Detroit alt-twelve patch fix — FINAL (19:43, owner-verified layout)**: the two sheet patches were swapped to the
  owner's ruling — **Priority Health "P" hexagon on the owner's LEFT shoulder (sheet box (736,947)-(886,1107)), Red Wings
  centennial "100" on the owner's RIGHT (sheet box (1134,913)-(1324,1133))** — on color/normal/coeff + base_color/
  base_normal. Applied via TextureSlot applymany (backup `20261124-194300`, read-back identical); staged PNGs in
  `D:\CIMS-test\alts-copy\work\detroit-patch\finalswap\` (+`apply.tsv`). The three cache sheets
  (`%LOCALAPPDATA%\CenterIceModdingSuite\nhl27png\content_character_skater_league_nhl_detroit_adidas_twelve_jersey_...
  _{color,normal,coeff}.png`) were overwritten with the corrected finals, so every later AltPort rebuild keeps this layout.
- **Evening rebuild #1 (20:33) and the fixes it proved** (`Debugging\altport\apply5.log`): owner reported the Rangers
  Stadium Series alt (NHL 27 `nyr/adidas/thirteen`, installed as NHL 24 `nyr/adidas/thirteen` variant 13) with wrong
  hemming, an "End Plastic Waste" globe on the back hem, and a wrong patch. Findings: (1) `HemFit` was still
  `Enabled = true` despite the Nov 12 ruling (Boston) — it shifted hem stripes on 18 alternates (nyr thirteen's back hem
  99.8%→96.0%); (2) `KitDonor.UsePaintedTextures` injected the EPW globe into every kit's tag slot via `StandardTag()`
  fallback, covering NHL 27's own painted 100-apple tag; (3) the Rangers "wrong patch" is NHL 27's own 100-apple at
  branding layer 6 = the donor's front sponsor slot per NHL 27's stamp data — left in place, owner's call after viewing.
  Code changes: `HemFit.Enabled = false`; tag fallback = `OwnNhl27Tag` only (EPW stand-in removed). Rebuild #1 installed
  fine but KIT CHECK failed 40/46: 21× "back-hem tag missing" + 18× "hem stripes run past" (both intended) and 14× sock
  stripes (NOT intended — `SockFit.Apply` was gated on `HemFit.Enabled` in `KitDonor.Retarget`, so socks regressed).
- **Rebuild #2 (21:10) — 46 of 46 pass** (`Debugging\altport\apply6.log`): `SockFit` decoupled from `HemFit`
  (always runs now); `FanaticsKitCheck` hem-stripe and back-hem-tag rules removed (sock check kept). Command:
  `AltPort.exe --target "D:\CIMS-test\alts-copy\NHL24PC" --restore-first "D:\CIMS-test\fullkit-backups\20261124-031541"
  --apply` (~31 min; restore-first is required every run — AltPort never overwrites existing variants). Install read-back
  ok, backup `20261124-205008`. After it, AltNames must be re-run (`--names Debugging\altport\names27.tsv --apply`)
  because the restore resets the names (done 21:21, backup `20261124-211128`). Note: this rebuild also cleared the
  earlier known FAILs (Carolina 11 sock false positive, LA/Pittsburgh pants-logo) — recheck §6.1/§6.4 against apply6.log
  before re-raising them.
- **Detroit alt-twelve wheel-over-hexagon (21:31 in-game report) — FIXED 21:46**: the rebuild placed NHL 27's centennial
  "100" (which NHL 27 ships as a branding LAYER in its `right_shoulder_sponsor` spot, not painted on the sheet) into the
  donor's sponsor slot 6. NHL 24's UV layout renders that stamp spot on the shoulder OPPOSITE to NHL 27's, so the layer
  drew the wheel over the painted Priority Health hexagon on the owner's left shoulder (log line: "detroit alt-twelve:
  NHL 27's jersey sponsor is a separate layer… placed in the donor's sponsor spot"). Fix: layer 6 blanked in the color
  and normal branding arrays via TextureSlot `apply --layer 6 --apply` (backups `20261124-214538` / `20261124-214559`,
  read-back identical, re-export confirms all 7 layers empty; coeff layer was already empty). Durable side: per-kit drop
  list `KitDonor.DroppedLayers` reading `data\nhl27_drop_layers.json` = `{"detroit alt-twelve": [6]}` — future rebuilds
  blank the slot. NOTE: TextureSlot refused the write at first ("REFUSED: the original game") because the suite's ACTIVE
  build profile (nhl24-v092b) points Nhl24GameDir at the alts-copy and `EnsureGameBuilds()` re-syncs it on every load —
  the workaround was switching ActiveGameBuildId to nhl24-v32 for the two writes, then restoring settings.json
  byte-for-byte (backup in `work\verify-final\settings-backup.json`).
