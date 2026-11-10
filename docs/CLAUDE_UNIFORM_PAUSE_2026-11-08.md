# New-uniform work paused, 2026-11-08

Paused at the owner's request. No test or live writes after this note. The jersey still shows the placeholder.

## Folders

| What | Path |
|---|---|
| Live game (installs and backups belong to this one) | `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC` |
| Test copy, launch with `nhl24_new.exe` | `D:\CIMS-test\play` (`game` is a junction to `D:\CIMS-test\NHL24-merged`; `game-UPDATE` is a real folder) |
| Test-copy backups | `D:\CIMS-test\backups\` |
| Prototype sources (session Temp, may not last) | `C:\Users\devin\AppData\Local\Temp\claude\c237366b8a8ea80b\9da489b5-306b-4833-bfa9-1155b90ada5e\scratchpad\` |

Prototype folders: `Live` (restore, whole-kit clone, jersey row), `Reg` (master list and gear rows), `Ctl` (four-to-home control), `Ver` (correct list read-back), `memscan3.py` (read-only memory trace, not yet run on the target screen). Nothing here is in the suite yet. `Lib` holds the DLLs they compile against, because the repo was mid-edit by another session.

`C:\Users\devin\Desktop\NHL24PC-CIMS` does not exist on this machine, although older handoffs name it as the playable copy.

## Live game state

Both installs (02:18 and 06:40 on 11-08) are still there: extra Carolina-style rows on every team (usually `twenty`, `twentyone`, `twentyfour`, `twentyfive`) and 2,448 `__cims_` bundles, all showing the placeholder. Nine center ice writes (06:43-07:11) came after them.

**Pending owner decision.** The owner asked to delete the old added slots but has not chosen how:
- **rows**: delete only the added rows from `nhl_game` and `initial_experience`, after a backup. Center ice is kept; unused bundles stay.
- **restore**: restore backup `%LOCALAPPDATA%\CenterIceModdingSuite\backups\20261108-021820`. Removes rows and bundles, and also the 9 center ice writes. The restore itself was rehearsed on the test copy (0 differences).

## Test copy state

Restored from `20261108-021820`, then:
- `carolina/adidas/twentyfour`: full clone of `four` (30 bundles, 120 EBX, 54 RES), own GUIDs, resource IDs, RES name hash at byte 112, manifest `h32`, stock chunk ranges.
- Carolina variants 24 and 25: jersey rows copied from variant 4, plus 4 gear rows each, in `nhl_game` and `initial_experience`.
- `player_team_brt`: `twentyfour` registered; `twentyfive` names are aliases.
- **Control still applied:** `four` names, and the `twentyfive` aliases that pointed at them, resolve to Carolina `home`. It re-pointed 57 assets in the patch version but only 10 in the base version (not investigated). Undo by restoring test backup `D:\CIMS-test\backups\20261108-112745`.

## Findings

- Kit assets are looked up by name in `content/common/configs/bundlereftable/player_team_brt` (RES type `428EC9D4`, 909 bundle copies, 2 contents). `lua/components/uniform.lua` asks for `jersey_{Fit}_{Team}_{Variant}…` and falls back to `jersey_adidas_frontend_home_*`.
- Table layout: header pointers, string pool, A rows (bundles, 24 bytes), B rows (leaf + folder, 16 bytes), C rows (16 bytes: FNV-1 64 of leaf and of `folder/leaf`, bundle index, asset index, sorted by the 64-bit hash), a tail, then a relocation table. RES `Meta` holds data size and relocation size.
- `content/common/configs/mappings/team_variant_mapping` lists `home` through `thirtynine`, so 24 and 25 have words.
- Only `nhl_game` and `initial_experience` have jersey and equipment tables. The new rows match `four` in every column except variant, jersey index, order and the gear table's row index.
- A read-only memory scan of the running test copy found the new `twentyfour` names and bundle names, so the edited list is loaded. Update layer confirmed (base 01.00, running 01.60).
- Result of the last boots: `twentyfour` and the `twentyfive` alias both show the placeholder. The stock `four` entry was reported unchanged despite the control ("it looks the same", "as before", read as the third entry). So the list is in memory but the lookup result is not what the decoded table predicts. Cause unknown.
- Latest lead, not followed up: some stock kits use `_launch` bundle names (`jersey_adidas_vegas_two_launch_player_team_brt`; also Vegas home and away, Florida, Arizona, Minnesota away, 37 for Vegas two). Carolina `four` has none, and the `player_team_brt` copy decoded here has no `launch` entries in its asset rows. `uniform.lua` has no `launch` reference.

## Next steps when resumed

1. Boot the test copy, stop on Carolina's last ALTERNATE, leave it open, run `memscan3.py` (requested names, `NULL` names, and what the in-memory table returns for four, home and the new kit).
2. Follow the `_launch` lead if the trace points to bundle naming.
3. If new kit names cannot work, say so; the fallback is the 8 spare real kits (Boston, Chicago, Detroit, Edmonton, Montreal, NYR, Tampa, Toronto), with replacing alternates for the rest as the owner's call.

## Label

The owner wants "Fanatics 26" instead of "ALTERNATE". The picker text comes from `locstring` on the jersey row. ALTERNATE is 2 and shared, so do not change its text. `gamemodes/service-defs/data/jersey_type/localization.json` allows types 0-7, and 7 is unused by any row, so it is the candidate. Still unknown: which loc key type 7 maps to. The language databases' strings are compressed and the repo has no decoder for them.

## Code

`src/Suite.Convert27/UniformSourceLayouts.cs:51-55` has an uncommitted fix: automatic slot-map links add a sponsor only when the folder has no reviewed rule or the link is manual. It stops the Carolina "no donor provides 7 layers" preflight failure. Not committed because the file is untracked and depends on about 115 other uncommitted files. Release was rebuilt with it on 11-08.

Known defects in the installed new-uniform path, not fixed in the repo: `NewUniformAssetPlanner` names bundles `__cims_`, clones only textures, and can pick earlier clones as donors (`:182`); `BrandingClonePlan` keeps the donor's name hash, resource ID and chunk metadata; the `RegKit` built-in read-back has a search bug (use `Ver`).
