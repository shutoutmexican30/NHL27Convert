# Blueline Studio: what it has and what we take (2026-11-23)

Blueline Studio 0.12.1 (github.com/SonkuModz/Blueline-Studio, MIT, .NET 10 / WPF) is a modding suite for **NHL 19 on PS4**.
Source cloned to `reference\blueline-0.12.1` (the `blueline-src` folder is the older 0.11.0). Studied: README, USER_GUIDE, CHANGELOG,
ARCHITECTURE, ROSTER_EDITOR, STATUS, the roster/gear/jersey code and the UI theme. Its code is MIT, so ideas and small pieces
can be reused with credit; its game formats are not ours.

## 1. What it is made of

| Area | Blueline | Our NHL 24 equivalent |
|---|---|---|
| Game files | `.big` (EB) archives, `databigs` overlay, `patch_*_99_*` mods | Frostbite TOC + CAS bundles, `ChunkReplace`, `LegacyFiles`, backup/restore |
| Textures | GNF + Thin1D tiling, DDS/PNG, `BatchTextureImport` | PS4-tiled BC textures, `TextureSlots`, Ps4Codec, Bc3/Bc5 |
| Database | T3DB (`TdbFile`), CRCs, CSV, **same short names** (ubPc bio, dSvy skater ai, ...) | `T3Db` (identical format and CRCs) |
| Roster editor | `RosterBook` + `RosterModule`: every player field, grouped, sliders, lists, swatches, line slots, move, bulk edit | `TeamEditor` + `DbEditorPanel` (raw), no field names, no choices |
| Gear colours | `GearColours`: a row of the equipment table = main colour + 15 zones, team-colour chips, zone-on-model highlight | Equipment Studio: 15 hex boxes |
| Jersey tools | Uniform slots (add a kit), jersey maps (normal/coeff from a colour sheet), refit, font maps, 3D uniform view | Full-kit install, Convert 27, per-kit zone colours |
| Models | `.mcd` viewer, OBJ export | `MeshSet` + OBJ export/import |
| Overlays/audio/video | Scaleform `.gfx`, EA Opus, VP6 | Scorebug page (different format), Sounds |
| Mods | Project folder (content-addressed), build, install, uninstall, `.blpack` packs, undo/redo | Backup folder + restore, `.cimspkg` + installer |

## 2. Not portable (different game formats). Do not touch our writers for these

`.big` archives and the install/uninstall model, GNF/GnmTiling, `.mcd`, EA Opus, `.gfx`/ABC/SWF overlays, VP6/AVI, the
`patch_*_99_*` build and `ModInstaller`. Our tested bundle/TOC/CAS writers stay as they are. Nothing from these folders is copied.

## 3. Compatible, and worth taking

1. **T3DB field knowledge.** `docs/ROSTER_EDITOR.md` documents every player and roster field. Its encodings match what we found in
   NHL 24: ranges below 0 are stored shifted, years are year - 1900, birth month and day count from 0, height = inches - 54,
   weight = pounds - 120, money in $5,000 steps, colours are RRGGBB. The line-slot list (l1-l4, pp, pk, ot, three, s1-s5, g1/g2, x1/x2)
   and "a slot is a flag on the player's row" are the same in NHL 24. (Its "years shifted by 8" is specific to the Overtime roster.)
2. **Named choices come from the game's own CSVs.** `RosterChoices` reads `creation_zone_csv`. **NHL 24 has the same folder**
   (`gamemodes/creation_zone_csv/*.csv`, found through `LegacyFiles`): origins, hair styles and colours, eye colours, helmet,
   glove, stick, skate and visor styles, traits (`playertraits.csv`), celebrations, stances, goal horns, and `colorswatches.csv`
   (243 game colours). So every dropdown can show real names.
3. **The field catalogue idea.** A table of field -> group, label, kind (text, number, rating, flag, choice, colour, year, height,
   weight, money, team), hint. Groups: Name and number, Birth and body, Club, Contract, Draft and career, Ratings, Potential
   and style, Traits, Tendencies, Appearance, Gear, Gear colours, Teams and lines, Personality. Fields not in the table fall back to a
   guess by name (`trait*`, `*color`, `personality*`, `nhl*`), so nothing is hidden.
4. **UX patterns:** collapsible field groups, "Find a field", sliders for 0-99 ratings, hex box + swatch for colours, bulk edit
   (ratings up/down, set one field for all, move all to a club), one dropdown per line slot with swap, pending edits kept as a list
   and saved as one undo step, warnings when a row is shared by several kits.
5. **Move semantics:** a move changes the membership's `team`, clears its slots and captaincy, sets bio team/proteam; matching is by
   `game_id` + team, never by row or key. (We already follow the same rule.)
6. **Gear rows:** a kit's glove/helmet/pants colours are one equipment row (main + 15 zones). If the kit has no row of its own it
   shares a fallback row, so an edit changes other kits too; "give this kit its own row" reuses an unused row or a spare slot.
   Same table as our `stockteamequipmenttable`.
7. **Colour UX:** team colours one click away, a zone can be found on the model by clicking it. We have team colours in the
   DB; zone-on-model needs a model + UV map (later).
8. **Texture tab ideas:** channel/mip/face preview, exact export, batch import that matches a folder of images to textures by name,
   per-change revert. Our `TexturesPage` can take the preview options and batch matching; the writer stays ours.
9. **Mod packs:** one file carries a project to another person's game (only changed files). Our `.cimspkg` already does this.

## 4. Done now

- **Readable tables everywhere.** Three bugs in the shared theme made every grid unreadable (white rows under near-white text,
  an item's type name printed over every row, and dropdowns showing a class name instead of the build name). Fixed in `App.xaml`;
  checked by rendering the controls off screen.
- **Colour picker** (`src/Suite.Convert27/Controls/ColorPicker.cs`): `ColorField` (swatch + hex box, click opens the picker, `Hex`
  binds two ways) and `ColorPickerPanel` (saturation/value square, hue bar, hex and R/G/B boxes, was/now preview, team colours,
  colours used lately, the game's own 243 swatches loaded from `colorswatches.csv` at startup). First use: every zone colour of the
  Equipment Studio. Any page can drop it in where it has a hex box.

## 5. Next: one "Database studio" page (proposal)

Merge Contracts, Teams & roster editor and the raw Database tab into one page with a shared model:

- **Players:** list with filters (team, league, position, free agents, search); one player in field groups with real names,
  sliders, dropdowns from the game CSVs, colour fields with the picker; contract fields inline (replaces most of Contracts);
  bulk edit; lines per team.
- **Teams:** `exhibitionteams` fields (names, colours with the picker, arena, AHL affiliate, goalie masks), strategies, jerseys
  and equipment rows for the team (kit list, "give its own row", colour zones with the picker).
- **Tables:** the existing raw editor for everything else (CSV export, bulk set, add/delete), plus friendly column headers and a
  colour cell for any 24-bit colour field.
- **Safety:** same rules as today (game closed, backup, read back, restore on failure; all five databases kept identical and
  under the record cap; Verify warns before a save).

Jersey customizer (after the above): per kit, pick the team's kit, edit its glove/helmet/pants zones with the picker and see a
flat preview from the kit's own textures tinted by the zones; "add a kit" stays in Convert 27.
