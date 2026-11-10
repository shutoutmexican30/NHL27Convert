# Center Ice Modding Suite — add uniforms through unused rows

**Date:** 2026-11-06  
**Workspace:** `C:\dev\NHL27Convert`  
**Application:** `C:\dev\NHL27Convert\src\Suite.App\bin\Debug\net8.0-windows\CenterIceModdingSuite.exe`

## Immediate priority and intended behavior

The user's direction is explicit:

> “we are having the tool add new uniforms not replace”  
> “its going to use ununsed rows”

Implement **new selectable uniforms using unused/spare `stockteamjerseys` capacity**. Do not replace the team's existing home, away, or alternate uniforms. Existing logical jersey rows and their current artwork must remain intact; the newly exposed row points at the converted NHL 27 uniform artwork.

This is a feature request, not a replacement-mapping bug. The UI should make **Add as new uniform** an explicit, understandable action and keep any replacement workflow separate.

## Existing prototype to build on

`tools\JerseyRows\Program.cs` already implements a dry-run-first prototype:

- Reads `variant_map.json` `extraKits` / `replaces24` targets.
- Finds unreferenced Adidas kit folders that stock rows do not expose.
- Adds rows to both `nhl_game` and `initial_experience` where available.
- Copies the team's alternate Adidas row, preferring variant 2 and otherwise its last non-default Adidas row.
- Sets `variant`, `fevariant`, and `variantleadingscorer`, allocates the team's next `jerseyindex` and `orderid`, sets `isdefault = 0`, and derives `islightcolor` from the NHL 27 colour sheet.
- Inserts at the end of the team's contiguous row block and is idempotent when the row already exists.
- Writes only with `--apply`, takes a backup, reads the databases back byte-for-byte, and restores on a failed check.
- Supports removal, but only when the target is still the team's last row.

`src\Suite.Rosters\T3Db.cs` `InsertRecord` uses the table's spare slots, increments the in-use count, updates header/section CRCs, keeps the database file size unchanged, and leaves other tables untouched. `stockteamjerseys` is suitable for this positional insertion because it is keyed by team and variant rather than record position.

`tools\JerseyDump\Program.cs` is the read-only inspector for current Adidas variants, defaults, and light/dark flags.

**Terminology:** the prototype inserts into spare table capacity; it does not overwrite an existing in-use row. If “unused rows” is later found to include existing but unreferenced in-use records, prove that those records have no references before editing them. Do not treat a zero-looking row as automatically safe.

## Implementation plan for the polished app

1. Extract the prototype's row planning into a UI-independent, tested production service. Keep dry-run planning separate from any write.
2. Present **Add as new uniform** in Standard mode. Show the team, NHL 27 source kit, target unused NHL 24 folder/variant, new `jerseyindex`, and affected databases before export.
3. Rename replacement-oriented metadata such as `replaces24` to addition-oriented semantics where practical, with backward compatibility for the existing JSON. Do not silently reinterpret the user's active map.
4. Coordinate the new database rows with the texture mod as one understandable operation. `ModApply` currently focuses on texture replacements and its comments route legacy database writes through the roster path, so an atomic/coordinated addition workflow still needs implementation. Do not claim the existing app already does this.
5. Preflight must reject insufficient spare slots, duplicate team/style/variant keys, broken team blocks, invalid variant values, missing source art, and unresolved target texture slots before any write.
6. Preserve all original logical rows and their values. Insertion may shift physical record positions, so compare rows by stable team/style/variant identity rather than demanding the entire table remain byte-identical.
7. Make repeated export/Apply idempotent: the same new uniform must not produce duplicate rows or duplicate texture writes.
8. Add tests for row planning, CRC/save/load, same file length, unaffected other tables, light/dark classification, duplicate prevention, insufficient slots, and removal of only tool-added rows.
9. Test the full texture-plus-row workflow only on a disposable game copy, with backup and complete read-back. A real game Apply requires explicit user approval. Final menu appearance and in-game artwork require actual game observation.

Texture targets should remain existing, unreferenced NHL 24 kit folders/slots unless support for genuinely new texture folders is separately proven. Do not create assets the game cannot reference.

### Other useful files

- `src\Suite.Core\VariantMap.cs` — default and extra-kit mapping model.
- `%APPDATA%\CenterIceModdingSuite\data\variant_map.json` — user's active mapping; preserve and back up before edits.
- `src\Suite.Convert27\Matching.cs` and `Convert27ViewModel.cs` — source/target selection and conversion.
- `src\Suite.Convert27\Studio\UniformStudioViewModel.cs` and `StudioMatcher.cs` — simplified studio and mod generation.
- `src\Suite.Convert27\ModApply.cs` — texture Apply entry point and mod validation.
- `src\Suite.Core\Frostbite\TextureSlots.cs` — writes existing named texture chunks.
- `src\Suite.Rosters\T3Db.cs`, `src\Suite.Core\Frostbite\LegacyApply.cs`, and `src\Suite.Core\Frostbite\LegacyFiles.cs` — database editing and legacy-file writing primitives.
- `tools\UniformRestore` — write-capable selected-texture restore tool; do not run Apply mode without explicit approval and a verified target/backup.

## Configured game profiles

The deployed settings are `%APPDATA%\CenterIceModdingSuite\settings.json`.

- `nhl24-v32` — **NHL24PC V3.2**  
  Source and Apply: `C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC`
- `nhl24-v092b` — **NHL24 PC v0.9.2b**  
  Source and Apply: `C:\NHL24-PC-v0.9.2b\NHL24-PC-v0.9.2b`

**Active profile:** `nhl24-v092b`.

The main-window selector switches source and Apply destination together. Both real targets passed `ApplyTarget.Problems` with zero problems. `GameLayout` prefers `game-UPDATE\Patch` when both legacy patch locations exist, keeping reads and writes on the same layer.

Settings backup: `Debugging\build-profiles\settings-before-deploy.json`.  
Deployed copy: `Debugging\build-profiles\settings-deployed.json`.

Both copied game configurations already had `neo_mode: true`; Neo mode has not been boot-verified.

## Utah duplicate crest and logo work

The screenshot's duplicated/offset Utah away wordmark was traced to the saved base composite already containing “UTAH”, shoulders, shield, and sponsor, followed by the branding array drawing those elements again. The small pants “U” is a separate secondary pants mark.

`src\Suite.Convert27\ModApply.cs` now:

- Repairs legacy jersey base composites **in memory** from the corresponding exact NHL 27 `_base_color.png` derived through the `_base_normal.png` path.
- Leaves saved mod JSON files unchanged.
- Leaves pants and sock composites alone.
- Blocks Apply when a legacy base cannot be resolved.
- Keeps one effective replacement per slot; later listed mods win.

Real-export verification before Fast Apply work:

- 2 mods loaded.
- 96 legacy jersey bases repaired.
- 1,565 unique effective replacements.
- 520 overlapping entries skipped.
- 0 unresolved legacy bases.

Relevant saved exports were copied to `Debugging\utah-crest\mods\`; their JSON was not rewritten. The diagnostic composites are under `Debugging\utah-crest\images\`.

This is a verified code/data repair, **not in-game visual verification**. Anaheim/Utah logo placement still needs an in-game check after an explicitly approved Apply.

## Fast Apply work — compiled, not fully tested

The user approved a CPU Fast Apply pipeline with incremental Apply. Implementation was in progress when this handoff was requested. **No game was applied and no GPU backend was added.**

Implemented:

- `src\Suite.Core\CpuParallel.cs` — explicit CPU budgets; on a 24-thread machine, up to four large texture jobs with partitioned codec threads.
- Optional worker parameters in `Images.cs`, `Bc1.cs`, `Bc3.cs`, `Bc5.cs`, `Ps4Texture.cs`, and `Ps4Codec.cs`.
- `src\Suite.Core\Frostbite\TexturePreparer.cs` — bounded parallel read/prepare pipeline.
- `src\Suite.Core\Frostbite\TexturePayloadCache.cs` — content-based payload/hash cache, default 4 GiB payload bound under `%LOCALAPPDATA%\CenterIceModdingSuite\texture-cache`.
- Cache keys include source contents, output format/mips/layers, relevant stock alpha, and untouched array slices. Hash-only entries remain useful after payload eviction.
- Repeat Apply compares desired output hashes against the actual current chunk. If every chunk is current, Apply returns without backup or game writes.
- `ChunkBlocks` and `ChunkReplace` can compress independent Oodle blocks in parallel and reassemble them deterministically. Re-encoded chunks are still decoded and compared before writing.
- `Oodle.Ensure` initialization is locked for concurrent use.
- `TextureSlots.Apply` reports stage timings and passes the worker budget to preparation and Oodle compression.
- Full safety remains in place: target guards, running-game refusal, conflict checks, backup before writes, complete bundle-index rebuild after writes, byte-for-byte read-back, and restore on failure.
- `ModApply` passes `SuiteSettings.Workers` into `TextureSlots.Apply`.

**Last verification command:**

```bash
env 'ProgramFiles(x86)=C:\Program Files (x86)' "/c/Program Files/dotnet/dotnet.exe" build "src/Suite.Convert27/Suite.Convert27.csproj" --nologo --disable-build-servers
```

Result: **succeeded, 0 warnings, 0 errors**.

### Fast Apply work still required

1. Add tests proving worker counts 1/2/6/default produce byte-identical BC1/BC3/BC5 chunks and mip chains.
2. Test cache-key stability after a chunk becomes modded, invalidation on source/alpha/untouched-slice changes, corrupt-payload fallback, and cache size pruning.
3. Test parallel Oodle output and decode/read-back against serial output when Oodle is installed.
4. Exercise no-op repeat Apply and partial incremental Apply on a disposable test fixture, proving no writes in the no-op case and correct backup/read-back in the partial case.
5. Run the complete main suite and application build. The prior **78 passed / 0 failed** result predates Fast Apply and must not be presented as validation of these new changes.
6. Benchmark stages on synthetic inputs and real data using read-only preparation. Do not benchmark by writing a playable build.
7. Keep full read-back until targeted verification has its own exhaustive tests; no targeted-readback optimization was implemented.

## Other verification status

The separate roster suite previously produced **37 passed, 1 failed**. The failing test is:

`T3DbTests.Integers_match_the_dump_and_an_edit_reads_back_with_valid_crcs`

The local `exhibitionteams` fixture says 531 valid records while the stock database has 527. This is a local database/CSV fixture mismatch, not evidence of a Fast Apply or build-profile failure. Refresh or realign the fixtures deliberately; do not hide the failure.

## Workspace and safety rules

- The repository has many unrelated pre-existing uncommitted changes. Preserve all of them. Do not reset, clean, commit wholesale, or overwrite unrelated work.
- Work in `C:\dev\NHL27Convert`. Copy external files into the workspace before inspecting or modifying their copies.
- Do not modify either game, apply mods, restore backups, or rewrite the user's active variant map/mod JSON without explicit user approval.
- Do not claim an in-game result until it has actually been observed.
- `docs\HANDOFF.md` contains older project history. This document is the focused handoff for adding uniforms through unused rows and for the unfinished Fast Apply work.
