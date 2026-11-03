# How NHL24Kit edits NHL 24's databases (rosters and text)

This covers how NHL 24 stores its rosters and its text, how the Roster Editor reads and changes them, and how edited databases get written back into the game files.

Applies to NHL 24 PS4 (CUSA37933, v01.00). Code: `C:\dev\NHL24Kit\Worker` (`DbTools.cs`, `LegacyWriter.cs`) and `C:\dev\NHL24Kit\RosterEditor`.

---

## 1. Where the data is

NHL 24 keeps rosters and text as **legacy files**: plain files packed inside the game's archives, outside the normal asset system. FMT lists them through its legacy file manager (`ChunkFileManager2022`), which reads 10,931 of them: databases, Lua, UI layouts, CSVs, and more.

| File | What it holds |
|---|---|
| `gamemodes/database/t3db/nhl_game.db` + `nhl_game-meta.xml` | **Rosters**: 119 tables (players, ratings, teams, lines, equipment, stats, schedules, franchise data...) |
| `loc/eng_us.db` + `eng_us-meta.xml` | **English text** (one table, 65,253 strings) |
| `loc/fre_fr`, `ger_de`, `swe_se`, `fin_fi`, `cze_cz`, `rus_ru` | The other languages, same layout |
| `loc/lng_lg.db` | String-ID language (shows the IDs instead of text) |
| `gamemodes/roster/entry_draft.db`, `expansion_draft.db`, `initial_experience.db`, `threes_campaign.db` | Rosters used by those modes; they open with `nhl_game-meta.xml` |

---

## 2. The database format (t3db)

These are EA's classic sports databases, the same family as FIFA's. A `.db` file holds the data, and the `-meta.xml` names everything.

### The .db file
```
"DB\0\x08"            signature
u32 0
u32 file size
u32 0
u32 table count
u32 CRC of the header
table count × [4-char short name][u32 offset]
u32 CRC of the table list
tables...
```

Each table has a header (record size, bit count, compressed string length, record counts, field count), a CRC, field descriptors (type, bit offset, short name, bit depth), then the records:
- **Records are bit-packed**: each field takes exactly its bit depth. Integers are stored as an offset from the field's minimum.
- **Strings** are fixed-width or **Huffman-compressed** (a tree is stored after the records). Long text such as the localization uses compressed strings, so editing text changes the file size.
- Every section ends with a CRC.

### The meta XML
It maps the 4-letter short names to real names and types:

```xml
<table name="LanguageStrings" shortname="GJCv">
  <fields>
    <field name="hashid" shortname="jKhj" type="DBOFIELDTYPE_INTEGER" depth="32" key="True" ... />
    <field name="stringid" shortname="VhAs" type="DBOFIELDTYPE_STRING" depth="800" />
    <field name="sourcetext" shortname="bYbZ" type="DBOFIELDTYPE_STRING" depth="32000" />
  </fields>
</table>
```

### Reading and writing
FMT ships a reader/writer for this format: `FifaLibrary.DbFile` in `FMT.FifaLibrary2.dll`, from the FIFA editing tools. NHL24Kit uses it with two fixes:
- `DbFile.SaveDb()` closes its stream before computing the CRCs and writes after the loaded data. NHL24Kit instead serialises into a fresh stream with `SaveDb(DbWriter)` and then `ComputeAllCrc(reader, writer)` (`DbTools.Save`).
- The stream handed to `DbFile` must be expandable, because saving can grow it.

### Round-trip results (load, then save unchanged)
| Database | Result |
|---|---|
| All `loc/*.db` | **Byte-for-byte identical** |
| `nhl_game.db` and the `gamemodes/roster/*.db` files | Same size, **every value identical** (checked by dumping all 119 tables before and after). The library rewrites empty record slots and "cancelled record" counters, and moves 2 all-zero placeholder rows to the end of their tables. |

---

## 3. Rosters (`nhl_game.db`)

Most roster tables come in three families:

| Prefix | Used for |
|---|---|
| `exhibition…` | The stock rosters: Play Now, and new seasons and franchises start from these. **Edit these to change the default rosters.** |
| `custom…` | Custom rosters |
| `league…` | An active franchise or season (also kept in saves) |

### Main tables

| Table | Rows | What |
|---|---|---|
| `exhibitionplayerbiotable` | 8,135 used (holds 13,300) | Player bios: names, team, position, jersey, height, weight, birth date and place, contract, appearance, career stats |
| `exhibitionskaterai` | 7,271 players | Skater ratings (103 fields) |
| `exhibitiongoalieai` | 865 players | Goalie ratings (50 fields) |
| `exhibitionplayers` | | Maps `index` to `playerid` |
| `exhibitionrostertable` | 19,030 | Roster membership and lines |
| `exhibitionteams` | 577 | Teams: names, abbreviations, colours, arena, mascot, rivals, goalie masks... |
| `exhibitionskaterequipment`, `exhibitiongoalieequipment` | | Equipment |
| `playernames` | 16,182 | Name bank with commentary audio IDs (so the announcer can say a name) |

### How the tables link

- **Player ID** = `game_id` in the bio, skater ratings and goalie ratings tables.
- **Team** = the bio's `team` field, which is the `index` of a row in `exhibitionteams`. `-1` means no team.
- **Lines and roster membership** live in `exhibitionrostertable`:
  - `playerindex` is **not** the player ID. It is an `index` in `exhibitionplayers`, whose `playerid` is the `game_id`.
  - Each row holds a `team`, the line slots (`l1c`, `l2lw`, `pp_1c`, `pk4_1ld`, `ev3_…`, `three_…`, `g1`, `s1`...), `rosterstatus`, `captain` and a team-specific `jersey`.
  - Team numbers in this table include extra rosters. For example, team 11 is the Oilers' 22-man roster, and team 678 is a 7-player Oilers roster.

### Value codes

| Field | Encoding |
|---|---|
| `position` | 0 C, 1 LW, 2 RW, 3 D, 4 G |
| `positionprimary` | 0 C, 1 LW, 2 RW, 3 LD, 4 RD, 5 G |
| `height` | inches minus 54 (19 = 6'1", 24 = 6'6") |
| `year`, `draftyear`, `yearprodebut`, ... | years since 1900 (85 = 1985) |
| `weight` | game units; no single formula fits, so it is left raw |
| `skill_overall` | the **overall rating the game shows** (McDavid 97). `c_overall` is mostly a filler value. |
| Ratings `c_…` | individual attributes (speed, wrist shot accuracy, faceoffs...) |

### Moving a player to another team
Changing the bio `team` alone is not enough. The Roster Editor also moves the player's lines row for the old team to the new team, and clears its line slots and captaincy so they don't clash with the new team's existing lines. Set lines in the game afterwards, for example with auto lines.

---

## 4. Text (`loc/*.db`)

One table, `LanguageStrings`:

| Field | What |
|---|---|
| `stringid` | The name the game asks for, e.g. `TXT_PLAYNOW_MODE_TITLE` |
| `hashid` | A hash of the ID (the key) |
| `sourcetext` | The text shown. It can hold placeholders such as `[<%1 tnn>]` (team name) or `%1s`; keep those. |

The same text can appear under several IDs: "Play Now" is used four times (menu, first boot, mode title, submenu). Change each ID you want changed.

Tested in game: changing those four to "Play Now (Kit)" showed "PLAY NOW (KIT)" on the main menu.

---

## 5. Writing a database back into the game

Legacy files are not stored one per chunk. A **data chunk** holds several files back to back, compressed together, and an **index chunk** (the chunk-file collector's chunk, `globals/chunkfilecollector`) lists every file.

| Database | Shares its data chunk with |
|---|---|
| `nhl_game.db` | 4 other files (the meta XML, a sim data file, 2 CSVs) |
| `loc/eng_us.db` | 281 other files (mostly UI scripts) |

### Index entries
The index chunk starts with `[u32][i64][i32 file count][i64 file table offset]...`. Each file is a 56-byte entry, little endian:

```
[u64 name offset][i64 compressed start][i64 compressed end][i64 offset][i64 size][16-byte chunk GUID]
```

- `offset`/`size` locate the file in the **uncompressed** data chunk.
- `compressed start`/`end` cover the compressed blocks holding it: from the start of the block with its first byte to the end of the block with its last byte. Several files can share a block.

FMT merges entries with the same name, so NHL24Kit reads the raw table itself and patches every entry.

### Steps (`LegacyWriter.Apply`)
1. Decompress the whole data chunk.
2. Check that the block rule reproduces every existing entry's compressed range. If not, refuse rather than guess.
3. Splice in the edited file. Every file after it shifts if the size changed.
4. Recompress with Kraken: the original block splits when the size is unchanged, otherwise the original block size.
5. Recompute every file's offsets and compressed range in that chunk.
6. Write the data chunk like a texture chunk: append it, repoint every TOC and bundle reference, and queue SHA1 updates. If the uncompressed size changed, also patch the chunk's size in bundle records.
7. Patch the index chunk's entries, recompress, and write it the same way.

Both chunks go through the same writer as textures, so Restore undoes database edits too. Database files share index chunks, so when one changes an apply writes all of them again together (see [APPLYING.md](APPLYING.md)).

---

## 6. Tools

### Roster Editor (`NHL24RosterEditor.exe`)
- **Players:** search by name, team and position; edit bio, ratings and lines rows.
- **Database:** any table in any of the 13 databases, in an editable grid; add rows (copied from the selected row) or delete them, up to each table's limit.
- **Text:** search any language by text or string ID and edit it.
- **Save edits** writes the edited databases to `%APPDATA%\NHL24Kit\dbmods\<game>\` and puts them in the shared mod list as "Roster & text edits". **Apply to game**, in either program, writes the whole mod list. **Revert database** throws one database's edits away.

Stock copies are exported once to `%LOCALAPPDATA%\NHL24Kit\db\stock\<game>\`.

### Worker commands
```
NHL24Kit.Worker.exe legacy        <game>                       list every legacy file
NHL24Kit.Worker.exe legacy-info   <game> <name>                where a file lives, and its chunk neighbours
NHL24Kit.Worker.exe export-legacy <game> <out dir> <name>...   save legacy files (stock data)
NHL24Kit.Worker.exe db-dump       <db> <meta.xml> <out dir>    every table as CSV
NHL24Kit.Worker.exe db-import     <db> <meta.xml> <csv dir> <out db>   replace tables from edited CSVs
NHL24Kit.Worker.exe db-roundtrip  <db> <meta.xml>              load and save unchanged; report differences
```

To write an edited database, add it to a plan's `legacy` list and run `apply`:

```json
{ "replacements": [], "legacy": [ { "name": "loc/eng_us.db", "source": "C:\\mods\\eng_us.db" } ] }
```

---

## 7. Cautions

- **Table sizes are fixed.** A table can't grow past its stored maximum (`NMaxRecords`); the editor refuses to save more rows.
- **Keep keys unique.** Copying a row copies its key fields (`game_id`, `index`, `hashid`); change them before saving.
- **Saves can override rosters.** A franchise or season uses the `league…` tables in your save, not the stock rosters.
- **Keep text placeholders** (`[<%1 tnn>]`, `%1s`, ...) or the game may show garbage.
- **Names and commentary:** a new player name won't be spoken unless it matches an entry in `playernames` (its `audioid`).
