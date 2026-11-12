# Center Ice Modding Suite (CIMS) — HANDOFF

Last updated: 2026-11-06. Repo: `C:\dev\NHL27Convert` (git, branch `convert27-slice`, not pushed anywhere).
Earlier survey notes (pre-decision): `C:\Users\devin\Documents\Codex\2026-10-20\where-are-the-imported-rosters-located\docs\HANDOFF.md`.

## New uniforms 2026-11-09

Detroit 24/25 are confirmed working in game on the test copy. Full write-up: `docs\NEW_UNIFORM_HANDOFF_2026-11-09.md`. New kits are installed by `FullKitInstall` (`tools\KitInstall`, and the app's uniform buttons through `NewUniformApply`). The older donor path is retired from installs.

**The six causes and fixes**

| # | Problem | Fix |
|---|---|---|
| 1 | `BundleIndex` skipped `levels/game/game.toc`, so the `player_team_brt` copy the game loads stayed stock | Walk up to `Gen4b` to find the game folder; all 911 copies are registered |
| 2 | Bare-name bundle rows kept the source kit's folder in their second column | `PlayerTeamRegistry` rewrites both row forms to the kit's own folder |
| 3 | Lengthening an EBX name moved the text after it without fixing pointers, crashing the `_hq_asp` presets | `EbxRiff.Rewrite` corrects and checks those pointers |
| 4 | Generated resource IDs were sometimes even; the loader reads even values as pointers | IDs are always odd, like every stock ID |
| 5 | New variants had no name, because `locstring` 6 reads the key `VINTAGE_<style>_<art>_<variant>` | `LanguageStrings` adds those keys; "Jersey name" box on the uniform page |
| 6 | Logos filled array slots in order, so Detroit's sponsor landed in the back-hem tag's slot | A logo matching a different stock slot goes there; the slot it left keeps its stock logo |

**Full-kit checklist (one ID everywhere)**

1. Clone every bundle of the same team's source kit, in the same Patch/Base layers, with new GUIDs, odd resource IDs, name hashes and per-copy chunk IDs.
2. Fix EBX pointers when a name changes length.
3. Overlay the converted art on each copy of each texture, in the slot that shows it.
4. Register every kit name in both versions of `player_team_brt`, in every copy, including `levels/game/game.toc`.
5. Add the `stockteamjerseys` rows in `nhl_game` and `initial_experience`, with variant = fevariant and `locstring` 6.
6. Add the `VINTAGE_<style>_<art>_<variant>` name to the language table.
7. Read back everything from a fresh index, under one backup that rolls back on any failure.

**Limits**

- Arizona/Utah pants drop the second logo layer, and Nashville's jersey drops one layer in its 5-layer copy (owner approved; needs `--cut-extra-layers`).
- The English language table has 37 free rows after the 64 kits.
- Variant words stop at `thirtynine`, and each CAS file must stay under 4 GB.
- The jersey name is English only.

**Next steps**

1. Boot-check the 64-kit batch on the test copy, then apply to live from the clean live restore point, with a fresh backup.
2. Build the "Sponsor patch" tool on the uniform page: keep, stock, replace or clear, for any uniform.
3. Add NHL 27 kits that NHL 24 doesn't have.

## Session 2026-11-06 (later): guided, preflight-checked uniform conversion

Convert 27 now opens in **Standard** mode: team/uniform selectors with friendly names, automatic NHL 27 source selection,
**Check & convert**, a live preview, and a plain-English conversion report. **Advanced texture controls** restores the
full matcher, per-layer editor, file drops, replacement folders, slot map, variant map, and stamping workflow.

`ConversionPreflight` is shared by Check & convert and Add to mod, so export cannot bypass validation. It repairs automatic
base/logo/normal choices, preserves manual overrides, reports unplaced sponsors as warnings, and blocks export for missing
artwork, baked automatic base previews, mismatched automatic colour/normal pairs, or unplaced crests/shoulders. Base slots
with no exact unbaked NHL 27 base now return to stock rather than silently using baked preview art. `PreflightReport` and
its checks are UI-independent and covered by `ConversionPreflightTests`; the existing logo regression tests also cover the
plain-preview fallback. The WPF application builds, but the redesigned page still needs an interactive visual pass.

## Session 2026-11-06: Teams & lines editor (built and tested; not yet seen in game)

The Puck Peak-style manual editor on top of `Suite.Rosters`, plus launcher integration. Committed with this handoff.

**New — `src\Suite.Rosters\TeamEditor.cs`:** the teams/lines/strategies editing model over one T3DB roster database.
- Reads teams (`exhibitionteams` city+team via the new `T3Db.GetString`), per-team roster rows with player names
  (roster `playerindex` → `exhibitionplayers.playerid` → bio `game_id`; **bio `game_id` is an integer field**, only
  names are strings), the 93 one-bit line-slot columns, and `exhibitionteamstrategies` (182 fields, grouped for the UI:
  forecheck/pressure, lines, biases, PP, PK, 3-on-3, breakouts, EV units, offense/defense — first match wins).
- All edits are `T3Db.SetInt` in place: jersey, captain (0/1/2), playerstyle, rosterstatus, any line slot
  (`SetSlot` moves the bit off the previous holder and refuses a position mismatch: G/D/F vs the slot's role),
  strategy fields. Layout rules hold by construction (nothing grows or moves).
- `Verify()` checks what the game enforces: each slot held at most once, no two dressed core slots per player,
  no unit doubled, dress shape changes vs load flagged (a team incomplete at load is not blamed), one captain /
  ≤3 letters / letters on dressed players only, jersey numbers unique per team.
- Tested: 9 new xUnit tests in `tests\Suite.Rosters.Tests\TeamEditorTests.cs` against the real stock `nhl_game.db`
  and the live `RosterCustom0` (read-only) — all pass, including byte-identical no-edit round trips. Solution builds
  0 warnings. (Pre-existing failure unrelated: `T3DbTests.Integers_match_the_dump…` — the `dbwork\nhl_game\before`
  dump (531 teams) predates the `dbwork\stock` re-export (527); refresh the dump fixtures.)

**New — app page `Pages\EditorPage.cs` ("Teams & lines"):** open a roster source → team list → Roster / Lines /
Strategy tabs → Verify / Save. Sources: saved rosters found in the CIMS copy (read-only today, see below) and any
`.db` file via "Open .db file…" (the stock export from the work folder is prelisted; its write-back creates
`<file>.before-edit`). Save runs `Verify()`, refuses while the game runs, writes through a temp file, and keeps a
backup (`save-backups\` for saves, `.before-edit` for files). Strings stay read-only (a length change would move
the layout). Strategy values are shown as the game's numeric codes — the meta XML names fields but not menu words.

**Launcher:** `NHL24PC-CIMS\tools\launcher.ps1` gains a third button under PLAY, "Rosters"/"Составы" (`btnStudio`),
which starts `CenterIceModdingSuite.exe` from `<root>`, `<root>\tools\CenterIceModdingSuite\`, or the dev build.

**FOUND, BLOCKS THE SAVE SOURCE — current savedata INDEX layout changed.** Every save written by the current
emulator build (Profile0 320 B, Season0 320 B, BeAGM0 320 B, RosterCustom0 **384 B**, all Nov 2-6) uses a new INDEX:
three named blocks `__SaveLoadFileInfo` (192 B), `__SaveLoadMetaData` (112 B), `__SaveloadVer` (80 B), each
`name[32] + u64 count + …` — **not** the 80-byte `[name, size, start, end]` record list `SavedRoster` was built
against (tested on the old `RosterConversion\backup\before-restore\RosterCustom2`). `SavedRoster.Find` throws on
`RosterCustom0` (384 % 80 ≠ 0) and silently returns no saves, so both the sync page's "Write saved roster" and the
new editor see no saves from the current game. The FileInfo block holds the real file name at +0x50 and u64
offset-ish pairs near the end of DATA (0x1b1c75/0x1b1cd5/0x1b2727/0x1b289f in RosterCustom0) plus 0x10…-flagged
values that look like block hashes; the DATA container itself (len+tag+payload blocks, LZ4 roster record) is
unchanged. **Next step:** decode the new INDEX against old+new samples and update `SavedRoster` (keep read/write of
the old layout for backwards compatibility; write must produce the layout the current emulator reads). Until then
the editor works through "Open .db file…" and the sync path works on the five stock DBs as before.

## Session 2026-11-06 (later): the Patch-merge — game tree restructure in progress

User direction: fold the 1.60 update into the main data tree (copy `game\Patch` into `game\Data`, delete Patch)
and run the game merged. **The restructure was being executed live on the machine while this was worked on**
(by the user / another session): CAS files left Patch first, then the rest, then the whole `game` folder was
replaced by `NHL24-merged\` at the project root (15 GB, Data carries the merged TOCs, a Patch folder still present
at the time of writing). **Do not assume the old `game\Patch` layout in any code.**

Suite changes for the merged layout (committed with this handoff):
- `BundleIndex.CasPath` (and every private copy — `BundleChunks`, `TocChunkWriter`, `LegacyFiles`, `TextureResource`)
  now resolves a patch-flagged chunk location to the Patch CAS when it exists and to the Data CAS otherwise.
  Without this, all patch-flagged TOC entries dangle after the merge and nothing reads (proven: current
  `LegacyFiles` failed with "No legacy file index lists…" on the merged tree; the scoreclock read passed with the
  fallback). Layouts with a real Patch folder behave exactly as before.
- New integration tests `tests\Suite.Rosters.Tests\MergedGameReadTests.cs`: read the meta XML, nhl_game.db and
  scoreclock.gfx out of `Desktop\NHL24PC-CIMS\game` through the suite reader — they run only when that folder is
  the merged tree (no Patch); green means the merged game parses end to end.
- `tools\mods.ps1` (NHL24PC-CIMS): `$PatchDir` resolves `game-UPDATE\Patch\Gen4b` → `game\Patch\Gen4b` →
  `game\Data\Gen4b` (was hardcoded to game-UPDATE and already broken on this install).

Verification done: merged `Data\pkg_00\cas_01.cas` carries the 1.60 chunks the rus_blr mod hashes to (verified again
against the final tree the folder was rebuilt as — the folder now comes from the D:\CIMS-test merged copy layout,
where the update content sits in Data at different total CAS sizes); every Patch file matched its Data counterpart
in size before the concurrent deletion; the meta, nhl_game.db and both scoreclock movies read clean through the
suite reader on the final tree (`MergedGameReadTests`, green). The empty `Patch` folder is gone; the game folder is
back at `Desktop\NHL24PC-CIMS\game`. **Only a game boot is still owed.** Notes: `snapshot.json` at the game root is
the D:\CIMS-test copy's manifest, inert here (the suite keeps its own backups under LocalAppData); the suite's
"stock baseline" pointer for this target predates the rebuild, so the next apply's backup is the new baseline.
If the merged folder had kept another name, `common.ps1` `Update-GamePaths`, `ApplyTargetDir` and the launcher
button would have needed repointing — moot at `game`.

## Codex continuation 2026-11-02: PLAYERS INSTALLED (15:55 local)

- User explicitly requested completing the player additions, including ratings, faces/equipment, portraits and lines.
  **Installed to `C:\Users\devin\Desktop\NHL24PC-CIMS\game`**, with the suite's own writers; original Downloads game untouched.
- Recovered the exact 48-player list from `<Desktop>\CIMS_new_players_review.html`. **47 new records**, plus correctly
  matching the existing **Jonas Røndbjerg (15576)** to the API's "Rondbjerg" (ø does not decompose under FormD).
  Do not create a second Rondbjerg. All 48 review names are accounted for.
- Three players from that list are currently AHL: **Cooper Black (FLA), Rieger Lorenz (MIN), Alex Bump (PHI)**. They were
  created and placed with their clubs' AHL affiliates. Requests persist in `%APPDATA%\CenterIceModdingSuite\data\roster_additions.json`,
  which `RosterSyncRun` now loads when no explicit additional-prospect list is supplied.
- **39/47 new players use NHL 27 data/ratings; 8 use NHL API identity and donor ratings.** Compatible generic NHL 24
  donor heads/hair/equipment are used, not NHL 27 head meshes. Older mode databases which already contained a planned
  player's ID now receive the main database's authoritative bio/ratings/equipment (`NewPlayers.AlignPlannedWithMain`),
  retaining their own player-index links. This fixed 55 appearance/data audit differences.
- Recovered NHL 27 CSVs from the earlier `RosterConversion\nhl27-csv` folder into `%LOCALAPPDATA%\CenterIceModdingSuite\nhl27db`;
  converted its tables.json `name` keys to the current dump format's `table` keys. Five stock source DBs came from
  `RosterConversion\more-dumps\legacy`. Working dumps/builds are restored under `%LOCALAPPDATA%\CenterIceModdingSuite\dbwork`.
- **810 portraits prepared and applied** (798 current NHL players + 9 retained injured players + the 3 AHL additions).
  All 810 read back byte-identical; all five databases stayed identical after portrait application. A cache-free Worker
  independently read Yurov, Hellebuyck and Celebrini portraits (512x340, BC3 mean differences 2.5/2.4/2.4 versus PNG).
- Five database layouts/headers/sizes passed guards. Strict read-back verification: **798/798 current NHL players in
  each database, zero problems**. Cross-database placements agree. Separate audit: every new player's bio/birth date,
  compatible donor assets, ratings, equipment, portrait and club link passes; 32 NHL teams have 20 distinct dressed
  players/full even-strength lines and two goalies in each DB; main online mirror matches. Eight fallback ratings remain
  explicitly marked in the review CSV. Existing retired Paul Stastny/index-0 free-agency warnings in older DBs remain.
- Verification now refuses missing API players/indices; it uses main IDs to disambiguate the older Tomy/Thomas Novak
  duplicate when the API says "Tommy Novak". New regression tests cover this, ø matching, rejected bios, appearance
  alignment and missing links. **53 tests pass (16 roster + 37 suite); solution build clean.**
- Verified build: `%LOCALAPPDATA%\CenterIceModdingSuite\rostersync\2026-11-02-complete-players\`.
  Mod: `%APPDATA%\CenterIceModdingSuite\mods\Complete players 2026-11-02.json` (five database replacements).
  Portrait plan/images: `%LOCALAPPDATA%\CenterIceModdingSuite\portraits\`. CLI supports `SyncWrite --prepare-portraits`
  to prepare portrait assignments in the same run; `--include-prospects <ApiPlayer-json>` and `--verify-only` are also available.
- Test copy `D:\CIMS-test\NHL24-merged` was refreshed from live first. DB and portrait **apply → restore → reapply passed**;
  restore reported zero TOC/CAS-length differences, database test also checked original CAS tail hashes.
  Test backups: `20261102-154852` (DB), `20261102-155112` (portraits).
- **Live backups:** `%LOCALAPPDATA%\CenterIceModdingSuite\backups\20261102-155156` is before this entire installation;
  `20261102-155514` is before the new portrait batch, after the new databases. Live DBs and 810 portraits read back
  identical. `ApplyTargetDir` restored to the CIMS game; prior settings saved as `settings.json.before-complete-players`.
  The Worker Apply/Restore guard must remain: its older state still must not roll back our own writes.
- Receipts/scripts/logs: `C:\Users\devin\Documents\Codex\2026-11-02\x20-ar\work\`.
  User review CSV: same chat folder's `outputs\players-added.csv` (48 rows, new/existing action, NHL/AHL level and rating source).
- Game launched directly from the CIMS root at 15:55 (process running). **Visual Minnesota/Yurov gameplay check remains
  unverified**: Windows Computer Use denied control with "Computer Use was not approved to use nhl24_new". No attempt
  was made to bypass that restriction. Do not claim a gameplay test passed. Existing BeAGM/Profile saves were not edited.

## Codex continuation 2026-11-02: initial missing-player validation (superseded by installation above)

- Resumed from the supplied screenshot and this handoff. Existing uncommitted changes were preserved.
- Initial solution build passed with 0 warnings/errors; existing suites passed (37 Suite.Tests + 9 Suite.Rosters.Tests).
- Fixed `NewPlayers.AddTo`: a rejected bio/name previously still produced player-index, ratings and equipment records.
  Linked rows now use only the players whose bios were accepted; a completely rejected batch leaves files unchanged.
- Added two regression tests covering rejection, mixed valid/rejected batches, fixed slot counts, donor head preservation,
  and repeat-run idempotence. Suite.Rosters.Tests now passes 11/11 (48 tests across the two suites).
- Current on-disk state differs from the earlier apply notes: `%LOCALAPPDATA%\CenterIceModdingSuite` has no `dbwork`,
  `nhl27db`, or `rostersync` directories, and the roaming settings file has an empty `ApplyTargetDir`. Do not infer that
  the live game is stock from missing workspace files; its applied contents have not been independently audited yet.
- No game files, settings, saves, or mod plans were changed. The 48-player addition and Minnesota/Yurov in-game test
  remain pending. Next: recover/rebuild the database inputs and verify the target's current contents, then validate the
  missing-player build on a test copy before applying to the playable copy.

## Session 2026-11-02 (goal close-out): what was finished, what is walled

Twenty automatic rounds worked every open thread in `docs\WORKQUEUE.md`, which now opens with a state-at-a-glance summary
and carries the evidence for each claim. Short version:

- **Finished and verified**: BC1/BC5 codecs and the PS4 texture chunk pipeline (our decode matches FMT's own decode on
  100 % of pixels, max difference 1); `TocChunkWriter` written against a real game copy (read-back identical, restore 0
  differences, re-apply verified); the suite's own writer proven on a 13.7 MB legacy database (restore 0 differences, CAS
  tails identical); the matcher backlog (slot labels, Adidas->Fanatics always, phantom array layers, partial stock dump
  rejection, mod-list validation); the goalie gear census; the face section/UV/slot/texture analysis; the LT name census
  and its negative proof; the logo inventory with per-team crest detection.
- **Walled, each with a named cause**: the gear, face-texture and fabric-colour swaps need one elevated
  `MeshExport --texture-resource` run (FMT insists on Administrator although the read view is writable - proven by probe);
  the LT tunable values, the licensed head `id -> asset` map and an FMT-free texture `.res` lookup all need an **EBX
  reader** - the single missing capability that unblocks three threads at once; and the NHL 24 side of the logo inventory
  is stuck behind the Worker's `Texture.get_PixelFormat()` failure, which writes zero files for those folders.

Nothing was written to the playable game (`Desktop\NHL24PC-CIMS\game`) in this session. Every write went to
`D:\CIMS-test\NHL24-merged` first, with a backup, a read-back and a restore check.
## Session 2026-11-02 (latest): first real texture-chunk write, and the goalie-gear question

**The chunk writer now exists and has written a game copy.** `src\Suite.Core\Frostbite\TocChunkWriter.cs` appends the new
stored blocks to the CAS named by the entry's location record and repoints every TOC entry that pointed at the copy being
replaced; `tools\TextureChunk apply <game> <res> <chunk> [--expect <liveChunk>] [--restore-check]` drives it, refuses the
original game/read view and a running game, backs up through `ApplyTarget.Backup`, reads back and can do a full
apply -> restore -> apply check.

Blocks: the game stores large textures as **256 KB raw blocks** (Boston home normal = 21 x 262,144 + 1 x 92,160 = 22
blocks, 3,720,126 stored). `ChunkBlocks.EncodeBlocks` splits that way; a single block cannot exceed the 20-bit stored-size
field. The low nibble of the block flag byte is not a flag - it is bits 16-19 of the stored size.

Test copy `D:\CIMS-test\NHL24-merged`, Boston NHL 24 home normal -> NHL 27 thirteen art:
`appended 2,021,360 bytes to cas_05.cas @ 1,074,648,234; 1 TOC entry repointed`, read back identical (5,597,184 raw),
`changes vs backup: 2` (CAS length + contentsb.toc), restore gave **0 differences**, re-apply verified. The playable copy
(`Desktop\NHL24PC-CIMS\game`) has **not** been touched.

### Goalie equipment: what the data actually is

`stockgoalieequipment` / `exhibitiongoalieequipment` are **colour-zone tables** - `game_id` plus fifteen zone colours
(`padszone4color`, `blockerzone1color`, `trapperzone11color`, `stickzone4color`, ...), not item ids.
`stockteamequipmenttable` adds `team`, `brand`, `jerseyartid`, `equipment`, `era`, `variant`, `index`. So a goalie set is
**model (brand/equipment slot) + fifteen zone colours**, and the artwork comes from the model's texture folder
(`skater/goalie/pads|blocker|trapper|helmet/<brand>/<model>/...` with `_color`, `_normal`, `_coeff`, `_recolor1..3`,
`_idmap`, `_bentnormal`, `_ferender`).

Consequence, stated plainly: NHL 27 **artwork and colour schemes can be brought over** by replacing the texture chunks of
models NHL 24 already ships, and those are assignable through the existing DB rows (the roster writer is proven). A model
NHL 24 does not ship **cannot be added** - there is no mesh, no material EBX, and the `brand`/`equipment` id space is
fixed; copying its textures would create files nothing references. The same rule already governs jerseys ("NHL 24 cannot
get new texture folders").

Next step for this: a read-only census (like `tools\FacePlan`) that lists goalie models per brand from both asset indexes
plus the `brand`/`equipment` columns of both `stockteamequipmenttable` dumps, and marks each model as *has NHL 24
counterpart*, *NHL 27 only*, or *unused in both*. That is the map needed before any texture swap or DB write.
## Session 2026-11-02 (later): texture chunk writer + BC5 codec, built and verified

Continuing the Codex handoff. The blocking dependency it named - an own writer for top-level TOC texture chunks with a
PS4-tiled BC5 encoder - now exists and is proven offline. **Nothing has been written to either game copy.**

**`src\Suite.Core\Frostbite\Ps4Texture.cs`** (new): `TextureRes` parses the 132-byte resource header and
`TextureChunk` splits/rebuilds the payload. Facts confirmed against Boston NHL 24 home, correcting two assumptions in
the previous handoff: the **mip count is the byte at offset 30** (not 31), and small mips are **padded to whole 8x8-block
micro-tiles**, which is why every mip below 32x32 still occupies 1024 bytes (BC5) / 512 (BC1). Sizes therefore follow
`ceil(ceil(w/4)/8)*8 * ceil(ceil(h/4)/8)*8 * blockBytes`. `BlockBytes` is inferred from mip 0, which is what distinguishes
BC1 (8) from BC3/BC5 (16) - **the jersey colour map is BC1, the normal is BC5**.

**`src\Suite.Core\Frostbite\Bc5.cs`** (new): BC5 = two BC4 channels, 8-value ramp, PS4 tiling via the existing
`Bc3.TiledIndex` (a BC5 block is 16 bytes like BC3); blue reconstructed as sqrt(1 - x^2 - y^2).

**`tools\TextureChunk`** (new, not in the .sln): `verify <res> <chunk> [refPng]`, `png <res> <chunk> <out>`,
`build <res> <template> <png> <out>`. Reads and writes files only.

Verification against the live Boston home chunks (`work\fabric24-resources`):

| Check | Normal (BC5) | Colour (BC1) |
|---|---|---|
| Header parse: 2048x2048, 12 mips, GUID, 5,597,184 / 2,798,592 payload | ok | ok |
| Rebuild from parsed mips byte-identical to the live file | **true** | **true** |
| decode -> encode -> decode error (R,G, 255 scale) | mean 0.31, max 14 | n/a (codec not implemented) |
| New mip chain keeps the exact byte length | **true** | n/a |
| Decoded mip 0 vs FMT's own decode of the same chunk | mean 0.35, **max 1**, 100 % of pixels within 8 | - |

That last row is the important one: our tiling and channel order reproduce FMT's decoder, so the format reading is
right, not merely self-consistent.

**First real artifact**: `outputs\fanatics-fabric\boston-home-normal-from-thirteen.chunk` - Boston NHL 24 home's
*normal* slot rebuilt from the NHL 27 thirteen normal, 5,597,184 bytes (identical length), header untouched, full
12-mip chain regenerated, encode error mean 0.06. Measured high-frequency detail (mean |Laplacian| of red) drops from
**65.04 to 19.14 (3.4x smoother)**, and in the worst dimple cell from 99.07 to 27.48 - the Adidas dimples are genuinely
gone, not just blurred. Preview: `outputs\fanatics-fabric\shoulder-detail-live-vs-27.png`.

**Still not applied, and why**: writing it needs the CAS/TOC step - append the chunk to the CAS named by the entry's
*location record* (Boston home normal is `Patch\Gen4b\contentsb.toc` pointing at
`Data\Gen4b\superbundlelayout\nhl_installpackage_05\cas_05.cas`, offset 339,795,401), repoint the entry, and update
every duplicate TOC/RES copy. Do it on `D:\CIMS-test\NHL24-merged` first, with a byte read-back and a full restore
check, before the playable copy (`Desktop\NHL24PC-CIMS\game`). The Worker Apply path stays unused - its stale state can
roll back the installed players.
## Session 2026-11-02: emulator build, patches, logo inventory

- Our NHL 24 runs on **NHL24PC V3.2** (repack by LEVASTAK): `nhl24_new.exe` reports **FileVersion 0.8.3.0**, so it sits
  on HajDEO's shadPS4 fork at v0.8.3, with 39 modified files in `source_changes\` and a PowerShell launcher
  (`tools\launcher.ps1`, `tools\setup.ps1`) behind `Play NHL 24.bat` / `Install.bat`.
- Game data: **CUSA37933** (US), base `param.sfo` APP_VER **01.00**, update **01.60**. The update's `eboot.bin` is
  **patched on disk**: 21 bytes in 7 runs against the `eboot.bin.orig` it keeps beside it - four code patches
  (`je to jmp` at 0x065324F0, `movzx`/`mov` on the 0x16E field forced to zero at 0x05C30524 and 0x04DD0FC7, `jne` to nop
  at 0x05AC37E5), one flag byte (0x01E9819B) and two language/loc data edits at 0x09059F50. Markers:
  `user\eboot_offsets.json`, `user\loc_offset.json`, `user\freezefix_188338041.ok`.
- Upstream **v0.9.1** (published 2026-11-02) ships **no game data** - emulator plus `launcher.exe`, you install your own
  PKG (US or EU; 1.60 verified on US). It fixes cutscene black boxes/lines (#17) and the 8 GB out-of-VRAM crashes after
  several matches, and folds in the torpedatorpeda Be a Pro/NVIDIA fixes. It never touches `eboot.bin`: its fixes are
  runtime memory patches from `game_patches\nhl24.xml` in `user\patches\`.
- **Decision**: stay on V3.2 for modding. The suite's read view, index fingerprint and apply target all point at it, and
  the Russia/Belarus national-teams mod depends on its patched eboot. A clean v0.9.1 install in a separate folder would
  be the better *stock baseline* if one is ever needed - re-point `Nhl24GameDir` / `Nhl24UpdateDir` / `ApplyTargetDir`
  and expect one full index rebuild (fingerprint is package.mft + layout.toc). Never mix the two: read one, play another.
- **`data\nhl24-patches.xml`** carries the repack's code patches as runtime memory patches, so a clean 1.60 extract can
  be used without touching its eboot. Addresses follow the patch-repository convention `0x400000 + file offset`, one
  `<Line Type="byte">` per byte. The two loc data edits and the unknown flag byte are present but disabled. **Not yet
  verified on a clean install** - run one session per entry and confirm the symptom it targets actually goes away.
- **`tools\LogoInventory`** (`dotnet run --project tools\LogoInventory [out.tsv]`, not in the .sln) writes
  `%APPDATA%\CenterIceModdingSuite\data\logo_inventory.tsv`: every branding/helmet `_color` sheet in both games with
  game/league/team/brand/set/garment/kind/layer, a role guessed from the 32x32 alpha silhouette
  (blank / shield / brand:adidas / brand:fanatics / epw_tag / sponsor-other), the decoded PNG and the asset path.
  Last run: **19,116 sheets** (NHL 24 7,883 / NHL 27 11,233; layer 14,184, array 2,898, helmet 2,034). Roles are filled
  only where a decoded PNG exists (414 of them so far - the app decodes a folder when you open it), so open more teams
  in Convert 27 and re-run to fill the rest; the paths and names are complete either way.
## Session 2026-11-01 (latest)

- **Viewer numbers rebuilt** (`src\Suite.Convert27\viewer\index.html`). The digit cells were measured on the
  2048-square NHL 27 font atlas, but every NHL 24 atlas is 1024 square, so the result side cropped outside the
  image (silently drawing nothing when the font slot was still stock) and each glyph was stretched into a fixed
  430x560 box in a square canvas that the 0.38 x 0.46 m plane then stretched again. Cells are now scaled to the
  atlas that actually arrives, every glyph keeps its own width, the pair is butted together at ~28 cm, and the
  canvas is built at the panel's real aspect (one `PANEL` constant shared with `placeJerseyNumber`).
- **Adidas -> Fanatics, logo and normal, always.** When the paired NHL 27 set has no brand layer, the Fanatics
  colour and its normal map are taken from the nearest NHL 27 set that has one (`FanaticsFromAnyNhl27Set` +
  `SlotRoles.KnownLogo`): the mark is the same image league-wide, so a converted kit never keeps Adidas. Applied
  in the matcher repair pass and in the mod-list export (which otherwise refuses).
- **Every slot is named for the selected team**: a new "This slot is" column (`SlotMatch.What`) driven by the
  slot map - Team crest, NHL shield, Adidas -> Fanatics (brand), End Plastic Waste . back-hem tag,
  "<sponsor> . sponsor", Shoulder logo, and for other rows Jersey base colour (fabric regions), Numbers font,
  Helmet sponsor, material coeff (kept stock) and so on. Rows the slot map labels blank say "Blank in NHL 24".
- **Mod list validation**: `ModApply.SourceProblems` lists every replacement whose source file is missing (and
  array entries with no layers); `ApplyAsync` prints them instead of failing mid-write. Covered by
  `tests\Suite.Tests\ModValidationTests.cs`.
- **Legacy uniform conversion removed** (details below). The PS3 RW4 reader, UV remap, kit catalogue,
  normal/alpha inspection, both CLI tools, the Boston draft map, the workbench window and its two settings are
  gone; a copy is outside the repo in `<Desktop>\CIMS-backup-legacy-uniforms-20261101\`.
- Build after all of it: 0 warnings, 0 errors; 33 tests pass (the count dropped from 55 because the 22 legacy
  UV/kit tests were removed with the feature).
- **Still open**: the phantom branding-array layers (row count comes from the numbered `_N_color` textures while
  the destination array has fewer slices - Detroit jersey 4 of 6, Detroit pant 2 of 3, Florida jersey 6 of 7), so
  "Stock branding images are missing" can still refuse an export for those sets until the row count is taken from
  the array's real slice count. Apply/Restore still has never been run against the real game.
## Codex continuation — October 31, 2026 (local time)

### November 1: legacy uniform conversion removed (user decision)

The PS3 "legacy uniform" source, its atlas-remap workbench and its tooling were removed from
the suite. NHL 27 is the only uniform source. The code that was removed (RW4 reader, UV remap,
kit catalogue, normal/alpha inspection, `tools/LegacyExtract`, `tools/LegacyRemap`, the Boston
draft map and the workbench window) is kept outside the repo in
`<Desktop>\CIMS-backup-legacy-uniforms-20261101\`. Nothing in the matcher, slot map, viewer,
mod list or Apply depended on it. `LegacyUniformDir` / `LastLegacyUvMap` were dropped from
settings (stale keys in an existing settings.json are ignored).

- User clarified: **Adidas branding normal map/mask must become Fanatics**, not just
  the coloured artwork. Matcher now pairs recognised Adidas layers with Fanatics
  colour/alpha and corresponding normals from the same source layer. Export also
  builds clean coeff from Fanatics alpha. Missing assets prevent export. Separate
  branding arrays are covered; embedded full-sheet manufacturer art still needs
  proper remapping and inspection.
- User clarified: **first pants logo is usually the team logo, not the NHL shield**.
  Pants role detection finds the club mark after known league logos; crest links
  cannot choose shields. Removed the old same-layer-number fallback. Missing club
  art/normal retains stock. The End Plastic Waste lock remains.
- Fixed an existing Windows-invalid composite cache filename (`asset|asset`), using
  a hash. Composite fabric alpha now comes from NHL 24, not the NHL 27 base image.
- Tests: all new UV/alpha/cache/branding tests pass; one pre-existing test
  `Folders_with_links_into_another_tree_are_refused` cannot create its fixture
  junction in this execution environment (`mklink /J`: Access denied). It fails
  before exercising the guard. No game files were applied/changed in this session.
- Next: the roster sync and remaining broad roadmap are unchanged.

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
     `C:\Users\devin\Desktop\NHL24PC-CIMS` = NHL24PC's emulator, tools and `user\` (saves, settings, shader
     cache) + `game\` = base `game\` with the whole `game-UPDATE\` laid over it (1.60 eboot.bin, sce_sys, Patch), no
     `game-UPDATE`. The original `NHL24PC\game` and `game-UPDATE` are untouched (clean fallback). CIMS settings:
     `ApplyTargetDir = ...\NHL24PC-CIMS\game` (backup `settings.json.before-cims-merged`); `Nhl24GameDir` still points at the
     original so ORIGINAL previews stay stock. The folder meets every guard rule (no links, Data+Patch, writable).
     **Boot test passed 2026-11-01 03:45 UTC:** the user launched `NHL24PC-CIMS\Play NHL 24.bat` and played a game. The merged
     folder is the live play/apply target from now on (applies only with the game closed; one-team test first for new kinds of mods).
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

## Uniform slot mapping (investigated 2026-11-01, read only)

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

### End Plastic Waste lock (2026-11-01, user rule)

Every End Plastic Waste layer on every team's jersey is drawn as the tag on the very back hem. Layer placement is fixed per
layer, so swapping images between layers moves the image to the other layer's spot. Implemented: role `epw_tag`, placement
"back hem tag" (recognised by shape, the same image league-wide); `SlotRoles.EnforceEpwLock` resets any link (automatic or
manual) that puts a non-tag image into a tag layer or a tag into another layer; the Slot map window locks the tag layer's
role and refuses such picks with a message; `tests\Suite.Tests\EpwLockTests.cs` checks every possible link.

Placement of the other layers: not found yet. The branding array is only a chunk referenced by three bundles; the per-layer
placement must sit in the jersey's material/shader parameters (candidates: MeshVariationDatabase, ObjectBlueprint,
SurfaceShaderPreset/AppearanceShaderPreset EBX of the jersey mesh). Next: EBX-dump those for one jersey and look for per-layer
transforms. Editing placements would mean writing EBX, which the writer does not do yet; not attempted.

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

### Pants rule: no NHL shield on pants (user, 2026-11-02)

Every NHL 24 pants set already has the NHL shield on the back, so the shield never goes onto pants. `SlotRoles.EnforcePantsCrestLock`
drops any pants link from or to a shield layer (shield slots stay stock); `Convert27ViewModel.KeepShieldOffPants` runs last in
auto-match and resets any pants layer (array, normal, single `_N_color`) whose source or stock image is the shield
(`SlotRoles.LooksLikeShield`, silhouette within 40 of the shield ref, looser than the 24 used for labels); `MatchProject.ToModFile`
skips shield sources on pants at export. Test: `BrandingRulesTests.Pants_never_take_the_NHL_shield_and_shield_slots_stay_stock`.
Not committed: these files also hold the other agent's uncommitted work.

## Roster databases: every location (enumerated 2026-11-01, read only)

In-game legacy files (same in the original and the merged copy): `gamemodes/database/t3db/nhl_game.db` (+ nhl_game-meta.xml),
`gamemodes/roster/initial_experience.db`, `entry_draft.db`, `expansion_draft.db`, `threes_campaign.db` (all five use
nhl_game-meta.xml), and 8 language dbs `loc/{cze_cz,eng_us,fin_fi,fre_fr,ger_de,lng_lg,rus_ru,swe_se}.db` (+ meta). The
**6th roster location is the saved roster** in emulator save data (`user\home\1000\savedata\CUSA37933\...`): it only exists
after a roster is saved in game; today both NHL24PC and NHL24PC-CIMS have only `Profile0` (172 KB DATA). An older saved roster
is `RosterConversion\save-original\roster.db` (9,272,746 bytes; container handling in package-save.py). Sync must include it
when present, writing only NHL24PC-CIMS's copy, backed up first, never touching league* rows of a career.

Consistency (1.60 dumps in `RosterConversion\more-dumps\`, nhl_game 1.60 in `more-dumps\nhl_game_160`): all five dbs have the
same table sizes (exhibitionplayers 17,766 rows, exhibitionrostertable 19,030). Players present in several dbs have **identical
data** (0 field differences besides row index). Rosters differ a lot: team-membership differences vs nhl_game =
initial_experience 8,148, entry_draft 6,790, expansion_draft 6,790, threes_campaign 5,980; each of the four holds ~1,100
players nhl_game does not. (`RosterConversion\nhl24-csv` is an older nhl_game, 8,132 players; do not use it as the 1.60 stock.)
So the four mode dbs are older roster snapshots; a sync that should make every mode current must write the same rosters into
all of them.

**Hard rule, layout guard (user, 2026-11-01):** every db write (sync, RosterPort, overrides) keeps NHL 24's layout: same
tables, columns, bit widths, table order, max/valid record counts (never grow past stock max; reuse rows), key scheme
team*40+slot, sort order, string table layout, recomputed CRCs, no NHL 27-only columns. A layout guard compares the written
db's header and table descriptors byte for byte with stock and refuses the write on any difference other than record contents
and CRCs; it runs on every db in the target list, including the saved roster.

### Sync rule refinement (DB thread, 2026-11-01)

Do not copy nhl_game rows into the other dbs. Apply the same NHL API result to each db on its own terms: match players by id,
move bio team + roster row (key team*40+slot) using that db's own free slots and rows. Post-sync check: for every NHL-rostered
player, team, jersey number and position are identical across all 5 dbs (and the save when present); slot order and lines may
differ. Players a snapshot has that nhl_game lacks are left alone unless the API rosters them; then they are added to every db
lacking them, within table max counts (layout guard applies).

### Roster sync, read-only planning (built 2026-11-01)

- `src\Suite.Rosters\NhlApi.cs`: standings/now + roster/<abbrev>/current, cached per day in
  `%LOCALAPPDATA%\CenterIceModdingSuite\nhlapi\<date>\`, 1 s pacing, 429 → Retry-After / exponential backoff.
- `data\nhlapi_teams.tsv` (hand-editable): API abbrev → NHL 24 team index (UTA → 22, LAK → 13, SJS → 25, TBL → 26, VGK → 30).
- `RosterSync`: bio decoding (year since 1900, 0-based month/day; position 0 C, 1 LW, 2 RW, 3 D, 4 G; `game_id` is EA's id,
  not the NHL id), player matching (birth date + last name, then birth date + full name, then unique full name), per-db plan
  (Move, Number, Position, Release to FA, NotInDb, Same). `RosterCsv` reads the bio table from Worker CSV dumps (stopgap).
- `tools\SyncPlan`: plans all 5 dumps against today's API. 2026-11-01: 32 teams, 803 players; nhl_game moves 503, numbers 296,
  positions 72, releases 240, not in db 48, already right 225 (the other dbs: 494–509 moves, 257–341 releases, 75–79 not in
  db); 721 API players matched in every db, 38 in some, 44 in none. Report in `%LOCALAPPDATA%\CenterIceModdingSuite\sync-plan.txt`.
- (Superseded by the writer below.)

### Roster sync writer (built 2026-11-02, commit 6e320d4; offline on copies, nothing applied to a game)

How the tables link (verified on all five dbs): `exhibitionrostertable.playerindex` → `exhibitionplayers.index`;
`exhibitionplayers.playerid` = bio `game_id`. **2,125 players have several player indices**: always match roster rows by
the set of a player's indices. Bio `team` = current team (NHL or AHL), `proteam` = NHL organisation; `positionprimary`
0 C, 1 LW, 2 RW, 3 LD, 4 RD, 5 G; `handedness` 0 L / 1 R. Roster: one row per player per team, keys unique and sorted,
team*40 + 0..39; `rosterstatus` 1 exactly for the 20 dressed (l1-4 c/lw/rw, l1-3 ld/rd, g1, g2); `captain` 1 C, 2 A
(stock teams: a C and two A's, or three A's). Free agents: `exhibitionfreeagents.playerindex`, sorted. The three small
roster dbs (entry_draft, expansion_draft, threes_campaign) have no exhibitionteams; they use nhl_game's (AHL affiliates).

T3DB facts that drive the guard (FifaLibrary via the Worker): in-use records sit at the front; on save a record counts as
in use when it is not all-zero (so a free-agent row with player index 0 cannot exist; Paul Stastny uses index 0 and is
skipped with a warning). The file header + table directory are `24 + 8*tables + 4` bytes (980 for nhl_game, 340 for
initial_experience, 116 for the small ones). The table area never moves; only each table's records CRC follows its last
in-use record, so `recordsCrcAt` shifts by (in-use change x record size). An unchanged dump+import equals db-resave.

Code (`src\Suite.Rosters`):
- `T3Csv`: Worker CSV table, cells kept as strings, `ReplaceUsed` refuses to grow a table.
- `RosterWriter.Write`: pass 1 removes everyone from club rosters he no longer belongs on, pass 2 adds rows in each
  team's free slots, updates bio (team, proteam, jersey, position/primary) and the free-agent list. Released NHL players
  go to the club's AHL affiliate when listed as its prospect, otherwise free agency. Teams that changed get lines
  repaired: vacated line slots filled by natural position, arrivals first, then depth from their old team; special
  units (PP/PK/EV/3v3/shootout/extra) inherit from the player who took the leaver's line slot, never twice in a unit;
  a leaving C is replaced by the top A, letter count kept; a slot nobody can fill signs a young free agent (not one the
  sync just released); numbers the API did not give are de-duplicated per NHL team.
- `RosterDbJob`: dump, write, db-import into `dbwork\<db>\<db>.db`, re-dump, guard, `Verify` (every API player on his
  team exactly once with his number; no line slot held twice).
- `RosterSyncRun.RunAsync`: API rosters + prospects; `roster/<team>/current` omits LTIR and AHL-assigned players, so every
  would-be release is looked up in the player search (search.d3.nhle.com, gives club) and his player page (latest
  season row's league): AHL → affiliate, NHL/none → kept on the club (injured), elsewhere → released. Then all dbs,
  then the cross-db check. `ExportStockAsync` copies the five stock dbs + meta out of the game via the read view
  (Worker export-legacy) so a sync always starts from stock. `ToModFile` → mod with `Legacy` replacements
  (`gamemodes/database/t3db/nhl_game.db`, `gamemodes/roster/<db>.db`), files copied to `rostersync\<date>\`.
- `SavedRoster`: roster saves (`user\home\<id>\savedata\CUSA37933\<name>\` DATA/INDEX/NAME). INDEX = 80-byte entries
  (name[32], 8 bytes, size/start/end u64); record = u32 (size-16), 12 bytes, 0x01, LZ4 frame (64 KB independent blocks,
  content checksum), u32 decoded length. The decoded blob is the T3DB **plus an 18-byte trailer** (`d92eb758 00000000
  <db size u32> 00000000 0100` in both saves seen; first 4 bytes unknown, not a plain CRC32 of the db) kept as is.
  Saved rosters hold the exhibition tables only. `WriteSaves` writes CIMS-copy saves only, backup in
  `%LOCALAPPDATA%\CenterIceModdingSuite\save-backups`, refuses while the game runs. Round trip tested on a copy of
  `RosterConversion\backup\before-restore\RosterCustom2`. NHL24PC-CIMS has no roster save yet (only Profile0).
- `ModFile.Legacy` / `ModApply.WritePlan`: the Worker plan takes `Legacy: [{Name, Source}]` (NHL24Kit `LegacyWriter`).
- App: Teams & roster page (`Pages\RostersPage.cs`): Sync rosters (optional "Only clubs" trial, e.g. BOS), per-db
  expandable change lists, Add to mod list, Write saved roster.
- `tools\SyncWrite <teams.tsv> <meta.xml> name=db...`: same run from the command line; `tests\Suite.Rosters.Tests`
  (6 tests, writer on a synthetic db, guard, CSV, save container; `CIMS_SAVE_SAMPLE` for a real save).

Result 2026-11-01 API on the 1.60 stock dbs: nhl_game moves 306, call-ups 198, to AHL 51, releases 182, numbers 294,
positions 70, signed 9, not in db 48; the other four similar (75-79 not in db). All five: layout guard passes, read-back
OK, 0 cross-db disagreements, team audit clean (no lost line slot, 2 goalies dressed, letters kept, 20 dressed, no
duplicate numbers). Outputs in `%LOCALAPPDATA%\CenterIceModdingSuite\dbwork\<db>\` (`changes.tsv` per db).

**Apply blocked (2026-11-02, user-approved full-league apply):** `tools\RosterApply` ran the sync (all checks passed, same
counts as above, 0 saved rosters in the CIMS copy), saved the mod (`%APPDATA%\CenterIceModdingSuite\mods\Roster sync
2026-11-02.json`) and took the suite backup, but the Worker apply stops before writing: "No TOC refers to legacy chunk
99f5f5fb-1994-7bb1-6c5e-69020d850bad" (nhl_game.db). The legacy dbs are TOC chunks (`chunk-blocks`: isToc true; nhl_game.db
in Patch cas_01 @98371920, 2,085,904 stored; initial_experience.db in Data cas_01 @946136836), listed in the TOC's own chunk
table, which the Worker's DirectWriter opens header-only, so `FindReferences` never sees them (no "ignored entry" lines
either). TOC files are obfuscated (start 00 d1 ce 01), so a raw GUID search finds nothing. Game copy verified unchanged
(only the suite's FMT marker). Fix options: the suite's own TOC-chunk writer (append to CAS + repoint the TOC chunk entry),
tested on D:\CIMS-test first; or FMT's GUI legacy import by hand.

**Solved 2026-11-02 with the suite's own legacy writer (commit 658157e) - APPLIED to NHL24PC-CIMS.**
Format notes (NHL 24 PS4 build):
- TOC: 0x22C-byte signed header (magic 00 D1 CE 01), then BE u32 fields; field 4 = chunk id table offset, 5 = count,
  6 = location table offset (relative to 0x22C). Id record 20 bytes: Guid.ToByteArray() reversed + u32 (low 24 bits =
  u32 index into the location table). Location 12 bytes: [0][patch][catalog][cas], BE offset, BE stored size. No SHA1 for
  TOC chunks anywhere. nhl_game.db + the index chunk are in Patch\Gen4b\globals.toc; the four roster dbs in Data\Gen4b\globals.toc.
  CAS: {Patch|Data}\Gen4b\superbundlelayout\nhl_installpackage_{cat:00}\cas_{cas:00}.cas.
- Blocks: BE u32 raw size, byte type (0x11 Oodle, 0x00 stored), byte flags (0x70 | bits 16-19 of the stored size),
  BE u16 low bits of the stored size. Raw blocks are 256 KB.
- Legacy index chunk (bb30a247-fe2c-e555-a3bf-938bde520b43, found by scanning small chunks for the file name): 56-byte
  records = u64 LE name offset, stored start, stored end (0/0 = whole chunk), raw offset, raw size, then the chunk id
  (Guid.ToByteArray()). nhl_game.db shares chunk 99f5f5fb with cannedmessage.csv, nhl_gm_simulation.data,
  nhl_game-meta.xml and playercolors.csv; each roster db has its own chunk.
- Writer: same-size replace only; re-encodes changed blocks (Oodle Kraken, level Optimal2) and keeps the others as stored,
  appends the chunk to its own CAS, repoints that chunk id's TOC entries, updates the stored ranges of every file in the
  chunk in the index, then appends/repoints the index chunk. Restore = ApplyTarget.RestoreFromBackup (truncate CAS + copy TOCs).
- The NHL24Kit Worker keeps one shared asset cache (`runtimes\...\_GameCaches\nhl24ps4.cache`) for every game folder, so its
  reads show stale (stock) data after our writes. Independent checks used a cache-free copy of the runtime at
  `%LOCALAPPDATA%\CenterIceModdingSuite\worker-fresh`. NHL24Kit/FMT opened on the CIMS copy may show stock data until
  their cache is rebuilt.
Results: D:\CIMS-test\NHL24-merged and NHL24PC-CIMS\game both: apply → own read-back identical (5 dbs), restore → 0
differences (CAS lengths, TOC hashes, last MB of every CAS unchanged) and stock dbs back, re-apply → identical; cache-free
Worker read-back identical on both. Changed files: Data/Patch globals.toc and Data/Patch cas_01.cas (appended
3,347,017 + 1,854,231 bytes). Backups: `%LOCALAPPDATA%\CenterIceModdingSuite\backups\20261102-014435` (CIMS, stock) and
`...\20261102-013827` (D:\CIMS-test, stock; that copy is left with the rosters applied). App: Teams & roster → Apply to CIMS copy.

**Moved 2026-11-02:** the CIMS game copy is now `C:\Users\devin\Desktop\NHL24PC-CIMS` (same-drive rename; the original
NHL24PC stays in Downloads). Updated: settings.json ApplyTargetDir, both CIMS backup snapshots' Game field, and the stock
baseline pointer (`backups\baseline-<sha1 of lowercased target>.txt`, now baseline-AB280E5FA180.txt → 20261102-012421).
The launcher and emulator configs use relative paths. NHL24Kit's `gamelinks\game` junction still points at the old path;
the Worker re-links it when it next opens a game. Boot with `NHL24PC-CIMS\Play NHL 24.bat`.

**Lines + online mirror (2026-11-02, commit f0ce7e2; built, NOT yet applied: the game was running).** Audit of the first
apply: lines valid but repaired from old depth (7 injured dressed, 9 backups rated above the starter, ~1,900 slots differing
between dbs) and nhl_game's `customonlinerostertable` (a row-for-row copy of exhibitionrostertable in stock) not updated.
No other roster/line data exists: the 4 small dbs only have exhibitionrostertable; league*/custom* are empty or custom teams;
loc dbs are names. Lines live in the roster rows (no separate table). `LineBuilder` now rebuilds every NHL team by ratings
(skill_overall + c_* attributes, from the main db for every db), injured scratched, overrides in
`%APPDATA%\CenterIceModdingSuite\data\lines_overrides.json`. Result nhl_game: 0 injured dressed, 0 better healthy scratches,
0 backup above starter, 3 teams with a line out of OVR order (natural-C rule), mirror matches; other dbs differ only on 13-14
teams where they lack a player. BOS has 11 F in the db (Hagens, Khusnutdinov missing) so Aspirot (D) plays l4rw.
Review sheet: `<Desktop>\CIMS_lines_review.html` (scratchpad script lines_review.py). Ready to apply: mod
"Roster sync 2026-11-02" now points at `%LOCALAPPDATA%\CenterIceModdingSuite
ostersync6-11-02-lines\` (apply with
`tools\LegacyWrite apply <CIMS>\game <mod.json> --restore-check` or the page button once the game is closed).

**Applied to NHL24PC-CIMS 2026-11-02 (game closed, user approved):** rosters + NHL 27 ratings (commit eb14502) + ratings-based
lines + online mirror + portrait slots (5 dbs, `rostersync6-11-02-portraits`), then 752 portraits with the suite's own
bundle writer (commit after eb14502; `Bc3`, `BundleChunks`, `tools\PortraitWrite`): encode + write in seconds, 752/752 read
back identical, dbs identical, cache-free Worker sees the new pictures, restore test 0 differences. The Worker path
(tools\PortraitApply) took ~30 s per picture and was stopped before it touched a TOC; the copy was restored to the
pre-portrait backup first. Portrait facts: `p<artid>` bundles in Data\Gen4b\contentlaunchsb.toc (CAS package 02), one bundle
per portrait (3 EBX, 1 RES, 1 chunk; EBX order varies), chunk = BC3 512x340 single mip, PS4 1D-thin tiled to 128x88 blocks
(180,224 bytes), one Oodle block; manifest SHA1 per data entry = SHA1 of its stored bytes. Backups: pre-portrait
`backups61102-144006`, pre-ratings `20261102-135133`, stock baseline pointer `baseline-AB280E5FA180.txt`.
**Caution:** the NHL24Kit Worker now reports the CIMS copy as modded (state from the stopped run = the pre-portrait game).
A Worker apply/restore (ModApply's Apply/Restore page) would first roll the portraits back. Use the suite writers.

Still to do: apply the lines build (done above); check in game (Boston, Detroit/Larkin, Utah, lines); portraits from NHL 27 (+ API fallback); portraits in the same button;
new players (NotInDb, e.g. James Hagens) from NHL 27 rows or templates; better line order (ratings instead of old depth);
confirm the saved-roster trailer by saving a roster in game.

## Center ice importer (TheFaceoff.net) — queued

User wants it, working like NHL27Kit's Faceoff window (feature reference only; our own code). Checked 2026-11-01:
robots.txt allows crawling (`Disallow: *?lightbox=`; crawl-delay 10 for some bots). The site's FAQ: "Use of graphics for
personal use only is permitted with credit and link back to thefaceoff.net". So: downloads only on user action, local cache
(`%LOCALAPPDATA%\CenterIceModdingSuite\faceoff`), ≥ 10 s between requests, visible credit + link, and **never include
TheFaceoff.net images in exported/shared mod packs**. Pages: `/ice-nhl`, `/ice-all`, `/ice-ahl`… (league lists),
`/center-ice-archive`, dynamic center-ice items (sitemaps `dynamic-center-ice_*`). Utah's Mammoth center ice → ice_centerlogo_22.

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

## Documented exception: Fanatics donor kits use converted NHL 27 maps (owner's choice, 2026-11-12)

The no-cross-game rule for mesh-UV-addressed jersey/pant/sock colour/normal/coeff has one owner-approved exception: the
64 NHL 27 Fanatics kits installed through the Anaheim-home donor (`KitDonor`, painted mode). The owner picked
"NHL 27 jerseys, NHL 24 shine" for the detail NHL 27's sheets carry. The maps are converted, not copied
(`KitDonor.ConvertToNhl24` / `ConvertMap`):
- colour: NHL 27 finished RGB; alpha = NHL 27's plain-sheet fabric-zone shapes with NHL 24 zone values (`ZoneMap`:
  each NHL 27 zone takes the NHL 24 zone it overlaps most; body 64/42/51/36 → 36, side panels → 109, collar → 146,
  255 kept). NHL 27's finished sheets have alpha 255 everywhere (NJ away's grey marbling); the donor's Adidas zones
  add a shoulder-yoke zone (73) that Fanatics jerseys do not have (shoulder dimples). Pants zones are identical;
- normal: NHL 27 X/Y (Fanatics seams and hem stitching) + the donor's alpha; the jersey's blue (NHL 24 fold shading,
  which carries the Adidas yoke seams) is flattened to the donor's plain-fabric level. NHL 27 leaves blue at 255;
- coeff: the donor's stock base coefficient for both base and finished slots (rule 10);
- hem fitting (`HemFit`) and, later, sock fitting per team. Chest sponsors are put on the right chest (`SponsorSide`).
Nothing else crosses: any other kit route still follows the rule.

Fanatics donor-kit rules (all in the tool, all with tests, 2026-11-12):
- Coefficient: base slot = donor stock base coeff; finished slot = donor stock *finished* coeff (packed differently:
  base 126/127/255/0, finished 127/27/0/255) with the donor's logo areas set to plain fabric.
- Fabric zones: NHL 27 zone shapes in NHL 24 values; a zoneless NHL 27 sheet (Vegas home) keeps NHL 24's zones minus the
  Adidas yoke (73). Converted alpha is final (no rule-11 "auto" at encode). No kit may carry zone 73.
- Hems (`HemFit`): front (bottom) and back (top, mirrored) move together, only when NHL 27's last stripe runs past the
  team's NHL 24 hem; the target is never above 0.948 / 0.04 (a high NHL 24 hem is a design, not a limit).
- Socks (`SockFit`): the NHL 27 stripe block moves up when it ends lower than the team's NHL 24 block (never above 70%).
  NHL 27 sock weave masks (`*_knee_weave` etc., Vegas/LA) have no NHL 24 slot and are left out.
- Logos NHL 27 keeps only as layers (`UnpaintedLayers`): chest sponsors and pants team logos go in the donor slot of the
  same role when the finished sheet does not show them; the shield never fills a team-logo slot. Chest sponsors sit on
  the player's right chest (`SponsorSide`).
- Back-hem tag: every kit gets NHL 27's own End Plastic Waste layer (found by picture in its NHL 27 layers), else the
  standard one; it goes to the donor's tag hotspot (not slice 0). Chicago home keeps its 100 tag via the slot map.
- Numbers/letters: the team's own NHL 24 font sheets byte for byte; only a renamed team (Arizona → Utah) gets NHL 27's
  glyphs fitted into NHL 24's layout (`FontRemap`). Number/name/captain spots come from the team's NHL 24 stamp.
- `FanaticsKitCheck` runs after every donor install and prints `KIT CHECK OK/FAIL` per kit from the installed files.
- CAS growth (open): every install/repair appends the cloned kit data to the donor's CAS
  (`nhl_installpackage_05/cas_05.cas`, where Anaheim home lives) and never reclaims the old copy. A file may not pass
  4 GB; the install then stops and restores (the test copy hit this after ~6 full rebuilds). A full 64-kit install adds
  about 0.85 GB there (measured 1.27 → 2.12 GB). Fix to build: spill to a fresh CAS (record location words) or compact unreferenced data.

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
  - Base slots use the exact NHL 27 **`_base_color`** art with rule 11's NHL 24 fabric-region alpha. The plain `_color`
    preview already contains crests and branding, so using it here duplicated the logos drawn by the branding arrays
    (Utah's away crest). A sponsor with no array destination, such as Utah's SME/1Password art, must be added selectively
    with **Stamp a logo**; manual stamps are preserved.
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
