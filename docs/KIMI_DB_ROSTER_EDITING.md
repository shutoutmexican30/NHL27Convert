# NHL 24 database and roster editing: everything we know (handoff for Kimi, 2026-11-22)

Owner: Devin Sutton. Repo: `D:\dev\NHL27Convert` (branch `checkpoint-2026-11-22`, commit `2a7603e` saves all work as of
that day). This file is self-contained for database and roster work. Uniform/texture work is in
`docs/HANDOFF_KIMI_2026-11-22.md`; the long history is `docs/HANDOFF.md`; cap/contract research is
`docs/CAP_CONTRACTS_FINDINGS.md`.

---

## 0. Owner rules (do not break)

1. **Never write the main game** (`D:\NHL24PC_V3.2\NHL24PC`) while testing. Work on a full copy under `D:\CIMS-test\`.
   Install to main only when the owner says so, game closed, with a backup.
2. **Stay inside NHL 24's structure.** Same file size, tables, fields, bit widths, record sizes, slot counts. Use only
   empty slots NHL 24 already has. Never add NHL 27-only columns, ids or assets (heads, gear models, masks > 511, leagues).
   No new leagues (stockleagues is 33/33). Do not replace any existing NHL 24 team.
3. **NHL 27 may supply data, not assets:** names, birth dates, ratings (range-checked against NHL 24's meta), colours.
   Every asset id (head, hair, gear, stick, gloves, audio, surname audio, casual gear) must be one stock NHL 24 already uses.
4. **Draft picks stay stock** (2023-2028 grid). The owner said no to moving them to 2026-2031. The roster sync rewrites
   them by default (see 6.4): put them back.
5. **Organizations:** a player no NHL club lists (not on an NHL roster, not in any prospect pool) goes to free agency;
   prospects stay on their own club's AHL affiliate.
6. **Salary cap patch: don't use it** (owner unsure it works). Don't copy `user\patches\nhl24.xml` cap entries anywhere.
7. Every write: backup first, read back, restore on failure, game closed. Never FMT, never the NHL24Kit Worker.
8. **Get it right the first time.** Redo cycles waste a lot of the owner's usage. Before running a roster/data change,
   check its side effects against these rules (who gets released, where prospects land, draft picks, assets).
9. **Boot-test past the title screen ("press X")** on the copy before calling anything done. Loading a menu is not enough.
10. Report plainly what was verified and what was not.

---

## 1. Where the databases live

All are T3DB files stored as **legacy files** inside Frostbite TOC chunks (not loose files):

| Legacy name | Index (TOC) | Contents |
|---|---|---|
| `gamemodes/database/t3db/nhl_game.db` (+ `nhl_game-meta.xml`) | `Patch\Gen4b\globals.toc` (and a base copy in Data) | Main DB, 119 tables: teams, arenas, players, rosters, jerseys, equipment, colours, contracts, schedules, draft picks, custom/league tables |
| `gamemodes/roster/initial_experience.db` | `Data\Gen4b\globals.toc` | 39 tables: players, rosters, teams, arenas (no colour table). One season "older" (contract lengths of 2022-23) |
| `gamemodes/roster/entry_draft.db`, `expansion_draft.db`, `threes_campaign.db` | Data globals.toc | The 11 player tables only (no exhibitionteams; they use nhl_game's) |
| `loc/{eng_us,cze_cz,fin_fi,fre_fr,ger_de,lng_lg,rus_ru,swe_se}.db` | both | Language string tables (see 9) |

- All five roster DBs share `nhl_game-meta.xml` (field names, ranges `rangelow`/`rangehigh`, string widths).
- **Every file exists twice** (base index and update index). Writers must update every copy, or one stays stock.
  `LegacyFiles.Replace` / `ReplaceEach` do this.
- nhl_game.db shares chunk `99f5f5fb-1994-7bb1-6c5e-69020d850bad` with cannedmessage.csv, nhl_gm_simulation.data,
  nhl_game-meta.xml and playercolors.csv. Each roster DB has its own chunk.
- **6th roster location: saved rosters** in emulator save data, `user\home\1000\savedata\CUSA37933\<name>\`
  (DATA/INDEX/NAME). See 8.
- **Franchise / Be a GM saves** hold 34 `league*` tables (their own copy of rosters, contracts, dead cap, calendar).
  Existing saves can't be patched by DB edits. Test DB changes in a **new** franchise.

---

## 2. T3DB file format (verified on all five stock DBs, every CRC reproduced)

```
0   "DB\0\x08", u32 0, u32 file size, u32 0, u32 table count, u32 header CRC (of bytes 0..20)
24  table list: count x [4-char short name][u32 offset from end of the list CRC], then the list CRC
table: 32-byte header (u32: 2, record bytes, record bits, 0, (in-use << 16 | ?), 0xFFFF0000, field count, 0)
       u32 CRC of those 32 bytes
       field descriptors, 16 bytes each: u32 type, u32 bit offset, 4-char short name, u32 bit depth
       the records (record bytes each, little-endian bit order inside a record)
       u32 CRC of everything from the descriptors to it
CRC = CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, not reflected, no final xor)
```

- Field types: 3 = integer, 0 = fixed-width string (UTF-8, NUL padded, needs text+1 bytes). Others are read-only to us.
- **Integers are stored as value − rangelow** (rangelow from the meta XML). E.g. `contractstatus` lo −1.
- **In-use records sit at the front.** The in-use count is the high 16 bits of table-header u32 +16. Slots = (section end − 4 −
  records start) / record bytes. Stock has many spare slots (bio 13,300 slots, ~9,100 in use).
- Header + directory = `24 + 8*tables + 4` bytes (980 nhl_game, 340 initial_experience, 116 the small ones). The table area
  never moves.
- **Blank-row trap (the 2026-11-22 bug):** older dump/import code counted a record "in use" when it was not all zero. Empty
  bio slots read `friendenemycompat0 = -256` (non-zero raw), so a re-import marked all 13,300 slots in use (4,178 blank
  players). Fix is in commit `2a7603e`: `T3DbIo.RepairInUse` + an import guard. Always check `in-use` counts and blank names
  after a write.
- Code: `src/Suite.Rosters/T3Db.cs` (load, GetInt/SetInt, GetString/SetString, InsertRecord/DeleteRecord within spare
  slots, Save recomputes CRCs; same file size always). `T3DbIo.cs` = the suite's own dump/import/layout (replaces the Worker).

---

## 3. Writing a legacy DB into the game (TOC chunk writer)

- TOC: 0x22C-byte signed header (magic `00 D1 CE 01`), then BE u32 fields; field 4 = chunk id table offset, 5 = count,
  6 = location table offset (relative to 0x22C). Id record 20 bytes: `Guid.ToByteArray()` reversed + u32 (low 24 bits =
  index into location table). Location 12 bytes: `[0][patch][catalog][cas]`, BE offset, BE stored size. No SHA1 for TOC
  chunks.
- CAS path: `{Patch|Data}\Gen4b\superbundlelayout\nhl_installpackage_{cat:00}\cas_{cas:00}.cas`. Offsets/sizes are u32:
  **a CAS can never pass 4 GiB.**
- Chunk blocks: BE u32 raw size, byte type (0x11 Oodle, 0x00 stored), byte flags (0x70 | bits 16-19 of stored size), BE u16
  low bits of stored size. Raw blocks 256 KB. Oodle = `oo2core_9_win64.dll` (Kraken, level Optimal2). It is now at
  `D:\Tools\FMT\oo2core_9_win64.dll` (the suite searches `<drive>\Tools\FMT*`) and in `fmt-src\...\ThirdParty\Compression`.
- Legacy index chunk `bb30a247-fe2c-e555-a3bf-938bde520b43`: 56-byte records = u64 name offset, stored start, stored end
  (0/0 = whole chunk), raw offset, raw size, chunk id.
- Writer (`src/Suite.Core/Frostbite/LegacyFiles.cs`): same-size replace; re-encodes only changed blocks, appends the chunk to
  its own CAS, repoints that chunk's TOC entries, updates the index ranges, appends/repoints the index chunk. A DB write
  appends a few MB to `nhl_installpackage_00` cas_01/cas_02 (not cas_05, the kit CAS near 4 GB).
- Guard + backup: `ApplyTarget.Problems(game)` (game closed, no links, writable, no `-mods` folder), `ApplyTarget.Backup`
  (copies TOCs, records CAS lengths), `ApplyTarget.RestoreFromBackup` (truncate CAS + copy TOCs back), `ApplyTarget.Compare`.
- **One-call API (new 2026-11-22):** `Suite.Convert27.GameDatabaseEdit.Read(game, settings, "nhl_game")` and
  `.Write(game, settings, db, originalBytes, editedBytes, say, backupRoot)`: refuses a size change, refuses if the game's
  copy changed since read, backs up, writes every copy, reads back, restores on failure, records undo. Tested on a copy.

---

## 4. Tables and how they link

| Table | Key facts |
|---|---|
| `exhibitionplayerbiotable` | One row per player. `game_id` = EA id (integer). Strings: firstname, lastname, nickname, birthplace. `team` = current team (NHL or AHL), `proteam` = NHL organization. Dates are year−1900 and **0-based month/day**. `position` 0 C, 1 LW, 2 RW, 3 D, 4 G; `positionprimary` 0 C, 1 LW, 2 RW, 3 LD, 4 RD, 5 G; `handedness` 0 L / 1 R. `artid` + `hasportrait` = portrait texture `p<artid>`. Asset ids: headid (generic heads ≥ 60000), hairid, eyebrowid, skincolor, audioid, surnameid, casualgearfk, introsongid... |
| `exhibitionplayers` | `index` (player index) → `playerid` = bio `game_id`. **2,125 players have several indices**: match roster rows by the set of a player's indices. |
| `exhibitionrostertable` | One row per player per team. `key` = team*40 + slot (0..39), unique and sorted. `playerindex`, `team`, `jersey`, `captain` (1 C, 2 A), `rosterstatus` (1 = dressed: exactly 20), `playerstyle`, and **93 one-bit line-slot columns** (l1-4 c/lw/rw, l1-3 ld/rd, g1, g2, PP/PK/EV/3v3/shootout/extra units). Lines live here; there is no separate lines table. |
| `customonlinerostertable` (nhl_game) | Row-for-row mirror of exhibitionrostertable in stock. Keep it mirrored. |
| `exhibitionfreeagents` | `playerindex`, sorted. Index 0 can't be stored (all-zero row = blank). |
| `exhibitionskaterai` / `exhibitiongoalieai` | Ratings by `game_id`: c_* and skill_* columns (skill_overall = OVR), traits, X-Factors. |
| `exhibitionskaterequipment` / `exhibitiongoalieequipment` | Gear by `game_id`. Goalie equipment = colour-zone table (15 zones per pad/blocker/trapper/stick), not item ids. |
| `exhibitionteams` | Team index, names (teamname/nickname/cityname/abbrname), colours (fe/pres/overlay primary/secondary), `artid`, `artabbr`, `ahlaffiliate`, goalie masks (`goaliemask0_starter/backup`, ids ≤ 511). NHL clubs are 0-31; 32-35 All-Star teams. **Team 22 = Utah Mammoth** (was Arizona; artid 22 and artabbr PHX kept). |
| `exhibitionarena` | Arena name/city/capacity/year, `crowdlightcolor`. |
| `stockteamjerseys` | The selectable uniforms. Keyed by team + style + variant (not by position). Fields include `isdefault`, `islightcolor`, `orderid`, `locstring`, `jerseyindex`. 2,342/2,799 in nhl_game. **Uniform rows must exist in both nhl_game and initial_experience.** |
| `stockteamequipmenttable` | Team helmet/gloves/pants colours per team+brand+jerseyartid+equipment+era+variant: zone1-15 colours. 8,805/12,781 in stock nhl_game. |
| `stockteamcolorstable` (nhl_game only) | Team colour palette (team 22 = rows 76-81). |
| `stockteamsalaryextras` | Per team dead cap `cbaexpenseyear0..24` and `retainedtradesalaryyear0..24` (whole dollars, year 0 = current season). |
| `exhibitiondraftpick` | 1,344 rows = 6 years × 32 teams × 7 rounds: round, owner, year, origteamid, pickoverall. Stock years 123-128 (2023-2028). |
| `leaguedraftpick`, `league*` | Franchise tables, empty in the DB, filled in saves. Don't touch. |
| `customteamjerseys`, `customskater*` etc. | Create-a-team / custom data. Full or unused. |

Prospects themselves (future draft classes) are **not** stored. Franchise mode generates them. "Draft class" in these DBs
means pick ownership.

### Contracts (bio fields, see CAP_CONTRACTS_FINDINGS.md §3)
`contractdollars` = cap hit in **$5,000 units** (14 bits, McDavid 2500 = $12.5M); `contractlength` years left;
`contractstatus` −1 none / 0 signed; `contractis2way`, `contractismultiyear`; `extensiondollars/length/is2way`;
`yearufa`, `yearwaivereligible`, `draftyear`, etc. are year−1900. **No NTC/NMC field exists in NHL 24** (clauses live only in
the suite's contracts file). Edit all 5 DBs consistently.

### Start season / cap
Season year and the cap live in the eboot / league settings (0x318-byte profile), not the DB. See CAP_CONTRACTS_FINDINGS.md.

---

## 5. Database facts per DB

- All five have the same table sizes (exhibitionplayers 17,766 slots, rostertable 19,030). Players present in several DBs
  have identical data. Rosters differ a lot: the four mode DBs are older snapshots, each with ~1,100 players nhl_game lacks.
- Rule: **apply the same API result to each DB on its own terms** (its own rows and free slots; never copy nhl_game rows
  over). Afterwards, every NHL-rostered player must have the same team, number and position in all 5 DBs.
- Stock in-use counts (clean, 2026-11-22 copy): bio 9,114 (nhl_game), 9,074 (IE), 9,178 (draft DBs), 9,185 (threes).
- Stock nhl_game ships things the roster checks flag (669 problems: duplicate numbers on non-NHL teams etc.). Only flag
  problems an edit introduces (the editor compares against the load-time baseline).

---

## 6. Roster sync (NHL API → all DBs)

Code: `src/Suite.Rosters` (`NhlApi`, `RosterSync`, `RosterWriter`, `RosterDbJob`, `RosterSyncRun`, `NewPlayers`, `Ratings`,
`LineBuilder`, `GoalieGear`, `Portraits`, `DraftPicks`). CLI: `tools\SyncWrite`.

### 6.1 Inputs
- NHL API (no key): `https://api-web.nhle.com/v1/` roster/<abbrev>/current, prospects/<abbrev>, standings; player search
  `https://search.d3.nhle.com/api/v1/search/player`. Cached per day in `%LOCALAPPDATA%\CenterIceModdingSuite\nhlapi\<date>\`.
- `data\nhlapi_teams.tsv`: API abbrev → NHL 24 team index (UTA → 22, LAK → 13, SJS → 25, TBL → 26, VGK → 30).
- NHL 27 DB dump (CSV) in `%LOCALAPPDATA%\CenterIceModdingSuite\nhl27db` (ratings, bios).
- `%APPDATA%\CenterIceModdingSuite\data\roster_additions.json`: extra prospects to create (ApiPlayer JSON).
- `%APPDATA%\CenterIceModdingSuite\data\lines_overrides.json`: hand line overrides.

### 6.2 Matching
Birth date + last name, then birth date + full name, then unique full name (accents normalized; ø needs special handling).
API `roster/current` omits LTIR and AHL-assigned players, so each would-be release is checked with the player search and
his player page: AHL → affiliate, NHL/none → kept (injured), elsewhere → released.

### 6.3 What the writer does
- Pass 1 removes everyone from rosters he no longer belongs on; pass 2 adds rows in each team's free slots (key team*40+slot),
  updates bio team/proteam/jersey/position, and the free-agent list.
- NHL players no longer rostered: club's AHL affiliate if he's in its prospect pool, else free agency.
- **(Added 2026-11-22)** AHL affiliate rows whose player no club lists go to free agency; listed prospects go to their own
  club's affiliate. Empty AHL slots are refilled with young free agents ("Signed").
- Lines: `LineBuilder` rebuilds every NHL team by ratings (12 F by OVR into 4 lines with natural C/LW/RW, 6 D, 2 G; injured
  never dressed; PP/PK/etc. from ratings), same in every DB.
- New players (`NewPlayers`): created in empty slots of bio, players, ratings and equipment tables, in key order. Identity and
  ratings from NHL 27 (range-checked) or the API; **every asset id from a donor** (a real NHL 24 player with a generic head,
  same position group, skin and hair, closest age). game_id = NHL 27's if free, else next free from 32000. No portrait
  (artid 0) until the portrait step. `AlignPlannedWithMain` / `CopyFromMain` make the older DBs agree with nhl_game.
- Ratings: NHL 27's where it has the player, else nhl_game's, only c_* / skill_* columns, each range-checked.
- Goalie gear: NHL 27 colour zones; models stay NHL 24's; a mask only if NHL 24 already uses that id.
- **Draft picks:** `DraftPicks` rewrites `exhibitiondraftpick` in every DB to the 2026-2031 grid from `data\draft_picks.tsv`.
  The owner wants stock picks: copy the original table's values back after the sync (done that way on 2026-11-22).
- Guard: layout identical (header + table descriptors byte for byte), read-back, cross-DB check (0 disagreements).

### 6.4 How to run it (what worked on 2026-11-22)
1. Export the 5 DBs + meta from a **copy** with `GameDatabaseEdit.Read` (scratch tool: write each to `<dir>\<name>.db`).
2. `dotnet run --project tools/SyncWrite -- data\nhlapi_teams.tsv <dir>\nhl_game-meta.xml nhl_game=<dir>\nhl_game.db
   initial_experience=... entry_draft=... expansion_draft=... threes_campaign=... [--include-prospects missing.json]`
   Outputs go to `%LOCALAPPDATA%\CenterIceModdingSuite\dbwork\<db>\<db>.db` (**shared folder: back it up first** if another
   session may need it). List nhl_game first.
3. Restore stock draft picks (copy `exhibitiondraftpick` values from the input), check in-use counts and blank names.
4. `GameDatabaseEdit.Write` each DB into the copy; boot-test past X; only then main.
- 2026-11-22 result with 332 missing API prospects: ~310 new players per DB, 715 non-org AHL players released, 273 signed,
  44 call-ups, 0 warnings, layout identical, 0 cross-DB disagreements. Staged on `D:\CIMS-test\prospects-copy`.
- Every new row's asset ids were verified to be ids stock NHL 24 already uses (heads, hair, gear, sticks, gloves, audio,
  surname audio, casual gear). New values are only the players' own ids, goalie colours and stat numbers.

---

## 7. Uniform and equipment rows (DB side)

- A selectable kit = `stockteamjerseys` row (team, style, variant = fevariant, isdefault, islightcolor, orderid, locstring)
  in **nhl_game and initial_experience**, plus the texture side (bundles/`player_team_brt` registration, see uniform handoff).
- Default kit per team = rows with isdefault=1 (style 2), by islightcolor; the primary pair has the lowest orderid. Real
  defaults are not always variants 0/1 (Boston 6/7, Vegas 2/1, Philadelphia has two pairs...). `KitDefaults.cs` handles this.
- **Warning (2026-11-22):** the copy built from `D:\stock-restore`, which has the Fanatics-as-default swap applied, crashes
  when X is pressed at the title screen (`0xc0000005`, rcx=0xdeadbeef in `user\log\shad_log.txt`), even with a fresh profile.
  The same build without the swap (`D:\CIMS-test\dbmesh-copy`) boots. Both have identical player tables. The swap is the
  prime suspect; don't apply it to anything played until it's boot-tested.
- locstring: 0 Home, 1 Away, 2 Alternate, 3-5 "2006-07" kits, 6 `VINTAGE_<style>_<teamartid>_<variant>` (per kit name in loc),
  8/9 CHL home/away. 7 has no string.
- `stockteamequipmenttable`: helmet/gloves/pants per team+brand+jerseyartid+equipment+era+variant with zone1-15 colours.
  Gear **models** can't be added (no mesh/material); only colours and existing models' textures change.
- Tools: `JerseyRowPlanner` (new rows in spare slots, both DBs), `EquipmentEditor` / `InstalledEquipmentEditor` (validated
  equipment edits, read-back of every unrelated row), `NewUniformEquipmentPlanner`.

---

## 8. Saved rosters (in-game "Save roster")

`user\home\<id>\savedata\CUSA37933\<name>\` with DATA/INDEX/NAME. INDEX = 80-byte entries (name[32], 8 bytes, size/start/end
u64). Record = u32 (size−16), 12 bytes, 0x01, LZ4 frame (64 KB independent blocks, content checksum), u32 decoded length.
Decoded blob = the T3DB **plus an 18-byte trailer** (`d92eb758 00000000 <db size u32> 00000000 0100`; keep it as is).
Saved rosters hold the exhibition tables. Code: `SavedRoster.cs` (Find, ReadDb, WriteDb with backup, Create RosterCustomN).
Profile0 is the profile save, not a roster. A bad profile was suspected for a crash on 2026-11-22 but ruled out.

---

## 9. Language strings (loc/*.db)

One T3DB table of 16-byte rows (text offset, key hash, key offset, 0) sorted by hash, then a pool with a Huffman tree and
compressed strings (key: 1-byte length, text: 2-byte length, MSB-first Huffman bits). eng_us: 65,498 of 65,535 rows used
(**37 free**, u16 count). Rules learned the hard way (each one crashed the game before the fix):
- New text must sit **before** its key in the pool (`Set` appends text + a fresh key copy, like `Add`).
- **Pad the pool to a multiple of 8 bytes.** Sizes ≡ 3 mod 4 crashed at boot.
- Edit every language and both copies. Code: `LanguageStrings.cs`.

---

## 10. Editing tools in the suite (app: Teams & database page)

- **Roster / Lines / Strategy tabs** (`TeamEditor`): numbers, letters, styles, every line slot, 182 strategy fields, with a
  verify (one holder per slot, 20 dressed, ≤3 letters, one C, unique numbers).
- **Database tab** (`DbEditorPanel`): every table, every in-use record, every int/string field; range-checked from the meta;
  add a record (copy of the selected, into spare slots, right after it) or delete, for tables the team model doesn't cache;
  bulk set; CSV export.
- **Sources:** saved rosters, loose .db files, and (new) **Game database: nhl_game / initial_experience / entry_draft /
  expansion_draft / threes_campaign** read from and written back into the apply target.
- **Extract .db / Import .db:** save the open DB to a file (meta beside it, read back), or load a same-size/same-layout
  file in its place. Save writes it to the open source with backup.
- Save refuses only problems the edit introduced (baseline taken at open).

---

## 11. Other game-data facts that matter for DB work

- Emulator: NHL24PC V3.2 (`nhl24_new.exe` v0.8.3, shadPS4-based). It runs `game-UPDATE\eboot.bin` (1.60) over `game\`.
  The launcher (`tools\launcher.ps1`) patches the eboot at launch (resolution, freeze fix), so the eboot hash changes after a
  first launch: that's normal.
- Logs: `user\log\shad_log.txt` (buffered; may lag minutes), crash dumps `user\log\crash_*.dmp`. First launch of a fresh copy
  sits on the NHL 24 splash for minutes (shader build).
- Mod release 2026-11-15 (`D:\CIMS-test\release\...`): 49 changed game files, `.cimspkg` + installer. `D:\CIMS-test\dbmesh-copy`
  is byte-identical to it on all 49 files and is the known-good base.
- `D:\stock-restore` is the pre-reinstall modded build (with the defaults swap), **not stock**.
- Mesh positions can be edited (`MeshSet`, `MeshSetPositionsApply`, Assets page OBJ export/import): same vertex count, inside
  the section bounds (bounds live in the RES, not rewritten yet). ~10% of meshes can't be read yet.

---

## 12. Copies on disk (2026-11-22 evening)

| Path | What |
|---|---|
| `D:\NHL24PC_V3.2\NHL24PC` | Main game. Another session was applying the player-table repair + defaults swap to it on 11-22; check its state before trusting it. |
| `D:\CIMS-test\dbmesh-copy\NHL24PC` | Known good: release 11-15 content, clean tables, boots past X. Master base. |
| `D:\CIMS-test\prospects-copy\NHL24PC` | dbmesh-copy + prospects sync (above). Awaiting the owner's boot test. Backups in `..\prospect-backups`. |
| `D:\CIMS-test\complete\NHL24PC` | Built from stock-restore: **crashes on X**. Don't use. |
| `D:\CIMS-test\v32-copy2`, `swaptest*` | Other sessions' test copies. |
| `%LOCALAPPDATA%\CenterIceModdingSuite\dbwork` | Shared sync work folder. `dbwork-backup-20261122` = its state before the prospects run. |

---

## 13. Open questions / known gaps

- Why the defaults swap crashes on X (diff stock-restore vs dbmesh-copy DBs: stockteamjerseys isdefault/orderid/locstring).
- Commentary name calls: DB side present for 46 players, audio not verified.
- Saved-roster trailer's first 4 bytes are unknown (kept as is).
- What sets the franchise start season (league-settings +0x6C) for a new franchise.
- Two pre-existing test failures: `T3DbTests.Integers_match_the_dump...` (stale `dbwork\nhl_game\before` dump) and
  `ContractsTests.Nhl27_prefill...` (broke during the roster stream's 11-22 edits).
