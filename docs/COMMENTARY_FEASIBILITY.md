# Commentary port NHL 27 (PS5) → NHL 24: feasibility

Read-only investigation, 2026-11-14. Nothing was written to any game folder. The scratch tools and dumps are in
`C:\dev\scratch\commentary` (`SndProbe`, `DbDump`, `scripts\spsscan.py`, `scripts\spssize.py`).

**Verdict:** feasible. Both games use the **same audio format**, so clips copy across byte for byte. The speech
index was decoded on 2026-11-15 (section 6): every link from a surname keyword to a clip is mapped and verified. The
one link left, player `audioid` → surname keyword, happens in game code. The donor-surname plan in section 6 avoids
needing it.

## 1. NHL 24 target (`D:\CIMS-test\livecopy`, PS4 build CUSA37933 behind the PC launcher)

- **Assets:** 9,459 `sound/speech/**` NewWaveAssets, about **4.19 GB** of audio chunks (sizes taken from the
  EBX). Main commentary alone is 7,534 assets. The audio lives in TOC chunks (not bundle chunks) in install package
  05, cas 5/6/7, plus Patch cas 11 for the LCU banks.
- **Codec:** EA SPS + Opus, header `48 00 00 0C 1F 04 BB 80`, 48 kHz. `D` blocks hold 20 ms Opus packets (first
  block 648 samples), stored uncompressed. See `docs/reference/SOUNDS.md`. 24Kit's `AudioCodec` already decodes
  and encodes this format through FMT's `opus.dll`.
- **Name-call waves:** each surname wave is one big multi-clip stream, not one asset per player.

  | asset (`soundwaves/eng_inxp/…`) | chunk |
  |---|---|
  | `pm_pxto_player_surname` | 37.4 MB |
  | `ps_player_surname_pxspos` | 21.5 MB |
  | `pa_player_surname_pxspos` | 10.6 MB |
  | `pm_player_surname_pxsend` | 3.7 MB |
  | `pa_player_surname_pxsend` | 3.2 MB |
  | other surname waves (`pxby`, `pxto_l2`, …) | rest of ≈180 MB |

- **Speech index:** `sound/speech/maincommentary/wc/eng_inxp` (164 KB EBX, the bank or wave catalogue) and
  `database/maincommentarysentenceplayersamplemetadata` (5.4 MB) plus `maincommentarydb` (2.7 MB).
- **Update banks:** `wc/eng_lcu_01` is EA's post-launch name update (11.7 KB EBX, its own surname waves in Patch
  cas 11). **`wc/eng_lcu_02` … `eng_lcu_10` ship as empty 358-byte placeholders.** They are a natural slot for
  new names.
- **CAS room:** files are ~1 GB on stock (package 05 has cas_01–16). The ones that grew are cas_05 (3.5 GB),
  cas_13 (2.8 GB) and cas_10 (2.5 GB). `layout.toc` does not list cas counts.
  - `TocChunkWriter.Replace` appends to the chunk's *current* cas and only repoints offset/size. Writing new audio
    into a stock cas with about 3 GB headroom means letting `Repoint` change the cas index too: a small change, and
    it avoids the unknowns of a brand-new cas file.
  - A new `cas_17.cas` is probably fine for the engine (Frosty-style tools do it) but is untested.

## 2. NHL 27 source (`D:\Archives\PS5\PPSA34063\…\PPSA34063-app0`)

All 264 cas files were scanned for SPS streams (stored blocks, no compression).

| codec byte | streams | size | hours | where |
|---|---|---|---|---|
| `0x1F` EA Opus (**same as NHL 24**) | 81,767 | 1.64 GB | 55.7 | pkg05 cas_35–37 and cas_126–129 (speech), pkg01 |
| `0x1D` EA ATRAC9 (`FE…` AT9 config) | 9,701 | 0.30 GB | 5.2 | pkg00/01/02 (SFX, crowd) |
| `0x1E` | 256 | 0.03 GB | 0.4 | pkg00/01 |

- The speech header (`48 00 00 0C 1F 04 BB 80 …`) and the `D` block layout (`00000288` = 648 samples, Opus TOC
  `F8`) are identical to NHL 24, so **no transcode is needed** for commentary.
- The asset names are in the pkg01 and pkg05 bundle manifests (pkg05/cas_01: maincommentary, threes, gauntlet,
  announcer, fe_bap, fe_woc).
- **NHL 27 surname waves are structured differently:**
  - pa: `pa_player_surname_pxsend` / `pxspos`
  - pm: `pm_player_surname_level_1..4`, `pm_pxby_player_surname_level_1`, `pm_pxby_goalie_surname_level_2`,
    `pm_pxto_player_surname`
  - NHL 27 has no `eng_lcu_*` banks.

  NHL 27's sentence system (intensity levels) therefore can't be dropped in. Only the clips can move, remapped onto
  NHL 24's `pxsend`/`pxspos`/`pxto`/`pxby` slots.
- **Tools:**
  - ffmpeg 9.0.1 (WinGet) has `atrac9` and `libopus`, so even ATRAC9 SFX could be converted after wrapping the
    frames in an AT9 RIFF.
  - vgmstream-cli is not installed.
  - FMT 26.10 has NHL24PS4 and NHL27PS5 plugins but no NHL 24/27 SDK. `NHL22SDK.dll` and `NHL23SDK.dll` do carry
    the type schema (`SentencePlayerWaveCollection`, `SentencePlayerSampleMetadataFeature`, `SentencePlayerName`,
    `SubtitleChunkGuid`, `HarmonySampleBank`, …): the best reference for parsing the speech EBX by hand.
  - `D:\Tools\NHL27TextureViewer\frostbite` reads NHL 27 PS5 toc/cas and bundle names. It can be extended to pull
    NHL 27 speech chunks by name.

## 3. Name calls

- **Player link:** the player bio (`exhibitionplayerbiotable`) has `audioid` (u16, the call id) and `surnameid`
  (index into `playernames`).
- **`playernames`:** 16,182 rows in NHL 24, 17,602 in NHL 27. Its own `audioid` is the selectable-name list. Both
  games have the same 2,003 voiced names, all with identical ids, so NHL 27 added no voiced names to this list.
- **Ids are shared:** both games use the same id space (max audioid 34,797). Players present in both keep the same
  `audioid`.
- **Missing calls:** of 760 NHL 27 players on NHL teams, **57 have an `audioid` NHL 24 doesn't use.**
  - **17 share a surname** with a player NHL 24 already voices (Moore, James, Doan, Martin, Wood, Leonard, Smith,
    Hutson, …). Pointing their `audioid` at that player's id gives a call with **only a DB edit**. This could go
    into `NewPlayers.cs`, which currently writes `audioid = 0`.
  - **40 need new clips:** Celebrini, Michkov, Demidov, Hagens, Schaefer, Nazar, Buium, Levshunov, Voronkov,
    Mukhamadullin, Lohrei, Laferriere, Frondell, Willander, …
- **Roster structure:** adding calls needs no change to it. `audioid` is an existing column, and the new clips go
  into the speech bank, not the DB.

## 4. Recommended approach

1. **Quick win (DB only):** in the roster port, set `audioid` for the 17 surname-sharing players.
2. **Decode the speech index.** Using the NHL 23 SDK types as a guide, reverse `wc/eng_inxp` / `eng_lcu_01` and the
   sentence metadata, to learn how an `audioid` resolves to a clip (offset/variation) inside the surname waves.
   Check this against a known player before writing anything. **This is the main blocker.**
3. **Pull the clips.** Extract the NHL 27 surname clips for the 40 players by name or id from the pkg05 Opus
   streams; they are already the right codec.
4. **Write them into a bank.** Write them as a new name bank, either by filling the empty `eng_lcu_02` slot
   (mirroring `eng_lcu_01`) or by appending to the existing surname waves the way `BankWriter` does for the horn
   bank. Put the new chunks in a stock cas with headroom (needs a cas-index-aware `Repoint`).
5. **Full commentary swap:** this is *not* recommended. NHL 27's sentence DB and wave set differ, and the payoff
   is low for 4+ GB.

## 5. Blockers and risks

- Solved 2026-11-15 except one step (section 6): keyword → take → audio is mapped, but player `audioid` → keyword
  is computed in game code. The donor-surname plan works around it.
- NHL 27 surname waves are split by intensity level and NHL 24's by phrase position, so which clip goes into which
  NHL 24 slot has to be chosen by hand or by heuristic.
- Writing a new cas file is untested on this engine. Appending to a ~1 GB stock cas is the safer path.

## 6. Speech index, decoded (2026-11-15)

Code: `src/Suite.Sounds` (`SpsStream`, `HarmonyBank`, `SpeechDatabase`, `SpeechCatalog`) and the read-only CLI
`tools/SpeechProbe`.

```
SpeechProbe <game> [--summary] [--verify] [--export <dir>] Stamkos kw:1091 var:87275
```

`--verify` on the live copy: all **35,641 takes in the 17 surname waves** sit at an SPS stream of the listed length.

### The chain

| step | where | how |
|---|---|---|
| player → surname keyword | **game code** (`PG_*_surname_id` param getters on `player_ID`) | not in the DB, the speech assets or the eboot as a table (searched: pair tables, dense arrays, every bio column) |
| keyword → takes | `database/maincommentarykeywords` (SentenceKeywords): 38,047 `{List<int> Keywords, u32 VariationId}` | Stamkos = 1091, both Toews = 1082. Ids come in recording batches (1081–1095 stars, 1462+ legends A–Z) |
| take → audio | each wave's RES, an `SBle` Harmony bank (TESD tables, see `HarmonyBank.cs`) | per-clip VariationId, stream offset (+3) and playback length into the wave's single chunk |
| take → text | `database/…sentencetranscriptioncollection`: 70,975 `{text, VariationId}` | "Stamkos", "to Stamkos", "by Stamkos" |
| bank list | `wc/eng_inxp`, `wc/eng_lcu_01` (SentencePlayerWaveCollection) | only `List<PointerRef> NewWaves` (3,739 imports for eng_inxp), no ids |

- **Class GUIDs** in NHL 24 match FMT's `NHL23SDK.dll`, so its layouts apply: SentenceDatabase, Sentence, Phrase,
  SentenceKeywords/KeywordData, SentenceSampleMetadata, NewWaveAsset, SoundDataChunk.
- **EBX pointers** are self-relative i32 values in 8-byte fields; a list's count sits just before its data; the root
  instance is at 0x30.
- **Sample metadata** (`…sentenceplayersamplemetadata`) only holds `speaker` / `MNar`-style tags per VariationId. It
  isn't part of name selection.

**Example: Stamkos (keyword 1091), 6 keyed takes**

| wave | VariationId | text |
|---|---|---|
| `pa_player_surname_pxsend` | 87275 | "Stamkos" |
| `pm_player_surname_pxsend` | 12059, 14402 | "Stamkos." |
| `pm_pxby_player_surname` | 20889 | "by Stamkos" |
| `pm_pxto_player_surname` | 20422 | "to Stamkos" |
| `pm_pxto_player_l2_surname` | 74375 | "to Stamkos" |

`ps_player_surname_pxsend` has one more take (56307) with no keyword.

**Wave coverage:**
- **Keyed (all eng_inxp except ps):** pa_pxsend/pxspos 943 + 2,835, pm_pxsend/pxspos 1,044 + 5,587, pm_pxby 4,475,
  pm_pxby_goalie_l2 516, pm_pxto 6,305, pm_pxto_l2 6,095.
- **Unkeyed:** the `ps_*` waves (955 + 4,800 takes) are selected by some other route; not needed for the plan below.
- **eng_lcu_01:** pa_pxspos 1,978 takes plus a handful in the others (EA's post-launch names, e.g. "Bairos" = keyword
  8393).

### Adding a name call

1. **Donor surname (works without the code mapping).** Pick a surname that is voiced in NHL 24 but belongs to no
   NHL 27 player. The pa wave alone has 85 safe ones (e.g. Francouz 2298, Balmas 2330, Billins 2211).
   - Replace every take carrying that keyword with the new player's clips, in each keyed wave.
   - Set the new player's `audioid` to the donor player's audioid.
   - The donor player loses his own call; he isn't on any NHL 27 roster.
2. **New keyword (cleaner, blocked).** Add `KeywordData` rows and new takes, for example in the empty `eng_lcu_02`
   slot (mirroring eng_lcu_01: wave collection + waves + RES + chunks). This only helps once the audioid → keyword
   code step is understood; it may be a range/offset rule or a table this search missed. Needs a disassembly look at
   the `PG_*surname_id` getters.

### Writer design (not built)

**`HarmonyBankWriter`** works on one wave RES and its chunk, and takes a set of (VariationId → new SPS stream)
replacements:
1. Rebuild the chunk by concatenating streams, 4-byte aligned, the same way EA does.
2. Rewrite the `3FE2AFD5` columns: offset +3, and duration (playback length; match the clip's samples/48000 minus
   preskip and confirm against an existing take). Repack a column whenever a value no longer fits its width: bump
   the width byte, or rebase the param the way `BankWriter` already does for the horn bank's `.OFF`.
3. Update `A28D4A0D/DC19107B` (chunk size) and the wave EBX `SoundDataChunk.ChunkSize` (u32 right after the chunk
   GUID).
4. Write the chunk stored-uncompressed (`FrostbiteBlocks.Store`; Kraken-compressed audio plays as silence) through
   `TocChunkWriter`, extended to repoint into a cas with headroom.

**Adding takes** (rather than replacing) also grows the row count of `11D5B185`/`A29AF127`/`3FE2AFD5`. Keep
`A29AF127` sorted by VariationId; `11D5B185` is a lookup whose order rule isn't known yet, and needs to be solved
first.

### Open

- **audioid → keyword** (code side).
- **NHL 27 side:** the same reader should work, since the classes are likely unchanged, but `BundleIndex` doesn't
  read the PS5 layout. Use the TextureViewer `frostbite` package, or teach `BundleIndex` the PS5 tocs, to pull the
  NHL 27 takes for the 40 missing players by transcription text.
- **Duration column:** its exact rule (playback length vs stream samples) is unknown.

## 7. Reuse: the Utah Mammoth goal horn

A shared **SPS + Harmony/speech bank reader and writer** covers both goals:

- **Read:** walk SPS streams, read the DSET tables, read the bank EBX.
- **Write:** append the stream, rewrite the `.OFF`/`.SIZ` rows, patch the stream size, repoint the cas.

The Utah horn is the simple case: a `hornbank` row, which `BankWriter` already handles. Name calls are the same
machinery plus the speech-index layer. As of 2026-11-15, `Suite.Sounds` holds the reader side: `HarmonyBank`
parses any SBle bank, horn bank included. The working `AudioCodec`/`BankWriter` code lives in 24Kit and its source
isn't on this PC, so the writer has to be rebuilt here from the design in section 6.

## 8. Tool and plan (2026-11-15)

Built, tested in memory, **not applied**: the session's permission check blocked both the scratch copy of the game
and the apply. Details and commands are in `docs/HANDOFF.md` (top section).

- **NHL 27 reader:** `Ps5SoundAssets` reads the PS5 tocs and bundles directly. `SpeechProbe <app0> --verify` passes
  on all 15,043 NHL 27 surname takes.
  - NHL 27 waves: pa pxsend/pxspos, pm `level_1..4`, pm_pxby_player/goalie, pm_pxto.
  - Keywords are per wave in NHL 27; in NHL 24 they are per surname.
- **Writer:** `SpeechBankWriter` plus `BundleEntryWriter`, and `TocChunkWriter` with `into:` + `storeUncompressed`.
  `Commentary --selftest` is byte-identical on all 17 NHL 24 surname waves, both for an unchanged rebuild and for the
  stored-block re-encode. A swap test re-reads correctly.
- **Plan** (`data/commentary_plan.json`): 21 DB-only reuses, 25 donor surnames (136 takes) and 30 silent players.
  - The 30 aren't voiced in NHL 27 either (Hagens, Demidov, Buium, Frondell, Levshunov, …), or their only takes fit no
    NHL 24 slot.
- **Exported clips:** the 22 of the original 40 that NHL 27 voices, in `C:\dev\scratch\commentary\nhl27_names` (raw
  SPS).
- **Risk:** the in-game id mapping (`surnameid`/`audioid` → keyword) is inferred. Only a boot test with sound
  confirms it.
