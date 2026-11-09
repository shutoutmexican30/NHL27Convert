# New uniforms in NHL 24: what was done and where it stands (2026-11-09)

The goal was to add NHL 27's Fanatics uniforms to NHL 24 as new selectable jerseys, without replacing any stock kit. Until today, every attempt showed a black NHL-shield placeholder. **Detroit's two new kits now load in game on the test copy.**

## Current state

| Item | State |
|---|---|
| Detroit 24/25 on the test copy | Confirmed working in game by the owner, before the name and sponsor fixes were added |
| All 64 kits on the test copy | Installing when this was written; needs a read-back and one boot |
| Jersey name "Fanatics 26-27" | Built and in the batch; not yet seen in game |
| Sponsor patch in its proper slot | Built and in the batch; not yet seen in game |
| Live game (`NHL24PC_V3.2\NHL24PC`) | Not updated. It still has the older Detroit-only install, which shows the placeholder |
| App uniform buttons and tools | Updated and committed; 171 tests pass |

## Why the kits never loaded

Each of these was enough to stop a kit working.

1. **One table copy was never updated.** Kit names are looked up in `player_team_brt`. There are 911 copies, and `BundleIndex` skipped the two in `levels/game/game.toc` because that file sits deeper than other TOCs. The game loaded that stock copy, so the new names were not found and it fell back to `jersey_adidas_frontend_home`.
2. **Table rows kept the old folder.** Bundle rows are either a full path or a bare name with the folder in a second column. Clones copied that column unchanged, so presets, branding arrays and stamps pointed at the source kit.
3. **Material presets crashed on load.** Lengthening an asset's name moved the text stored after it, without fixing the 60 pointers to that text across the four `_hq_asp` files.
4. **Resource IDs crashed the loader.** The loader treats an even value as an already-resolved pointer. Every stock ID is odd; about half of ours were even.

## Two more problems found once kits loaded

- **Blank jersey name.** Jerseys with `locstring` 6 read a per-kit language key, `VINTAGE_<style>_<art>_<variant>`. New variants had no such key. The language file's Huffman text and CRC-32 key hash are now decoded, so entries can be added.
- **Missing sponsor.** A logo's slot decides where it sits on the jersey. Filling slots in order put Detroit's Priority patch in the back-hem tag's slot. A converted logo that matches a different stock slot now goes to that slot, and the slot it left keeps its stock logo.

## What changed in the code

| Area | Change |
|---|---|
| `Suite.Convert27\FullKitInstall.cs` | The install path: whole-kit clone from the same team, converted art, registration, jersey and equipment rows, jersey names, one backup, full read-back, automatic rollback |
| `Suite.Core\Frostbite\PlayerTeamRegistry.cs` | Reads and extends `player_team_brt`, rewriting both row forms |
| `Suite.Core\Frostbite\BundleIndex.cs` | Finds the game folder for TOCs at any depth |
| `Suite.Core\Frostbite\EbxRiff.cs` | Keeps pointers valid when an asset name changes length |
| `Suite.Rosters\LanguageStrings.cs` | Reads and adds strings in the language databases |
| `Suite.Convert27\NewUniformApply.cs` | The app's uniform buttons now use `FullKitInstall` |
| Uniform page | New "Jersey name" box, default "Fanatics 26-27" |
| `tools\KitInstall` | Command-line install. Refuses the live game without `--allow-live`, and needs `--cut-extra-layers` to drop art layers |
| Older donor-based install | Retired from the app; its live diagnostics now refuse to run |

Commits are on `convert27-slice`, from `1ae37da` to `fe4f90e`. The branch does not build alone: the repo still has about 115 other uncommitted files that this work depends on. Commit `59cb1d9` also includes earlier uncommitted work in `Convert27View.xaml` and `SuiteSettings.cs`.

## Folders and restore points

- **Live game:** `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC`
- **Test copy:** `D:\CIMS-test\play`, launched with `nhl24_new.exe`
- **Clean test restore point:** `D:\CIMS-test\backups\20261108-111146`
- **Clean live restore point:** `C:\dev\NHL27Convert\Debugging\live-detroit-clean-backup`. It has the same TOCs and CAS lengths as backups `20261108-021820` and `20261108-202820`.

## The 64 kits

All 32 teams, with home going to variant 25 and away to variant 24.

Two teams need more logo layers than the stock slot holds. The owner chose to install them with the extra layer left out, so check these in game:

- **Arizona/Utah pants:** the second pants logo layer is dropped.
- **Nashville jersey:** one layer is dropped in the 5-layer copy of the array.

To run the install again:

```
tools\KitInstall\bin\Release\net8.0-windows\KitInstall.exe --target "D:\CIMS-test\play" --mod "C:\dev\NHL27Convert\Debugging\installed-fanatics-mod.json" --restore-first "D:\CIMS-test\backups\20261108-111146" --backups "D:\CIMS-test\backups" --cut-extra-layers --apply
```

Leave out `--apply` for a dry run. Rebuild the tool first.

## Next steps, in order

1. Finish the 64-kit install and read-back, then boot the test copy and spot-check several teams, Arizona, Nashville, the jersey name and the sponsor patch.
2. After the owner confirms, apply to the live game with a fresh backup and read it back. It must start from the clean live restore point, because the install refuses a target where the kit folders already exist.
3. Release the test copy to the goalie thread, which owns goalie colours, masks and Utah.
4. Build the "Sponsor patch" control: keep, use stock, replace with a PNG, or clear, for any uniform.
5. Add NHL 27 uniforms that NHL 24 doesn't have.

## Limits to plan around

- **Language table:** at most 65,535 rows. It had 65,434, and the 64 kits use 64, so 37 are left unless older entries are reused.
- **Variant words:** the game's list stops at `thirtynine`.
- **CAS files:** each must stay under 4 GB. The install checks this as it writes and rolls back if it would be exceeded.
- **Languages:** the jersey name is added to English only.
