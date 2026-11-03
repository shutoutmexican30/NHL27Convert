# Porting NHL 27 rosters to NHL 24

`mods\roster27\RosterPort` brings NHL 27's people into NHL 24's roster databases: players, bios, ratings,
contracts, rosters, X-Factors, plus Utah, team colours and gear colours. It moves people, not art: NHL 24 keeps its
team rows, strategies, arenas, faces it already has, and equipment art.

The rules in [DATABASE_EDITING.md](DATABASE_EDITING.md) and [custom-leagues.md](custom-leagues.md) apply. The
most important ones: edit **both** roster files, roster keys are `team * 40 + slot`, keep every table sorted.

## 1. Inputs and build chain

- NHL 27 database: `Downloads\NHL 27 Roster` (db and meta XML).
- NHL 24: `gamemodes/database/t3db/nhl_game.db` and `gamemodes/roster/initial_experience.db` (Play Now reads the
  second one too), each with its meta XML.
- `nhl_colors.tsv`: official team colours (from TruColor), 3 per team. Arizona's slot holds Utah's colours.

```
cd mods\roster27
RosterPort <24 nhl_game.db>  <meta> <27 db> <27 meta> ported_nhl_game.db           nhl_game.db nhl_colors.tsv
RosterPort <24 initial_exp>  <meta> <27 db> <27 meta> ported_initial_experience.db  nhl_game.db nhl_colors.tsv
UshlBuilder ported_nhl_game.db           -> nhl_game.db             (adds the USHL on top)
UshlBuilder ported_initial_experience.db -> initial_experience.db
cat ushl_strings.tsv utah_strings.tsv > all_strings.tsv
LocAdd <stock eng_us.db> all_strings.tsv -> eng_us.db              (team names)
```
The three output files go in the kept list as "NHL 27 rosters + USHL".

## 2. What RosterPort does, in order

1. **Teams** are matched by identity: `artabbr`, then name. Utah Mammoth goes into Arizona's slot (22). A club that
   relocated falls back to the NHL 24 team in the same slot, but only within club leagues (0–9, 11–13, 15). In
   NHL 27 league 27 is the PWHL, in NHL 24 it's women's national teams, so slot matching across leagues pairs the
   wrong teams. Teams new in NHL 27 are skipped.
2. **Players** in both games (same game id and last name) keep their NHL 24 face, portrait, commentary audio and
   equipment. Their bio, contract and ratings come from NHL 27. Players new in NHL 27 are added, and their asset
   references are mapped:
   - generic heads: NHL 27 `1000xx` = NHL 24 `600xx`; real faces share ids;
   - unknown faces get an NHL 24 generic head of the same skin colour;
   - unknown equipment and traits fall back to valid NHL 24 values;
   - commentary surnames come from NHL 24's `playernames` list.
   - New player ids stay below 40000. USHL players use 40000+, and 56818–59999 belongs to custom players: reusing
     those crashed Play Now.
3. **X-Factors** (section 3).
4. **Rosters** of matched teams are replaced (numbers, lines, captains), keyed `team * 40 + slot`.
5. **Free agents** come over too.
6. **Utah:** the Arizona slot gets team name, city, nickname and abbreviation UTA, `artid` 2917 (new logo art,
   see [LOGOS.md](LOGOS.md)) and arena "Delta Center", Salt Lake City, UT. The display names come from loc
   strings (`utah_strings.tsv`: Utah Mammoth / Utah / Mammoth / UTA and the matchup line), keyed by artabbr.
7. **Equipment colours** where NHL 27 has the exact same key.
8. **Home/away gear colours** (section 4).
9. **Team colours** from `nhl_colors.tsv`: each colour field is set to the nearest official colour of its family
   (presentation, front end, overlay), using NHL 27's row as the guide.
10. Sort every table and check for dangling references. When writing back, restore every table's max and valid
    record counts.

**Years** are stored absolute (year − 1900) and stay real: birth, draft, debut, cup and UFA years. Only
`yearwaivereligible` follows the current season (NHL 24 writes 122, NHL 27 126), so it shifts by 4. Contract
lengths are years remaining and stay as they are.

## 3. X-Factors

NHL 27 has 3 slots (ability 0–30 plus tier 1–3). NHL 24 has one zone ability (`xza`) and up to five superstar
abilities (`xsa1-5`), with different ids. The map was built by co-occurrence on players in both games (`?` =
weakest evidence):

| NHL 27 | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 11 | 12 | 13 | 15 | 16 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| NHL 24 | 6 | 5 | 45? | 15 | 20 | 14 | 16 | 24 | 26 | 27 | 2 | 3 | 4 | 33 | 39 |

| NHL 27 | 17 | 18 | 19 | 21 | 22 | 23 | 24 | 25 | 26 | 27 | 28 | 29 | 30 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| NHL 24 | 44 | 35 | 34 | 49? | 36 | 8 | 38 | 40 | 202 | 200? | 201 | 220 | 211? |

A player gets a zone ability with two tier-3 abilities, or with one if NHL 24 already gave them a zone. The top
ability becomes the zone and the rest become superstar abilities. Players keep their other NHL 24 superstar
abilities in the free slots.

## 4. Gear colours follow the jersey variant

`stockteamequipmenttable` (zones 1–15: helmet, gloves, pants and so on) is keyed by team, equipment, **variant**,
jersey art id and era. The variant ties equipment to a jersey. NHL 27 moved every NHL team's default home and away
to new variants (most 10/11, St. Louis 14/15, Utah 12/13), while NHL 24 uses mostly 0/1. So copying rows by exact
key never reaches NHL 24's home and away, and the gear stayed NHL 24's.

The home/away step: for each NHL team and for light 0/1, find NHL 27's default variant and NHL 24's default
variant (`stockteamjerseys`: `isdefault = 1`, `islightcolor`), then copy the zones of every NHL 27 equipment row
on that variant onto NHL 24's row for the same equipment and era. Missing rows are added (cloned, with team,
variant and jersey art id changed). Afterwards the table is sorted by team and its index renumbered 0..n−1.
Result: 51 rows updated, 11 added. Confirmed in game (St. Louis).

Side effect: every team gets NHL 27's home/away gear colours, including teams still wearing NHL 24 jerseys.

## 5. Checks

- After a build, verify a few rows by hand (for example St. Louis variant 0 helmet = `0072CE 202020 FFB60F`).
- Load Play Now and open lines for a ported team: wrong roster keys crash lines view (null TeamJerseys).
- Crash addresses: subtract the eboot load base from the address in the shadPS4 log (see [EMULATOR.md](EMULATOR.md)).
