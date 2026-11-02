# CIMS work queue — every open workstream

Single list so nothing is dropped between sessions. Status is what is *proven*, not what is planned.
Last updated: 2026-11-02.

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
- [ ] **First gear swap**: pick a shared model whose NHL 27 art differs meaningfully, rebuild its colour (BC1) and normal
      (BC5) chunks, write to the test copy, read back, restore check. Then assign through the existing DB rows.

## 3. Faces (from the Codex handoff; prototype only, `game_ready: false`)

- Staged: `outputs\face-feasibility\prototype\demidov-surface-prototype.lod0.bin` — 3,157 vertices mapped, skinning/UV/
  indices untouched, tangents recomputed, exact position read-back. **Not a complete face, never applied.**
- [ ] Eyes / eyelashes mapping (eye UVs differ between games; lash UVs ambiguous).
- [ ] Face texture conversion (needs the BC5 path we now have + BC1).
- [ ] Destination slot audit — do not overwrite shared head 60008 in the playable copy (Demidov's head is shared by 62
      players, Yurov's by 24).
- [ ] Rest-pose / animation / material binding validation (bone map 433 vs 434 entries, identity unproven).

## 4. NHL 27 LT/L2 skating + vision-control behaviour

- Names only so far: `gVisionControlPenaltyFriction`, `gVisionControlMaxSpeedMultiplier`,
  `gVisionControlNumTicksInBetweenBackToBackExplicitSideRequestToAddPenalty`,
  `gEnablePTMVisionControlFrictionPenalty_Paranoia2026`, `gEnableSpeedDependentSmoothingVisionControl_Paranoia2026`,
  plus shared `gFacingAngleBlendPivotSpam*`, `gProtectPuckWithVisionControlFriction`, `gPuckCarrierPivotMaxSpeedToAllowAccel`.
- **No defaults, registrations, callers or patch sites validated. No write made.** Remapping is out of scope (user chose
  behaviour only).
- [ ] Locate and verify the registrations + physics code, then define an honest approximation/backport scope.

## 5. Matcher / suite backlog

- **Phantom branding-array layers - fixed.** `Matcher.SliceCount` counts the dumped per-slice images (`slot#0`, `slot#1`,
      ...) and `Matcher.ClampToSlices` removes the empty rows past the last slice when a folder loads. Filled rows are
      kept, labelled "No slice N in NHL 24 (not written)" and pruned at export, so no work is silently dropped.
      Verified against the real dump: Detroit home jersey has 6 numbered textures and 4 array slices, so the grid now
      shows 4 layers instead of 6 phantom ones - the "free sponsor slot" that could never be written is gone.
      `tests\Suite.Tests\ArraySliceTests.cs` covers the count, the clamp and the empty-row case.
- [ ] Still worth doing: validate the cached `NHL24Kit\refs` dump against the index instead of accepting any non-empty
      folder (Carolina/Calgary have no branding art cached at all, which is why every layer there reads as blank).
- [ ] Apply/Restore against a real game folder (the suite's own writer path, not the Worker guard).
- [ ] `data\nhl24-patches.xml`: the V3.2 repack's eboot patches as runtime patches — **unverified** on a clean 1.60.
- [ ] Logo inventory (`tools\LogoInventory`): re-run after opening more teams so the `role_guess` column fills in
      (only 414 of 19,116 sheets are decoded today).

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