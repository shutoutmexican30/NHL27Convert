# Player portraits from NHL.com headshots

`mods\portraits` replaces player portraits with the NHL's current headshots and gives portraits to players who had
none (players new in the NHL 27 port).

## 1. How the game finds a portrait

- Texture `ui/textures/common/playerportraits/p<artid>`: 512×340 BC3 with alpha (a cut-out). 13,699 exist.
- `artid` and `hasportrait` are in `exhibitionplayerbiotable`. With `hasportrait = 0` the game shows no photo.
- Players new in the NHL 27 port have `artid = game_id`, `hasportrait = 0` and no texture. 5,613 portrait textures
  aren't used by any player in either roster file, so new players are given one of those artids. No existing
  player's portrait is touched.

## 2. The NHL API

- Rosters: `https://api-web.nhle.com/v1/roster/<TEAM>/20262027` (use `curl -L`; `/current` redirects there).
  Each player has `id`, names, `birthDate` and `headshot`.
- Headshots: `https://assets.nhle.com/mugs/nhl/<season>/<TEAM>/<id>.png`, 336×336 cut-outs on transparent
  backgrounds.
- API reference: github.com/Zmalski/NHL-API-Reference

## 3. Steps

```
cd mods\portraits
(fetch rosters\<TEAM>.json for all 32 teams)
python match.py            -> matched.json (780 of 787; the 7 others aren't in the database)
(download each headshot to mugs\<id>.png)
python crop.py build       -> out\<id>.png (512x340)
(assign: players with a portrait keep their artid, others get the next free slot from free_slots.json)
PortraitAssign <db> nhl_game-meta.xml assign.tsv <out db>    (for nhl_game.db and initial_experience.db)
```
The portraits go in the kept list as "Player portraits (NHL.com headshots, 2026-27)". The roster files are the
ones in `mods\roster27`; the copies from before portraits are in `mods\roster27\before_portraits`. **After a
roster rebuild (RosterPort → UshlBuilder), run PortraitAssign again as the last step.**

**Matching** (`match.py`): birthdate plus last name, then first name. Fallback: full name plus birth year,
because some database birthdates have day and month mixed up. The database stores year − 1900 and 0-based month
and day.

**Cropping** (`crop.py`): NHL 24 frames the head larger. From each cut-out's alpha, the script finds the top of the
head and the head's centre and width, measured at the same distance below the top as in NHL 24's portraits. It
scales so the head is 29% of the width, places the top at 10.5% of the height and centres at 49%. Those
numbers come from `crop.py calibrate` on stock portraits; `crop.py sheet` shows stock and new side by side. A check
over all 780 found none outside the expected ranges.

Files Windows writes with CRLF break `xargs` file names (a stray `\r`). Write lists with `newline='\n'`.
