# Team logos

Team logos are textures named by **art id**: `ui/textures/common/teamlogos/<variant>/t<artid>`. A team's `artid`
(in `exhibitionteams`) says which art it uses. There are two cases.

## 1. The 18 variants

Sizes run from 80 to 1024 px; `hutcardbw` is 332×464. They come in these styles: plain fit, mono, glossy,
stylized left/right (the slanted ones) and HUT card. The game stores them as BC3.
`mods\ushl\make_logos.py` has the full table (`VARIANTS`) and builds all 18 from one logo image:
- `fit` centres the logo with a margin,
- `mono` / `glossy` / `stylized` add the effects,
- `hutcard` builds the card layout.

## 2. Replacing an existing team's logo (art id the game has)

Ordinary texture replacement. Make a PNG per variant and add one replacement per slot. The worker sizes and
encodes it. Example: `mods\roster27\make_nhl_logos.py` for Anaheim (t0) and St. Louis (t24) writes
`logos\<abbr>\<variant>.png` and `logo_replacements.json` (36 slots), which went into the kept list as
"NHL logo updates (Anaheim, St. Louis)".

Prepare the source: crop to the alpha bounding box, pad to a square with 6% margin, resize to 1024.

## 3. New art (art id the game doesn't have)

A new team (Utah t2917, USHL t2901–t2916) has no texture to replace. `NHL::CustomLogo` falls back from
`fb://ui/textures/common/teamlogos/<variant>/t<id>` to a **loose file**:
`kettle_assets/teamlogos/<variant>/t<id>.gnf`.

Building the GNFs:
- start from a stock GNF header and patch it per size: word 2 (dimensions), word 4 (pitch, rounded up to 32),
  word 7 (data size) and byte 12 (file size) (`gnf_header()`);
- encode with the worker: `png-gnf <header.gnf> <image.png> <out.gnf>` (BC7);
- output: `mods\ushl\kettle\teamlogos\<variant>\t<id>.gnf`.

`deploy_logos.sh` copies the tree into the candidate folders under the emulator's user data:
`user/download/CUSA37933/temp/nhl/ui/kettle_assets`, `user/download/CUSA37933/nhl/ui/kettle_assets`,
`user/temp/CUSA37933/nhl/ui/kettle_assets` and `user/download/CUSA37933/temp/kettle_assets`. These are loose
files, not part of an apply. **Not yet confirmed in game**: check the shadPS4 log for which path the game opens,
then remove the other copies.

Rule from the user: new teams get their own logos and textures. Never reuse another league's logos, jerseys or any
other texture.

## 4. Team colours

Colours sit in `stockteamcolorstable` (nhl_game.db only). For NHL teams they come from `nhl_colors.tsv` (TruColor's
official colours) through RosterPort (see [ROSTER_PORT_NHL27.md](ROSTER_PORT_NHL27.md)). USHL colours come from
the user's palettes in `UshlBuilder` (primary, secondary, tertiary).
