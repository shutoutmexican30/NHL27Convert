# Sounds: commentary, goal horns, music

24Kit's Sounds tab browses, plays and exports the game's sounds and replaces them with any WAV file. Plan entries
are `sounds` (whole assets) and `bankSounds` (one sound inside a sample bank).

## 1. The audio format

Sounds are `NewWaveAsset` EBX assets (9,374 of them, 7,534 main commentary) whose audio lives in chunks. FMT's type
info for NHL 24 is wrong (every field reads empty), so chunk ids are found by scanning the raw EBX for known chunk
GUIDs. The EBX stores each chunk's size as a u32 LE right after the GUID (`AudioTools.ChunkIds`).

Audio is **EA SPS + Opus** (codec 14, 48 kHz):
- `'H'` header: `[u24 12][codec|flags][channels-1 << 2][u16 rate][u32 flags:4 | samples:28]`
- `'D'` blocks: `[u24 size][u32 samples][one 20 ms Opus packet]`
- `'E'` end
- The first block holds 648 samples (960 minus the 312-sample preskip).

`AudioCodec.cs` decodes and encodes through FMT's native `opus.dll`. A round trip gives exact sample counts.

**Audio chunks are stored uncompressed** (block type 0x00, 256 KB blocks), because the game streams byte ranges.
Writing them Kraken-compressed plays as silence. `FrostbiteBlocks.Store` is used when the original is stored-only.

## 2. Sample banks (goal horns, songs)

Harmony banks hold many sounds in one stream. `hornbank` = every team's goal horns (439 sounds). The bank index is
made of DSET tables ("TESD"): 24-byte column descriptors `[tag][type width<<24 | kind][param][0][data][x]`, with
value = stored + param. Row n joins the RAM table's `.OFF` (stream offset) with the meta table's `.SIZ` (length)
and `.SRC` (u16 offset into the name pool).

`BankWriter` appends the new SPS (32-byte aligned) to the stream chunk, rewrites the `.OFF` and `.SIZ` rows
(rebasing the column param if needed) and patches the stream size in the bank EBX. Confirmed in game: Washington's
horns replaced.

## 3. Goal songs (on hold)

`music_inxp` table 2 (1206 rows): rows 0–4 are goal songs (Rangers, Funky Woo Hoo ×2, Blues, a Kraken
placeholder), then 33 identical 31-row organ blocks, one per team. The team-to-song choice is in the Harmony logic
(`sound/nhlharmony/nhlharmony`, which FMT reads as empty), not in a table. The Rangers song also has a `.RAM` copy,
which `BankWriter` doesn't handle yet.
