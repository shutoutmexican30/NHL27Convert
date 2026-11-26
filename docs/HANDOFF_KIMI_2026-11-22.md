# Handoff: NHL 27 → NHL 24 conversion work (for Kimi K3), 2026-11-22

Owner: Devin Sutton. This document is self-contained. Read `docs/HANDOFF.md` for the long history of the suite; this file
covers what is in flight right now, the exact state of the machine, and what to do next, in order.

> **Correction 2026-11-24:** `D:\NHL24PC_V3.2` does not exist. The current main game is
> `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC` (1.60 eboot 188,338,041 bytes, cas_05 3,690,459,199 bytes, globals.toc
> written 2026-11-23 23:36). The Suite settings point at `D:\CIMS-test\prospects-copy\NHL24PC` (the DB/mesh thread's copy).
> The NHL 27 alternates port uses its own copy, `D:\CIMS-test\alts-copy\NHL24PC` (tool `tools\AltPort`). New kit data
> goes to a new kit CAS file (`FullKitInstall.KitCas`), never into cas_05.
>
> **NHL 27 alternates, state 2026-11-24 (test copy only, not on the main game):** all 46 NHL alternates installed on
> `D:\CIMS-test\alts-copy\NHL24PC` (backup before kits: `D:\CIMS-test\fullkit-backups\20261124-031541`), KIT CHECK 46/46,
> NHL 27 names (read from NHL 27's PS5 `loc/eng_us.db` with `tools\LocDump27\extract_loc27.py` + `loc27_lookup.py`;
> applied by `tools\AltNames --names Debugging\altport\names27.tsv`). Kit data in new files
> `nhl_installpackage_05\cas_17.cas`, `nhl_installpackage_02\cas_08.cas`, Patch `nhl_installpackage_05\cas_13.cas`.
> Rerun: `AltPort --target <copy> --restore-first <pre-kit backup> --restore-only --apply`, then `AltPort --apply`, then
> `AltNames --apply`, then `AltNames --check`. Fixes in this pass: per-kit `KitLabel` (no Fanatics settings leak),
> NHL 27 numbers per kit (`Nhl27Numbers`, FontRemap), NHL 27 sponsor spot names (`right_shoulder_sponsor`) and no sponsor
> where NHL 27 has none and the C/A would sit on it, every donor logo array cleared (19 kits had Anaheim pants logos) plus
> a KIT CHECK rule for donor art, no Adidas yoke zone in ZoneMap, equipment for any new variant 2-25.
> Left: 4 kits keep NHL 24 numbers (Columbus/Dallas/Edmonton: FontRemap.Verify flags a glyph box a few px wide because
> the band's glyph detection merges with the next row; Detroit 2024-25 Stadium: NHL 27 sheet has no letters). AHL/CHL/
> ECHL/Europe and the 7 new teams not started (owner: NHL only for now). Code is not committed (shared dirty tree).

## 0. Ground rules from the owner (do not break these)

1. **Never write to the main game** `D:\NHL24PC_V3.2\NHL24PC` while testing. Work on a full copy under `D:\CIMS-test\`
   (the suite's tools refuse other paths for checkpoints). Only install to the main game when the owner says so.
2. **Salary cap patch: do not use it.** Owner: "im not even sure it even works so i would prefer to not use it". Don't apply,
   re-apply or copy the `Salary cap 2026-27` entries of `user\patches\nhl24.xml` to any folder, test copies included. Removing
   it from the main game is a separate (contracts) task: report only.
3. Every game write goes through the suite's safe path: backup first (`ApplyTarget.Backup`), read back, restore on failure,
   game must be closed. Never FMT and never the NHL24Kit Worker for these jobs.
4. Stay inside NHL 24's structure: no new leagues (stockleagues is full, 33/33). Owner decision 2026-11-22: **do not replace
   any existing NHL 24 team**.
5. Report plainly: what was verified, what was not.

## 1. Machine state (verified read-only 2026-11-22, after the owner deleted and reinstalled the game on 2026-11-21)

| Item | State |
|---|---|
| Main game | `D:\NHL24PC_V3.2\NHL24PC`. `game\` and `game-UPDATE\` are now **real folders** (no junctions). 53.4 GB. Exe is `nhl24_new.exe` + `Play NHL 24.bat` (the stock V3.2 launcher; the old IceHouse/launcher.exe files are gone). |
| eboot | `game-UPDATE\eboot.bin` 188,338,041 bytes (1.60); `game\eboot.bin` 187,071,493 (1.00). `user\eboot_offsets.json` matches 188,338,041. |
| Mod state | **Modded, same as before the delete.** nhl_game `stockteamjerseys` and `exhibitionteams` are byte-for-byte the same rows as the pre-delete build: 64 Fanatics 26-27 kits (style 2, variants 24 away / 25 home, 32 NHL teams), Utah Mammoth in slot 22, the same 17 teams' goalie-mask fields. Number-fix kits: not verified at texture level (needs `FanaticsKitCheck`). |
| Fanatics as default | **Not applied** (0 of 64 Fanatics rows have isdefault=1). |
| Contracts | Present but the player table changed: `exhibitionplayerbiotable` now has 13,300/13,300 rows in use (was 9,114) with 4,178 blank-name rows. 6,639 players still match the old contract dollars. Check this with the contracts owner before touching players. |
| Cap patch | **Active again** in `user\patches\nhl24.xml` (3 `Salary cap 2026-27` Metadata blocks, isEnabled=true). `nhl24.xml.off` is the same file without the cap patch. Not changed by me. |
| CAS | `game\Data\Gen4b\superbundlelayout\nhl_installpackage_05\cas_05.cas` = 3,690,459,199 bytes. Hard cap 4 GiB (4,294,967,296) → ~604 MB headroom ≈ 45 kits at ~13.3 MB/kit. |
| Free space | D: 334 GB free. |
| Old data | `D:\stock-restore\{base,update,update-UPDATE}` still holds the **pre-delete modded build's data** (it was what the old junctions pointed at). Despite the name it is not stock. Don't use it as stock. |
| Test copies | `D:\CIMS-test\v32-copy` (first copy) was deleted with the reinstall. `D:\CIMS-test\v32-copy2\NHL24PC` is **incomplete** (46.4 of 53.4 GB, robocopy was stopped). Redo it (command in §5). `D:\CIMS-test\dbmesh-copy` belongs to another task, so leave it. |
| Repo | `D:\dev\NHL27Convert`, branch `convert27-slice`, HEAD `828577b`. Many uncommitted changes from other work streams (see `git status`); this task's changes are listed in §3. `C:\dev\NHL27Convert` holds only a stray 92 MB file named `C:devNHL27Convert ` (left alone). Worktree `C:\dev\NHL27Convert-kits` is gone (git lists it prunable). |
| Suite app | `src\Suite.App\bin\Release\net8.0-windows\CenterIceModdingSuite.exe` (built 2026-11-22 00:52). |
| Settings | `%APPDATA%\CenterIceModdingSuite\settings.json`: active build id `nhl24-v092b`, whose GameDir/ApplyTargetDir now = `D:\NHL24PC_V3.2\NHL24PC` (the profile id/name is the old v0.9.2b one; the `nhl24-v32` profile still points at the deleted `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC`). Top-level `ApplyTargetDir`/`Nhl24GameDir` = D: path. Tidy-up item: rename the profile or point `nhl24-v32` at D: and make it active. |
| Backups | `%LOCALAPPDATA%\CenterIceModdingSuite\backups\` has suite backups from 2026-11-22 01:03–03:06 (TOC + CAS-length backups, not full copies). `D:\CIMS-test\release\Center Ice Mod 2026-11-15` (installer release folder). The stock baseline `D:\CIMS-test\stock-v32` mentioned in HANDOFF.md no longer exists. |
| Oodle | Tools need `oo2core_9_win64.dll`; FMT is not on this machine. A copy exists at `D:\EA SPORTS College Football 27\oo2core_9_win64.dll`; put it beside a tool's exe. |

## 2. Work queue (owner's priorities, in order)

1. **Defaults swap fix**: code done and unit-tested, copy test half done (§3).
2. **CAS spill/reclaim fix**: designed, not built (§4). Blocks all porting.
3. **Locstring code 7 test** (§3.3).
4. **Port NHL 27 uniforms** (§6): the 84 kits missing on teams both games share, plus the 7 NHL 27 teams that fit unused NHL 24 slots. No team replacements. Proposed order: NHL kits, then AHL and CHL, then the rest.
5. **Uniform Editor page** in the texture suite, scope/report only (§7), behind 1-2.
6. Later (memory): manual uniform/equipment DB editor; portable suite (no machine-specific paths).

## 3. Defaults swap (Fanatics kits become each NHL team's default home/away)

### 3.1 What changed (uncommitted, in `D:\dev\NHL27Convert`)
- `src/Suite.Convert27/KitDefaults.cs`:
  - `Teams()` now covers NHL clubs only (team ids 0-31: 32-35 are the All-Star teams, and "World of CHEL Tier 1" was wrongly included before) that have both Fanatics rows (2/25, 2/24) and a current default.
  - New `OldDefaults(db,t,team,light)`: the team's **actual** current default rows (style 2, isdefault=1, active, by islightcolor, not Fanatics), primary = lowest orderid. The old code assumed variants 0/1. On V3.2 nhl_game: Boston 6/7, Buffalo 4/5, Carolina 2/3, Edmonton 4/5, Ottawa 3/4, San Jose 4/5, Vancouver 4/5, Vegas 2(dark)/1(light), Philadelphia two default pairs (0/1 with orderid 5/6 and 6/7 with 0/1). initial_experience differs (Boston and Philadelphia are 0/1 there), so each database is handled from its own rows.
  - `MakeFanaticsDefault`: clears **every** current default of the team and sets Fanatics home/away to default. Fanatics take the primary pair's orderid and the primary old pair takes Fanatics' orderid. It saves every touched row (team, variant, isdefault, orderid, locstring) for an exact undo. It is idempotent via `IsFanaticsDefault`.
  - `Run()`: read-back now checks the saved teams are Fanatics-default (apply) or that every saved row is restored exactly (undo). It also keeps the earlier 2-line fix: `T3Db.Load((byte[])bytes.Clone(), …)` (Load edits in place).
- `tests/Suite.Tests/KitDefaultsTests.cs`: new test `Real_current_defaults_are_swapped_not_variants_0_and_1` (Philadelphia-style two pairs, orderid hand-off, team ids ≤31, idempotent, exact undo). Both KitDefaults tests pass.
- Known cosmetic bug left: `Run()`'s success message always says "the old kits are named '23-24 Adidas'" even though `NameOldKits` is false. Fix the text.

### 3.2 Copy test so far
- On `D:\CIMS-test\v32-copy` (since deleted) apply worked: 32 teams Fanatics-default in nhl_game and initial_experience, every copy read back. The DB writer appended re-encoded chunks to `nhl_installpackage_00` cas_01/cas_02, **not** cas_05. **Undo was not tested** (the copy was deleted mid-test). The undo file the test created was moved off the live suite folder to the scratch dir, so the suite does not think there is an undo pending.
- To finish: make a fresh copy (§5), then `apply`, check, `undo`, and check that every value is back. A harness exists at `C:\Users\devin\AppData\Local\Temp\claude\…\scratchpad\DefaultsTest` (it may be gone; it is ~40 lines: load `SuiteSettings`, set `Nhl24GameDir`/`ApplyTargetDir` to the copy, call `KitDefaults.Run(s, undo)`, print per-team defaults from both DBs via `LegacyFiles.ReadAll(...everyToc:true)`). **Warning:** `KitDefaults.UndoPath` is the real `%APPDATA%\CenterIceModdingSuite\data\kit_defaults_undo.json`. Move it away after a copy test, or the app will offer an undo meant for the copy.
- Then boot the copy to the menu and check a team's default kit in Play Now.

### 3.3 "23-24 Adidas" label / locstring code 7
- From the eboot (`reference/eboot-v32.bin`, string table near `VINTAGE_%d_%d_%d`) plus `loc/eng_us`: locstring 0 Home, 1 Away, 2 `ALTERNATEJERSEY` "Alternate", 3 `OLD_HOME` "2006-07 Home", 4 `OLD_AWAY` "2006-07 Away", 5 `OLD_2NDHOME` "2006-07 2nd Home", 6 `VINTAGE_<style>_<teamartid>_<variant>` (per kit), 8 `TXT_CHL_HOME` "CHL Home", 9 `TXT_CHL_AWAY` "CHL Away". **Code 7 has no string and no jersey row in either game uses it.** It is either a blank gap or handled in code. NHL 27 adds 10/11/12 (unknown to NHL 24).
- English table: 65,498/65,535 used, **37 free** (the count is a u16 in the header). Per-kit names for 64 old kits don't fit. 3/4 and 8/9 are shared but already used by the 2006-07 kits and the European CHL kits, so renaming them would mislabel those.
- Test still to do (on a copy): set one old kit's locstring to 7 and look at the jersey picker (blank? crash? some shared text?). If 7 resolves to one key, adding that single key gives every old kit "23-24 Adidas" for 1 entry. Otherwise keep "Home"/"Away" (current default, `NameOldKits=false`).

## 4. CAS spill/reclaim (must come before any porting)

**Problem.** `FullKitInstall.CloneKit` appends every cloned kit's payloads and manifests to the CAS the **source entry** lives in. With the Anaheim donor that is `nhl_installpackage_05/cas_05.cas`. A CAS offset/size is a u32, so a file can't pass 4 GiB. Failed applies restore TOCs but leave the appended bytes. A guard already exists: the space check before backup in `FullKitInstall.Run` uses `PerKitBytes` and refuses up front.

**Format facts (from `BundleIndex.Records`).** A bundle record is: header 32 bytes (u32, u32, u32 flagsOffset, u32 entryCount, u32 0x20 ×3, u32 0), then per entry: if that entry's flag byte = 1, a BE location word `[0][patch][catalog][cas]` first, then BE u32 offset and size. Flag 0 inherits the previous entry's CAS. The CAS path is just `<Data|Patch>/Gen4b/superbundlelayout/nhl_installpackage_<catalog:D2>/cas_<cas:D2>.cas`. TOC chunk entries carry their own location word (`TocArchive.Chunk(Id, LocationWord, Offset, Size)`). `BrandingClonePlan.BuildMergedRecord` already writes records with explicit per-entry location words (flag 1), so that pattern is proven.

**Design (recommended, lowest risk): spill into an existing CAS of the same package that has room, not a new file.** The game certainly opens cas_01..cas_16 of package 05, and most are ~1.07 GB, leaving ~3 GB each. A brand-new `cas_17.cas` might need layout registration and is unproven.
1. In `CloneKit` (writes and plans are already collected before anything is written), pick a target CAS per write: keep the source CAS if `length + appended + bytes ≤ limit` (use 4,294,967,296 − 64 MB margin), else the same-package, same-layer CAS with the most room. Apply the same for manifests (currently appended to `p.Source.Manifest.Cas`).
2. When any entry of a record moves CAS, rebuild that record with **flag 1 + explicit location word for every entry** (manifest first), like `BuildMergedRecord`: copy the 32-byte header, write `flagsOffset`, keep the count, then the entries, then the flag bytes. Non-cloned entries keep their original CAS/offset/size, given explicitly. `TocArchive.NewBundle` takes the new record bytes.
3. Chunks: `newChunks` get `LocationWord(spillCas)`. Check that `LocationWord()` in FullKitInstall encodes the patch bit and catalog correctly for package 05.
4. Change the guard to sum planned bytes per target CAS after spill choice (dry run prints projections per CAS already).
5. **Reclaim (safe subset):** a tool `CasReclaim --target <copy> [--apply]` that collects every location in every TOC of both layers (all bundle entries + chunk tables), finds each CAS's highest referenced end, and truncates only unreferenced **tail** bytes (left by failed applies). Read-only report first; never compact the middle.
6. Test on a full copy: force a tiny limit so a **re-install of one existing Fanatics kit** (not a new port) spills, then run `FanaticsKitCheck`, re-index (`BundleIndex.Build`) and check the bundle entries resolve to the spill CAS and decode, then **boot to menu** and view that kit in game. Also run `StateCheckpoint` before/after.

## 5. Making a full test copy

```powershell
$dst='D:\CIMS-test\v32-copy2\NHL24PC'
robocopy D:\NHL24PC_V3.2\NHL24PC $dst /E /XJ /R:1 /W:1 /NFL /NDL /NP /MT:8 /LOG:D:\CIMS-test\v32-copy2\copy.log
```
(Rerunning resumes the stopped copy.) Then, **on the copy only**, replace `user\patches\nhl24.xml` with the content of `nhl24.xml.off` and delete any `.bak` holding the cap patch, so the cap patch is not present on the copy. If the main game ever has `game`/`game-UPDATE` junctions again, copy the junction targets, not the links. `ApplyTarget.Problems` refuses links.

## 6. Uniform porting inventory (read-only, done 2026-11-21, still valid: the team and jersey tables are unchanged after the reinstall)

Method: live nhl_game `exhibitionteams` + `stockteamjerseys` vs `reference/nhl27-full-csv` (`exhibitionteams.csv`, `stockteamjerseys.csv`, `stockleagues.csv`). Teams matched by normalized name across leagues (NHL 27 reuses NHL 24's team index slots; ~50 blank rows in the 27 CSV are dump padding). "Missing" = NHL 27 active (style, variant) with no NHL 24 row for that team. NHL Fanatics home/away (`data/variant_map.json` nhl27 homeVariant/awayVariant → installed as 25/24) are excluded. The full per-team tables are in the attached `NHL27_full_inventory.md`; keep that file next to this one.

- **Matched teams' missing kits: 84** (EASHL/HUT/Pro-Am excluded):
  - NHL 46 (mostly 2/12 "Alternate" and 2/13 per-kit names; Boston 2/10, 2/11, 2/15; Tampa 2/4 uses NHL 27-only locstring 12).
  - AHL 3: Grand Rapids, Texas, Coachella Valley (1/2 alternates).
  - ECHL 1: Trois-Rivières 1/2.
  - OHL 2: Flint, Guelph. QMJHL 1: Chicoutimi 1/3. WHL 4: Everett, Regina 1/3, Seattle, Wenatchee.
  - Europe 27: SHL Frölunda, Rögle; Liiga KooKoo, SaiPa; DEL Eisbären, Kölner Haie; Extraliga Liberec, Olomouc, Pardubice, Plzeň, Sparta; Swiss Davos, Fribourg; ICEHL Graz, KAC. Mostly style 0 variants 10/11 with CHL Home/Away names 8/9, or 0/2.
  - 23 of the 84 use locstring 6 and need one English entry each (37 free in total; budget this).
- **New teams going into unused NHL 24 slots (owner approved): 7**: 96 Tahoe Knight Monsters (ECHL, 3 kits), 97 Greensboro Gargoyles (ECHL, 3), 205 Kiekko-Espoo (Liiga, 2), 248 Hockey Club Milano (ICEHL, 2), 262 HK Nitra (Champions League, 2, locs 8/9), 269 Adler Mannheim – Spengler (2), 379 Norway – Womens Worlds (2). Each needs a new `exhibitionteams` row at that index (46 spare rows: 531/577), and also: team name/abbr strings, colours, arena/logo art references, roster rows, equipment, and checks of every per-team table the game indexes by team id. This is unproven. The 39 other NHL 27-only teams would replace an NHL 24 team, so they are **excluded** by the owner's decision.
- Capacity: stockteamjerseys 2342/2799 (457 spare; initial_experience stock 2262/2799, check live), stockteamequipmenttable 9336/12781, variant words stop at "thirtynine".
- **Space:** 84 + 16 ≈ 100 kits × ~13.3 MB ≈ 1.3 GB. This does not fit cas_05's ~604 MB, so §4 comes first.
- **Unproven:** the install pipeline has only cloned from the NHL adidas donor (style 2). AHL/CHL kits are style 1 and the European ones style 0, so the donor, garment templates and kit check are untested for those. Kits in a slot both games have, where NHL 27 redrew the art, are **not** counted. Do a texture-level compare per team before saying "nothing to port".
- **Proposed porting order:**
  1. NHL style-2 kits that use shared names (locstring 2): ~25 kits, no language entries, same donor pipeline as the 64.
  2. NHL locstring-6 kits (~20) within the 37-entry budget.
  3. AHL (3), then CHL (7), then ECHL (1), after a 1-kit style-1 donor proof on a copy.
  4. Europe (27) after a style-0 donor proof.
  5. The 7 empty-slot teams last (new team rows).
  - Every batch: copy first, dry run, apply, `FanaticsKitCheck` OK, read back, boot, owner looks, then main game.

## 7. Uniform Editor page (scope only, owner wants it; build after §3–4)

Owner: "people need to be able to make their own uniforms in the texture suite and move things around to their liking."
The planned scope (not yet checked against the code in detail):
- Reuse: `FullKitInstall` / `NewUniformAddition` (new slot), `JerseyRowPlanner`, `PlayerTeamRegistry.Register`, `UniformHotspots` (Capture/Replace/CopyFontPlacement/CopyBrandSpotInPlace: logo, number, name, captain and sponsor spots), `UniformPhysicalLayouts`, `FontRemap`, `HemFit`/`SockFit`, `UnpaintedLayers`/`SponsorSide`, `KitDonor`, `FanaticsKitCheck`, `LanguageStrings` (jersey name), texture suite page (`TexturesPage`), and the mannequin/mesh viewer (`tools/MeshExport`, `D:\mesh-view`).
- Minimal v1:
  - Pick team and base kit.
  - Layers list (crest, shoulder logos, numbers, name, captain patch, sponsor, stripes/hem).
  - Drag/resize spots on a flat 2D sheet with live recolour (zone colours).
  - Preview on the mannequin.
  - "Save as new kit" with a name, using the same safe installer (dry run → backup → install → KIT CHECK → read-back).
- Build order:
  1. Read-only editor over an installed kit's hotspots + layers (flat preview).
  2. Edit spots/colours and export a mod JSON.
  3. Mannequin preview.
  4. Install as a new slot through `FullKitInstall`.
  5. Edit an existing slot in place (repair path).

## 8. Useful tools and files

- `tools/JerseyDump <game> [db] [--team N]`: stockteamjerseys per team.
- `tools/FullKitAudit`, `tools/KitInstall`, `tools/Fanatics27Add`: kit install/audit.
- `tools/StateCheckpoint --target <copy under D:\CIMS-test>`: exact TOC/CAS checkpoint + restore.
- `tools/LegacyStrings dump|verify|replace`: legacy files (loc, gamemodes).
- `data/variant_map.json`: NHL 24 ↔ NHL 27 default variant per team + extraKits.
- `reference/nhl24-full-csv/...nhl_game.db` (a stock-ish NHL 24 DB), `nhl_game-meta.xml` (T3Db schema); `reference/nhl27-full-csv/*.csv`.
- Read-only dumper used for the inventory: `T3Db.Load(bytes, meta)` + `LegacyFiles(game, settings).Read("gamemodes/database/t3db/nhl_game.db")`, then write each field with `GetInt`/`GetString`. Tables: `exhibitionteams`, `stockteamjerseys`, `stockleagues`, `exhibitionplayerbiotable`.
- `docs/HANDOFF.md`: the full kit rules (donor, fabric zones, hems, socks, EPW tag, numbers/letters, kit check) and the earlier CAS growth note around line 1209.
