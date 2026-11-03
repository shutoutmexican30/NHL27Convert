# handoff for Claude — NHL 27 → NHL 24 conversion suite, state at 2026-11-02

Read this whole file before touching anything. It records what is **verified**, what is **broken and why**, the
**rules that were learned the hard way**, and the **next task with exact parameters**. `docs/WORKQUEUE.md` is the
long-form tracker; this file is the short, safe entry point.

Repo: `C:\dev\NHL27Convert` (branch `convert27-slice`, clean tree, all work committed).
Owner: Devin. The build is .NET 8, WPF app + xUnit tests (**43 suite + 16 roster tests**).

---

## 1. What the project does

Convert NHL 27 (PS5) uniforms, goalie gear and player faces onto the NHL 24 (PS4) game, which is played through a
shadPS4 fork. The suite (WPF, `src/Suite.*`) matches NHL 27 texture sets onto NHL 24 slots, the viewer previews them in
three.js, and writers put the resulting textures into the game's CAS archives.

There is also a separate **Worker** (`NHL24Kit.Worker.exe`, a prebuilt harness binary) that the app historically used to
apply mods. **Its texture path is broken — see §4. Do not "fix" the app by calling the Worker for textures.**

---

## 2. What is verified and working (do not re-do)

- **PS4 tiled texture codecs** — `src/Suite.Core/Frostbite/Bc5.cs`, `Bc1.cs`. Our decode matches FMT's own decode of the
  same live chunks on **100 % of pixels, max difference 1**. BC1 = 8-byte blocks, BC3/BC5 = 16-byte blocks, 8×8-block
  Morton micro-tiles, mips padded to whole tiles.
- **Texture resource header (`Ps4Texture.cs`)** — 132 bytes: `width u16 @22`, `height u16 @24`, `mipCount u8 @30`,
  `GUID @32..47`, fifteen mip sizes `u32 @48`, `payloadSize u32 @108`, and a name at `@116` **truncated to 15 chars**
  (so it cannot be used as a lookup key). Payload = sum of the mip sizes.
- **Writer** — `src/Suite.Core/Frostbite/TocChunkWriter.cs`. Appends stored blocks to the CAS the location record names,
  then repoints every TOC entry that pointed at the replaced copy. Verified on a real game copy: read-back identical,
  restore 0 differences, re-apply verified.
- **The suite's own writer on a 13.7 MB legacy database** — exported, written back, read back identical,
  restore 0 differences with CAS tails identical. No elevation needed.
- **A real texture write** — NHL 27 Boston colour built into the NHL 24 jersey-colour slot:
  `length identical to template 2,798,592`, applied, `2,798,592 raw expected`, read back identical, restore 0 differences.
  **The mechanics work. The content choice was wrong — see §3.**
- **Matcher features** — slot labels, Adidas→Fanatics always, phantom array layers clamped to the array's real slice
  count, partial stock dumps rejected, mod-list source validation.
- **Analysis** — goalie gear census (1,572 NHL 24 models / 1,749 NHL 27 / 1,453 shared / 296 NHL 27-only; the DB id space
  is closed: 24 brand values, 14 equipment values in both games); face section table (head 6,014 triangles, eyes 320,
  lashes 84 — identical in both games); per-section UV overlap; head destination slot audit (475 heads used by exactly
  one player, generics back up to 300, `60000` backs 2,948 custom rows); logo inventory with per-team crest detection.

---

## 3. THE RULE THAT COST A BROKEN JERSEY — which maps may cross games

**Never transfer a map whose pixels are addressed by the garment mesh UVs.** NHL 24 and NHL 27 unwrap jerseys
differently (NHL 27's sleeve stripes are canted, its shoulder yoke wavy), so copying a sheet across games puts the
stripes in the wrong places. This was done once, looked wrong in game, and was reverted.

| Map | Cross games? | Why |
|---|---|---|
| `jersey_*_color`, `_normal`, `_coeff` (also pants/socks) | **NO** | bound to that game's garment unwrap |
| branding **array** layers (crest, shield, brand mark, back-hem tag, sponsor) | **YES** | the game composites them; not mesh-addressed |
| helmet logos, other array-backed sheets | **YES** | same reason |
| **stamps** drawn into the destination's own stock map | **YES** | layout preserved by construction |
| **head / face** textures | **YES** | head UVs are identical in both games: 3,146 of 3,146 shared |

Heads are the exception that proves the rule — that result must not be generalised to garments. It was, and it produced
the broken jersey.

**Corollary for the fabric task:** you cannot fix the fabric by copying a normal map. You must keep NHL 24's own normal
(layout) and add only the Fanatics **grain**.

---

## 4. What is broken, with proof

**The Worker cannot parse any texture.** `NHL24Kit.Worker.exe apply <game> <plan.json>` — even with an identity texture
replacement — restores the CAS and then dies before writing:

```
ERROR Value cannot be null. (Parameter 'enumType')
   at System.Enum.Parse(Type enumType, String value, Boolean ignoreCase)
   at FrostySdk.Resources.Texture.get_PixelFormat()
   at NHL24Kit.Program.Apply(...)
```

Cause: the Worker's `FMT.Resources.dll` is a **0.0 MB stub** (the real one is 11.5 MB), so `Texture.PixelFormat` has no
nested type. **Every** runtime on disk carries the stub — all ten under `%LOCALAPPDATA%\NHL24Kit\runtimes\`, plus
`runtime\` and an old `worker-fresh`. Roster DBs, portraits and bundle chunks still work through the Worker because none
of them parse a `Texture` — which is why the failure looked folder-specific at first and wasted time.

Grafting the real assembly **does not work**: the 11.5 MB `FMT.Resources.dll` gets past the texture class but then fails
on `Unable to find Class EBXSharedTypeDescriptorV2` (assembly set is version-locked), and swapping the whole FMT stack
fails because **FMT 2026.3 is a .NET 10 build** while the Worker is .NET 8
(`Could not load System.Runtime, Version=10.0.0.0`). An experimental runtime copy was made and **deleted again**;
`settings.WorkerRuntimeDir` was never changed. A real fix needs the Worker rebuilt against a matching FMT.

**FMT itself requires Administrator.** `tools/MeshExport --texture-resource` is the only path that yields a texture's
`.res` header, and FMT refuses to open the game folder with `"restart the Application as Administrator"`. This is **not**
an ACL problem: a non-elevated write+delete inside the read view succeeds. Approval prompts are disabled in the agent
session, so elevation must be done by the human.

**Resources cannot be found by name.** `.res` payloads live in CAS files, but the name field is truncated to 15 chars, so
`skater_jersey_d` matches every jersey. Searching all three Patch TOCs for a known header's bytes returns **zero hits** —
the name→resource mapping goes through EBX. An EBX reader is the keystone that would unblock the `.res` lookup, the LT
tuning values and the licensed-head `id → asset` map.

---

## 5. Paths and environment (do not guess these)

| Thing | Path |
|---|---|
| Source repo | `C:\dev\NHL27Convert` |
| Test copy (all writes go here) | `D:\CIMS-test\NHL24-merged` |
| Playable copy (**never write without a verified test run**) | `C:\Users\devin\Desktop\NHL24PC-CIMS\game` |
| Original package | `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC` |
| Emulator | `nhl24_new.exe` (v0.8.3, shadPS4 fork) |
| Test-copy launcher | `D:\CIMS-test\play\nhl24_new.exe` (`game` = junction to the test copy) |
| Desktop shortcut | `NHL 24 - TEST COPY (Boston check).lnk` |
| Settings | `%APPDATA%\CenterIceModdingSuite\settings.json` |
| Read view (for FMT) | `C:\Users\devin\AppData\Local\CenterIceModdingSuite\nhl24view` |
| Decoded NHL 27 PNGs | `%LOCALAPPDATA%\CenterIceModdingSuite\nhl27png\` |
| Backups | `%LOCALAPPDATA%\CenterIceModdingSuite\backups\<timestamp>\` |
| Work artifacts | `C:\Users\devin\Documents\Codex\2026-11-02\x20-ar\work\` and `...\outputs\` |
| Python (bundled, has numpy/Pillow) | `C:\Users\devin\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe` |

Emulator fact: it loads whatever is in a `game` folder beside the exe; the launcher passes **no** game argument.

**Current test-copy state:** stock. TOCs were restored from `backups\20261102-180222` and all 17 hashes match the
pre-write snapshot. `Data\Gen4b\superbundlelayout\nhl_installpackage_05\cas_05.cas` still has **63,802 unreferenced
trailing bytes** (the emulator held the file open during restore); no TOC entry points at them, so behaviour is stock.
Trim with `SetLength(1074648234)` once the emulator is closed.

---

## 6. Tooling you will use

```
tools\TextureChunk\TextureChunk.exe
  build <res> <templateChunk> <png> <outChunk>       # res = layout, template = live full chunk for the size check
  png   <res> <chunk> <out.png>
  verify <res> <chunk> [refPng]
  apply <game> <res> <newChunk> [--expect <liveChunk>] [--restore-check]   # refuses read view / running game
  res   <game> <texture name> <out.res>              # NOT WORKING - see §4
```

Rebuild the tool after library changes: `dotnet build tools\TextureChunk` (otherwise `--no-build` runs a stale
`Suite.Core.dll`).

Decoding an NHL 27 texture needs only its index location — write a job JSON
(`name, pkg, cas, tree, offset, size, width, height, format, out`) and run the bridge:

```
python -I tools\nhl27_bridge.py decode --viewer <Nhl27ViewerDir> --game <Nhl27GameRoot> --jobs <file> --workers N
```

Known Boston home assets (both games, 2048², 12 mips):
- NHL 24 colour `content/character/skater/league/nhl/boston/adidas/home/jersey_adidas_boston_home_color`,
  chunk GUID `acd87bd5-edd1-8d0d-e1b0-758e5b87e6af`, header `work\fabric24-resources\jersey_adidas_boston_home_color.res`,
  live full chunk `work\fabric24-resources\live-color-full.chunk`.
- NHL 24 normal: header `...\jersey_adidas_boston_home_normal.res`, live chunk `live-normal-full.chunk`,
  decoded stock normal `outputs\fanatics-fabric\boston-home-normal-live.decoded.png`.
- NHL 27 normal: `pkg 5, cas 57, tree Data, offset 169968888, size 5595312, BC5`.
- NHL 27 colour: `pkg 5, cas 57, tree Data, offset 178361856, size 2797656, BC1`.

---

## 7. THE NEXT TASK — the Fanatics fabric, done correctly

The user's actual want, in their words: *"i just wanted the right fanatics fabric in game"* — plus faces and the other
workstreams. Not a foreign jersey sheet. Concretely:

**Keep NHL 24's own colour and normal maps. Add only the Fanatics grain to the normal.** The Adidas→Fanatics brand mark
comes through the **array layer** (safe class, §3), not from a sheet copy.

### Two failed attempts — do not repeat them

1. "Flattest 128×128 patch" from the NHL 27 normal, tiled → produced visible 128 px blocks. The patch was large-scale
   jersey structure, not weave.
2. High-pass of a 1024×1024 region with a σ≈6 blur, tiled → bright streaks and a hard diagonal seam. The region was too
   large, so macro shading and edges survived the high-pass.

Both failures share one cause: **the sample region was too big**. Fabric grain is fine detail; a coarse blur keeps
structure.

### Method to use

1. Take a **64×64** window from a genuinely flat fabric area of the NHL 27 normal.
2. High-pass with **σ ≈ 2** so only grain survives; recentre to zero mean.
3. Make the tile **periodic** (wrap-blend or mirror the edges) so 32×32 repetitions across 2048² do not seam.
4. **Frequency split, not addition**: keep the NHL 24 normal's low frequencies (its seams/hems/dimples must stay exactly
   where the mesh expects) and add only the grain band, strength ≈ 0.6–1.0 in tangent space.
5. Recompute Z (BC5 stores only X,Y) before encoding.
6. **Verify at 4× against the stock crop, from two different areas** — that second crop is what would have caught both
   failures. Do this *before* writing to the game.
7. Only then: `build` (BC5, header = `jersey_adidas_boston_home_normal.res`), `apply` to the **test copy** with
   `--restore-check`, read back, and let the user look.

The stock fabric is a fine **diagonal** weave; whatever comes out has to read at that scale when zoomed 4×.

---

## 8. Order of work after the fabric

1. **Faces** — head UVs are identical, so textures transfer verbatim (BC1 colour, BC5 normal, and `_specmask` where the
   destination head has one; choose a **single-use** head from the 475, never `60000`, never a 144–300-player generic).
   Eyes need the rigid position transfer (max 1 influence); lashes keep the template's when the source has none.
2. **Gear** — first check whether gear colour sheets are UV-bound like jerseys (same before/after test, no writes).
   Only the array layers are certainly safe. The 296 NHL 27-only gear models **cannot** be added: no mesh, no material,
   fixed id space.
3. **`.res` headers for other slots** — either one elevated `MeshExport --texture-resource` run (human), or the EBX
   reader (the real fix). Until then only slots with a known header can be written.

---

## 9. Hard constraints

- **Never write to the playable copy** (`Desktop\NHL24PC-CIMS\game`) without a verified test-copy run first. Every write
  goes to `D:\CIMS-test\NHL24-merged`, with backup, read-back and restore verification.
- **Do not** use the Worker for textures (§4) and **do not** copy UV-bound maps across games (§3).
- The suite's Apply path must refuse a mod that names a missing source; `ModApply.SourceProblems` exists for that.
- Do not reset the working tree or commit wholesale; commit per finished thread.
- Verify visually **yourself** before asking the user to look in game. Twice in this session a defect was visible in a
  crop the agent had already produced.

---

## 10. Machine note (unrelated but pending)

The CPU has been throwing fatal WHEA 18 cache-hierarchy errors (11 in 14 days, 8 on one SMT pair) plus 9 Kernel-Power
41 resets — a Ryzen Master `-16` all-core curve optimiser was the cause. `tools\remove-ryzen-master.ps1` (run elevated,
then reboot) and `tools\stability-report.ps1 -Days 2` are ready. XMP now holds at 3200. Zero Display TDRs.
---

## 11. NEXT NEXT TASK — goalie colours and team masks (user asked 2026-11-03, not started)

The user wants **every goalie's pads/blocker/trapper/stick in team colours** and **every goalie in his team's mask**, taking
the values from NHL 27 because the rosters are ported from it. Explicit approval was given to write to the CIMS game.

**Data is already staged** in `%TEMP%\goalie-colors\` (regenerate with the script in this section if missing):

| Table | Rows | Matched | Rows changed | Colour cells |
|---|---|---|---|---|
| `stockgoalieequipment.csv` | 2,129 | 1,737 | 751 | 14,090 |
| `exhibitiongoalieequipment.csv` | 1,783 | 1,435 | 407 | 7,322 |
| `stockteamequipmenttable.csv` | 12,781 | 12,666 | 4,410 | 41,573 |

Keyed by `game_id` (player tables, 52 `*color` columns) and by `team`+`brand`+`equipment` (team table, `zone1..zone15`).
Schemas are identical apart from NHL 27's extra `skatebrand`.

**Masks cannot be coloured** - there is no mask colour column anywhere in the DB. A mask is chosen by `masktype` +
`maskid` (per player) and its colour lives in the mask art `content/character/skater/goalie/helmet/<brand>/textures/
goalie_helmet_<brand>_<n>_color`. So "team mask" means either (a) pointing goalies at the mask whose art is that team's
(NHL 27 assignment, ported by the same `game_id` key) or (b) porting the NHL 27 mask texture - which is only valid if the
mask model's UV matches between games (**not yet tested**; do the same crop comparison that caught the jersey error).

**The write pipeline (DB, not CSV drop) - use the Worker's DB commands, which still work** (it only breaks on `Texture`):

1. `LegacyWrite export <CIMS game> <dir> gamemodes/database/t3db/nhl_game.db` (and the other four DBs).
2. `NHL24Kit.Worker db-dump <db> <metaXml> <outDir>` - see `Suite.Rosters.RosterDbJob.DumpAsync` for the exact call.
3. Overwrite the three CSVs in that dump with the staged ones.
4. `NHL24Kit.Worker db-import ...` to rebuild the DB (see `RosterDbJob`/`RosterSyncRun.ExportStockAsync` for the flow).
5. Write it back with `LegacyApply.Apply` (backup + read-back), exactly as `tools\RosterApply` does.

Do **not** change the DB layout: same tables, columns, record counts, sort order (`RosterDbJob.Guard` checks this).

Regenerate the staged CSVs with: read `nhl24-csv` + `nhl27-csv` under
`C:\Users\devin\Documents\Codex\2026-10-20\...\RosterConversion\`, copy every `*color` column (player tables) and
`zone1..zone15` (team table) from the 27 row with the same key into the 24 row, write with `\r\n`.
### The user's bug report that names the real cause (2026-11-03): "when we moved john gibson he was still using a duck helmet"

Checked in the dumps: **John Gibson, `game_id 9490`** - NHL 24 goalie equipment row says `team 0, maskid 0` (Anaheim's mask),
NHL 27 says `team 10, maskid 10`. So the ported rosters **never updated `stockgoalieequipment.team`**, and the mask follows
that stale team. The goalie equipment rows are per player and independent of the roster tables that were ported.

`maskid == team` is **not** a general rule (only ~2 % of rows in either game), so do not derive the mask from the team id.
Derive it per team from that game's own goalies: for each team, the **most common `maskid` among its goalies** is the team's
mask; every goalie of that team gets it. Order matters:

1. Set `team` per goalie from his current roster (NHL 24 ids) - this is the actual bug.
2. Set `maskid` (and `masktype`) to his team's mode mask from step 1.
3. Set the 52 colour-zone columns from the **team's** row, not from the player's NHL 27 row: for a moved player the 27 row
   carries his old colours (Gibson's `trapperzone1color` is identical in both games because it is still Anaheim's).
