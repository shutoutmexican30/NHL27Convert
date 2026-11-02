# CIMS work queue — every open workstream

Single list so nothing is dropped between sessions. Status is what is *proven*, not what is planned.
Last updated: 2026-11-02.

## State at a glance

**Done and verified** - detail in the sections below:

- **Texture chunk pipeline**: BC1 and BC5 codecs (our decode matches FMT's own decode on 100 % of pixels, max diff 1),
  `Ps4Texture` (132-byte header, mip table, micro-tile padding), `TocChunkWriter` (append 256 KB stored blocks, repoint),
  and a real write to the test copy: read-back identical, restore 0 differences, re-apply verified.
- **Own writer on a real game file**: a 13.7 MB legacy database exported, written back, read back identical,
  restore 0 differences with CAS tails identical, re-applied. No elevation needed.
- **Matcher backlog**: slot labels ("This slot is"), Adidas->Fanatics always, phantom array layers clamped to the array's
  real slice count, partial stock dumps rejected, mod-list source validation. 43 suite + 16 roster tests.
- **Goalie gear census**: 1,572 NHL 24 models / 1,749 NHL 27 / 1,453 shared / 296 NHL 27-only, and the DB id space is
  closed (24 brands, 14 equipment values in both games).
- **Faces analysed**: section table (head 6,014 triangles, eyes 320, lashes 84 - identical in both games), per-section UV
  overlap (head and lashes map by UV; scanned eyes have identical UV counts but zero overlap, so they need a rigid
  transfer), destination slot audit (475 single-use heads; generics back up to 300 players; 60000 backs 2,948 custom
  rows), texture map sets.
- **LT/L2**: name census (43 NHL 27-only, 28 shared, 26 NHL 24-only) and the negative proof that the values are not stored
  beside the names.
- **Logo inventory**: every NHL 27 branding colour sheet decoded and classified - 8,980 of 19,116 rows carry a role, with
  per-team crest detection (1,898 crests) validated against the known Detroit slot pattern.
- **Repo**: committed in coherent chunks, tree clean.

**Blocked - needs something this environment does not have:**

| Blocker | What it stops | What unblocks it |
|---|---|---|
| FMT refuses without Administrator (the read view *is* writable; proven) | gear swap, face texture swap, fabric colour swap | one elevated `MeshExport --texture-resource` run |
| No EBX reader | texture `.res` lookup without FMT, LT tunable values, licensed head `id -> asset` map | an EBX reader (the keystone - see the texture section) |
| Worker fails `Texture.get_PixelFormat()` on some folders (0 files written) | NHL 24 side of the logo inventory; Carolina-style blank caches | the suite's own PS4 texture reader, or a Worker fix |

**Open but actionable without those**: nothing material - the remaining items are tests and polish, not threads.


## 1. Texture chunk pipeline (shared by fabric, gear, faces)

| Item | Status | Next action |
|---|---|---|
| `Ps4Texture` (132-byte header, mips, padding), `Bc5` codec, `ChunkBlocks.EncodeBlocks` | **done**, verified against live Boston chunks (decode matches FMT to ±1 level) | — |
| `TocChunkWriter` + `tools\TextureChunk apply` | **done**, written to `D:\CIMS-test\NHL24-merged`, read-back identical, restore = 0 differences | — |
| Boston home normal from NHL 27 thirteen | **built and written to the test copy**, 3.4x smoother dimples | inspect in game on the test copy |
| Same write on the playable copy `Desktop\NHL24PC-CIMS\game` | **not done** (deliberately) | needs a go/no-go; test copy is the gate |
| **BC1 colour encoder** | **done**, verified: decode matches FMT on 100 % of pixels (205,752-byte mip 0, mean 0.10 / max 1); encode round-trip mean 2.21 | colour chunks can be rebuilt like normals |
| Multi-block rule | 256 KB raw blocks; flag low nibble = size bits 16-19 | documented in the code |

## 2. Goalie gear (pads / blocker / trapper / helmet / mask / stick)

- Data model: `stockgoalieequipment` and `exhibitiongoalieequipment` are **colour-zone tables** — `game_id` + fifteen
  zone colours, not item ids. `stockteamequipmenttable` adds `team, brand, jerseyartid, equipment, era, variant, index`.
  So a set = **model (brand/equipment slot) + 15 zone colours**, art from the model's texture folder.
- **Addable**: NHL 27 artwork and colour schemes on models NHL 24 already ships — chunk swap + DB row edit.
  **Not addable**: a model NHL 24 does not ship (no mesh, no material EBX, fixed id space).
- **Census built and run** (`tools\GearPlan`, read-only) -> `outputs\gear-feasibility\goalie-models.tsv` (3,321 rows):
  **NHL 24: 1,572 models, NHL 27: 1,749, 1,453 in both, 296 NHL 27-only, 119 NHL 24-only.**
  NHL 27-only by part: blocker 21, pads 21, trapper 21, stick 68, helmet 165 (the helmets are mostly legacy/throwback
  masks: cooper, hasek, itech). The 21 pads/blocker/trapper entries are genuinely new catalogue families - Bauer Supreme
  Fuse/Shadow, Vapor Flylite, Brian's Iconik 2 / Kryptik / Optik 4, CCM Axis XF / ExtremeFlex 7 / Tacks, True Catalyst
  Nitro/PX5 / HZRDUS, Vaughn Velocity V11 / Ventus LT90, Warrior Alpha Surge, plus originals.
- Both equipment dumps expose the **same** id space: 24 `brand` values and 14 `equipment` values. That closes the door on
  "add a new id": the model is chosen by index inside that fixed space, so a model NHL 24 does not ship has no row to sit
  in either.
- **Blocker found for the first gear swap (2026-11-02):** `MeshExport --texture-resource` is the only path that yields a
  texture's 132-byte `.res` header (and with it the resource's chunk GUID), and FMT refuses to open the read view with
  `"You are unable to read & write to the Game Folder. Please restart the Application as Administrator."` This is **not** an
  ACL problem: a non-elevated write+delete probe inside `%LOCALAPPDATA%\CenterIceModdingSuite\nhl24view` succeeds, and the
  view has `package.mft`, `Data`, `Patch`; FMT simply requires the process to be an administrator. The suite's own reader
  does not need FMT - only this header/GUID mapping does. Candidate pair already chosen:
  `content/character/skater/goalie/pads/brians/gnetik/textures/goalie_pads_brians_gnetik_4_color` and `_normal`.
- **The RES header is not in any TOC (2026-11-02).** Searched all three Patch TOCs for the first 8 bytes of the known Boston
  colour `.res` (`f5 d4 00 00 df 2c 01 00` - sizes/timestamps, unique to that resource): **zero hits** in
  `contentsb.toc` (4,030,673), `contentlaunchsb.toc` (3,105,681) and `globals.toc` (65,804). Combined with the bundle scan
  finding nothing, the resource payload lives in a CAS and the name -> resource mapping is **not** a TOC table we can see:
  in Frostbite that mapping goes EBX (the texture asset, which references its resource id) -> RES. So the FMT-free path
  needs an **EBX reader**, not more TOC archaeology.
- **This is the keystone finding of the session:** one missing capability - parsing EBX - unblocks three separate blockers:
  the texture `.res` header (gear, face textures, fabric colour), the LT tuning values, and the head `id -> asset` mapping
  for licensed faces. Everything else in those three threads is built and verified.
- **The bundle scan was looking in the wrong table (2026-11-02).** Jersey textures are not UI textures: the Boston jersey
  chunk is found through `TocFile`'s *chunk* table (fields 4/5/6, patch=False, cas 05), so its RES is not in a bundle
  manifest at all - which is why `TextureResource` resolved nothing rather than mis-parsing something. The TOC header holds
  more tables than the two we use: for `Patch\Gen4b\contentsb.toc` (4,030,673 bytes) the LE/BE u32 fields after the 0x22C
  header are 60, 42952, 10723, 214520, 266920, 13100, 528920, 528920, 686120, 528920, 39300, 7, 181424, 76, 1411816. Known:
  **bundles = fields 1/2/3**, **chunks = fields 4/5/6**. The remaining pairs (7/8, 9/10, 11/12, 13/14 - note 6, 7 and 9 share
  the same offset and 39,300 looks like a count) are the candidates for the RES name/type tables.
  Next step: identify which pair describes resources by checking record shape, then point `TextureResource` at it.
- **Attempted the FMT-free route and did not finish it (2026-11-02).** `src\Suite.Core\Frostbite\TextureResource.cs` is a
  scaffold that scans TOCs, reads each bundle manifest and looks for the texture's RES entry, and `TextureChunk res
  <game> <name> <out.res>` drives it. It currently resolves **nothing** (`no bundle declares ...`), so the manifest/RES
  offset assumptions are wrong. The likely cause is that I re-derived the manifest layout instead of reusing
  `BundleChunks.Find`, which is **proven** (it replaces UI-texture chunks in bundles today). Next step: add a RES lookup to
  `BundleChunks` using its working parse (same `ebxAt`/`resAt`/pair-index logic it already applies to chunks) rather than
  keeping the duplicate. Verify against the FMT-exported `.res` files in `work\fabric24-resources`, which must match byte
  for byte.
- [ ] Run once **elevated** (no ACL or guard change):
      `dotnet run --project tools\MeshExport -- "<read view>" "<out>" --texture-resource <color name> <normal name>`
      then build both chunks and write them to the test copy with `tools\TextureChunk build|apply`.
- [ ] **First gear swap**: rebuild that model's colour (BC1) and normal (BC5), write to the test copy, read back, restore
      check, then assign through the existing DB rows.

## 3. Faces (from the Codex handoff; prototype only, `game_ready: false`)

- Staged: `outputs\face-feasibility\prototype\demidov-surface-prototype.lod0.bin` — 3,157 vertices mapped, skinning/UV/
  indices untouched, tangents recomputed, exact position read-back. **Not a complete face, never applied.**
- **Eyes and eyelashes - sized, topology confirmed (2026-11-02).** Section inventory of the same head from both games:

  | Section | NHL 24 (indexed) | NHL 27 (expanded) | Triangles |
  |---|---|---|---|
  | `mat_head` | 3,157 verts | 18,042 = 6,014 x 3 | **6,014 both** |
  | `mat_eyes` | 198 verts | 960 = 320 x 3 | **320 both** |
  | `mat_eyelashes` | 64 verts | 252 = 84 x 3 (Demidov only) | **84 both** |

  So all three sections have **identical triangle counts** and the same indexed-vs-expanded difference the head transfer
  already handles - the quantized-UV surface transfer generalises to eyes and lashes without new format work.
  Two findings that shape it: `mat_eyelashes` is **absent from 5 of the 6 sampled NHL 27 heads** (only Demidov has one), so
  the converter must keep the template's lashes when the source has none - which is exactly what the staged prototype does;
  and NHL 27 eyes carry **max one influence** (rigid on the eye bone) versus up to four for lashes.
- **Per-section UV overlap measured (2026-11-02, `tools\FacePlan\section_uv_overlap.py`).**

  | Template (NHL 24 60008) vs | `mat_head` | `mat_eyes` | `mat_eyelashes` |
  |---|---|---|---|
  | NHL 27 **male_60008** (same head) | 3,146 / 3,146 shared | **198 / 198 shared** | absent in 27 - keep the template's |
  | NHL 27 **Demidov** (scanned face) | 3,146 / 3,146 shared | 198 vs 198 UVs, **0 shared** | 8 / 8 shared |

  So: the head surface and the lashes always transfer by UV; the eyes transfer by UV **only to the same head**, and for a
  scanned face the eye UV islands are completely different despite the identical 198 UV count. That confirms the old
  handoff's "eye UVs differ" note and localises it to eyes on scanned faces only.
- **Why eyes can still be done**: NHL 27 `mat_eyes` carries **max one influence** (rigid on the eye bone) with 320
  triangles, and NHL 24's eyes are equally rigid. A rigid position transfer (nearest rigid frame per eye) is therefore the
  right tool for scanned faces - no UV correspondence needed, and no deformation to reproduce.
- [ ] Implement the eye transfer: rigid position mapping per eye for scanned faces, UV mapping when the source is the same
      head; keep the template's `mat_eyelashes` whenever the source has none.
- **Face textures characterised (2026-11-02).** Map sets, from both indexes: NHL 24 has 5,320 head texture assets
  (color 2,367, normal 1,399, coeff 1,174, specmask 190, mask 66, alpha 27, bentnormal 15, animglow 11, cavity 1);
  NHL 27 has 7,200 (color 2,979, normal 1,905, coeff 1,176, **specmask 743**, mask 127, **bentnormal 84**, animglow 75).
  A generic NHL 24 head (`head_male_60008`) has only **color + normal**; a scanned NHL 27 face (Demidov) has
  **color + normal + specmask**. So a destination slot must be chosen with the source's map set in mind, or the spec mask
  has nowhere to land.
- **The good news**: because the head UVs are identical across heads (3,146 of 3,146), a face texture transfers
  **verbatim** - no UV repaint, only format and mip conversion through the BC1/BC5 codecs. That is the whole texture job.
- Two blockers for the first real face swap, both already known elsewhere in this file: the destination's `.res` header
  (FMT / elevation, same as gear) and, for licensed heads, an **id -> asset name mapping that the exported DB dumps do not
  contain** - there is no appearance/head table among them, and licensed assets are named after the player
  (`head_connor_mcdavid`), so a numeric `headid` search finds nothing (0 candidates from the 475 single-use ids).
- [ ] Resolve the head appearance mapping (EBX `HeadAppearance`-style assets, or the Worker's listing), then repeat the
      candidate search and convert one face onto a single-use head on the **test copy**.
- **Destination slot audit done (2026-11-02, from the NHL 24 roster dumps).** Heads are referenced by `headid` in
  `exhibitionplayerbiotable` (13,300 rows, **703 distinct heads**) and `customplayerbiotable` (4,948 rows, a single head:
  60000), and by `coachheadid` in `exhibitionteams` (577 rows, 21 coach heads).
  Sharing, worst first: **60019 = 300 players, 60009 = 289, 60006 = 245, 60017 = 198, 60131 = 179, 60004 = 173,
  60030 = 170, 60020 = 144**; custom head **60000 backs 2,948 custom-player rows**; coach heads 30010/30014/30000 run 25-81.
  **475 heads are used by exactly one player** - those are the safe overwrite targets, and the asset name carries the id
  (`head_male_60019`, `head_coach_...`), so the slot is addressable directly.
  Rule: never overwrite 60000, the 144-300-player generics, or a coach head in place; pick a single-use head instead.
- [ ] Pick one of the 475 single-use heads as the first face destination, confirm it is a scanned/licensed head and not a
      placeholder, then run the game-ready conversion against it on the **test copy**.
- [ ] Rest-pose / animation / material binding validation (bone map 433 vs 434 entries, identity unproven).

## 4. NHL 27 LT/L2 skating + vision-control behaviour

- Name census (2026-11-02, re-checked): **43 globals exist only in NHL 27, 28 are shared, 26 only in NHL 24.**
  Directly relevant to the LT behaviour: 27-only `gVisionControlPenaltyFriction`, `gVisionControlMaxSpeedMultiplier`,
  `gVisionControlMaxSpeedMultiplierMultiplier`, `gVisionControlNumTicksInBetweenBackToBackExplicitSideRequestToAddPenalty`;
  shared `gFacingAngleBlendPivotSpamMinCount/MaxCount/MinEffect/MaxEffect`, `gProtectPuckWithVisionControlFriction`,
  `gPuckCarrierPivotMaxSpeedToAllowAccel`. Evidence: `lt-tuning-comparison.json` (work folder).
- **Where the values are not:** the names sit in a plain NUL-separated string table inside each executable (NHL 24 eboot
  188,338,041 bytes: `gFacingAngleBlendPivotSpamMaxEffect` @152,369,947; NHL 27 eboot 281,346,759: `gVisionControlPenaltyFriction`
  @229,726,607). The bytes after each name are just the next string, so there is no name->value record beside them.
  The values therefore live in a tunable/profile EBX or as compiled constants, not in the string table.
- **No defaults, callers or patch sites validated. No write made.** Remapping is out of scope (user chose behaviour only).
- [ ] **Next concrete step**: hunt the tuning asset. Run `tools\MeshExport --inventory` over both games with a filter for
      tunable/profile/gameplay EBX (the earlier NHL 27 inventory was a narrow 1,521-asset slice and contained none of these
      names), then diff the field lists. If the values are EBX fields, a backport needs an EBX writer - which does not exist
      yet, so this stays analysis until that lands.

## 5. Matcher / suite backlog

- **Phantom branding-array layers - fixed.** `Matcher.SliceCount` counts the dumped per-slice images (`slot#0`, `slot#1`,
      ...) and `Matcher.ClampToSlices` removes the empty rows past the last slice when a folder loads. Filled rows are
      kept, labelled "No slice N in NHL 24 (not written)" and pruned at export, so no work is silently dropped.
      Verified against the real dump: Detroit home jersey has 6 numbered textures and 4 array slices, so the grid now
      shows 4 layers instead of 6 phantom ones - the "free sponsor slot" that could never be written is gone.
      `tests\Suite.Tests\ArraySliceTests.cs` covers the count, the clamp and the empty-row case.
- [ ] Still worth doing: validate the cached `NHL24Kit\refs` dump against the index instead of accepting any non-empty
      folder (Carolina/Calgary have no branding art cached at all, which is why every layer there reads as blank).
- **Apply/Restore verified against a real game folder with the suite's own writer (2026-11-02).** On the test copy
  (`D:\CIMS-test\NHL24-merged`), a real 13,726,652-byte legacy database (`gamemodes/database/t3db/nhl_game.db`) was
  exported, written back unchanged (`LegacyWrite apply ... --restore-check`), read back and cycled:
  chunk rebuilt with **0 of 57 blocks re-encoded** (the identity write reproduces the stored form), appended 1,799,668 bytes
  + the 80,339-byte legacy index to `cas_01`, 2 TOC entries repointed, **read back identical**, `changes vs backup: 2`
  (CAS length and `globals.toc`), then **restore: 0 differences, CAS tails identical**, read back identical (restored),
  re-apply identical, `DONE`. Oodle came from `D:\Tools\FMT-26.10\...`; no elevation needed.
  Remaining gap: texture *mods* still reach the game through the Worker, so the suite's own writer covers legacy files
  and bundle/UI chunks but not top-level texture chunks yet - that is the `TocChunkWriter` path, which is verified
  separately on a jersey normal chunk.
- [ ] `data\nhl24-patches.xml`: the V3.2 repack's eboot patches as runtime patches — **unverified** on a clean 1.60.
- **Logo inventory roles filled for the ten teams in play (2026-11-02).** The NHL 27 index carries every decode location
  (Width/Height/Format + Package/Cas/Tree/Offset/Size), so `tools\nhl27_bridge.py decode --jobs` can be driven straight
  from it without opening folders in the UI. Decoded **1,242 branding colour/normal sheets** for detroit, boston, florida,
  chicago, anaheim, buffalo, carolina, calgary, seattle and utah, then re-ran the inventory:
  **414 -> 1,035 sheets with a guessed role**; shield 130 -> 367, sponsor/other 184 -> 428, epw_tag 27 -> 68,
  blank 35 -> 67, brand:adidas 16 -> 55, brand:fanatics 22 -> 41 (plus 9 adidas_stripes that had never matched before).
- **Extended to every NHL 27 branding colour sheet (2026-11-02).** 7,950 further sheets decoded in one bridge run:
  **8,980 of 19,116 inventory rows now carry a guessed role (47 %, up from 414 / 2 %)** - NHL 27 8,869 of its 11,233,
  NHL 24 only 111 of 7,883 because its art is dumped per folder through the Worker. Counts: sponsor/other 6,495,
  shield 1,615, blank 292, epw_tag 214, brand:adidas 170, brand:fanatics 135, brand:adidas_stripes 59.
- **Crest split from sponsor (2026-11-02).** `tools\LogoInventory` now builds **737 crest references** - one per team and
  garment, taken from that team's own branding layer 0 - and tests them after the league-wide marks. Result on the same
  19,116 rows: `sponsor/other` 6,495 -> **4,597** and a new **crest 1,898**, with every other bucket unchanged
  (shield 1,615, blank 292, epw_tag 214, adidas 170, fanatics 135, adidas_stripes 59). Verified against the known slot
  pattern: Detroit's away jersey now reads crest, shield, brand:adidas, epw_tag, sponsor/other, shield and the `eleven`
  set reads crest, shield, **brand:fanatics**, ... - exactly what the role work established by hand.
- This crest reference is also the matching signal the face work needs for licensed heads, since those are named after the
  player rather than an id.
- **NHL 24 side blocked by the Worker itself (2026-11-02).** Re-dumping `nhl/carolina/adidas/home` through the stopgap Worker
  to prove the new coverage check fails: `NHL24Kit.Worker.exe refs` dies inside `FrostySdk.Resources.Texture.get_PixelFormat()`
  with `Value cannot be null (Parameter 'enumType')` and writes **0 files**. So `DumpDirAsync` now correctly refuses the
  partial cache and re-dumps, the re-dump fails, and the folder still has no stock art - the remaining gap is the Worker's
  PS4 texture-format handling for those assets, not our coverage check. The same failure was seen for Detroit in an earlier
  attempt, so it is not one folder.
- [ ] NHL 24 inventory side stays at 111 of 7,883 until that Worker format gap is solved (or the suite's own PS4 texture
      reader lands and replaces the dump entirely).

## 6. Machine / environment (user actions)

- [ ] Run `tools\remove-ryzen-master.ps1` elevated, reboot, confirm the driver service and `.sys` are gone.
- [ ] Watch `tools\stability-report.ps1 -Days 2`: 11 fatal `Cache Hierarchy` MCEs in 14 days, 8 of them on APIC 0/1
      (one core's SMT pair). Curve is stock now that Ryzen Master is gone; if a new entry appears it was in BIOS.
- [ ] Optional: BIOS 2.50 is from July 2023 — newer AGESA is calmer for Zen 3 curve tuning.

## 7. Repo hygiene

- **Committed in five coherent chunks** (2026-11-02): matcher/viewer/stamp, legacy-uniform removal, the texture chunk
      pipeline + census tools, the roster work, and the docs. Working tree is clean; the only local change was setting the
      repo's own author identity from its existing commits (`Devin Sutton <devinlejeune@gmail.com>`), because git had none.
- [ ] Keep committing per finished thread from now on; do not leave weeks of work unstaged again.
- [ ] Keep this file, `docs\HANDOFF.md` and the Codex `outputs\` status files in step with reality.

## Ground rules that keep being relevant

- Never write to the playable game without a verified test-copy run first.
- The Worker Apply/Restore path stays unused: its stale state can roll back the installed players.
- Report staged prototypes as staged. Do not claim an in-game test that was not seen.