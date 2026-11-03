# How NHL24Kit replaces textures in NHL 24 (PS4)

This is how NHL24Kit writes a new image into NHL 24's game files so the game uses it: in menus, in gameplay, at every distance. It covers the file formats, the conversion, every place the game keeps a reference to a texture, and the rules found by testing in game.

Applies to NHL 24 PS4 (CUSA37933, v01.00), extracted and played with shadPS4 or nhl24pc. Code: `C:\dev\NHL24Kit\Worker` (writer) and `C:\dev\nhl24-fmt-plugin\NHL24PS4Plugin` (conversion code shared with the FMT plugin).

---

## 1. The approach in one paragraph

NHL24Kit never edits data in place and never uses a mod folder. For each replaced texture it builds new data, **appends** it to the end of an existing CAS archive, and **repoints** every reference to the old data at the new copy: TOC entries, bundle entries, integrity hashes and texture headers. The stock data stays where it was. Undoing is exact: truncate each CAS file back to its stock length and copy back the stock TOC files. FMT is used only to *read* the game's asset index. Everything written comes from our own code.

---

## 2. How NHL 24 stores its data

| Piece | What it is |
|---|---|
| `Data/layout.toc` | Top-level index of superbundles and install chunks. Never modified. |
| Superbundle TOCs (`Data/Gen4b/contentsb.toc`, `contentlaunchsb.toc`, ...) | Index files. Each has a **chunk table** and a list of **bundles**. These are what NHL24Kit rewrites. |
| CAS archives (`Data/Gen4b/superbundlelayout/nhl_installpackage_NN/cas_NN.cas`) | Large files holding the actual data: chunks, RES headers, EBX assets and bundle records. New data is appended here. |
| `package.mft` | Manifest. Never modified. |

### TOC chunk table
Each entry is a chunk ID (stored byte-reversed in the file) plus a 12-byte location:

```
[unk u8][patch u8][catalog u8][cas u8][offset u32 BE][size u32 BE]
```

`catalog` and `cas` name the archive (`nhl_installpackage_<catalog>/cas_<cas>.cas`). Offset and size are big endian.

### Bundles
A bundle groups the assets a part of the game loads together, such as the menu or gameplay. In the TOC, a bundle is a list of entries, each with an archive, offset and size, plus the positions of those fields in the TOC (`locationOfOffset`, `locationOfSize`). An entry with flag 0 reuses the previous entry's catalog and cas.

- `Entries[0]` is the **bundle record** itself, a binary blob stored in a CAS archive.
- The bundle record lists the bundle's EBX, RES and chunk assets. For every asset it stores a **SHA1** and, for chunks, a `logicalOffset` and `logicalSize` (the uncompressed size).
- The SHA1 positions (`SB_Sha1_Position`) are absolute positions in the CAS file.

### TOC signature
After changing a TOC, its header signature is rebuilt with FMT's `TOCFile.RebuildTOCSignatureOnly`.

FMT's cache validity depends only on `layout.toc` and the `package.mft` timestamp, which NHL24Kit never changes. So FMT's index keeps describing the stock data, which is still present because we only append. That makes reading the index safe on a modded game.

---

## 3. Anatomy of a texture

A texture is three linked pieces:

1. **EBX `TextureAsset`** (or `TextureArrayAsset`): the asset, named like `content/common/logo/stlouis/logo_stlouis_color`. It points at its RES.
2. **RES texture header** (about 130 bytes, stored compressed): describes the image and points at the chunk.
3. **Chunk**: the pixel data, compressed in Frostbite blocks.

### RES header layout (NHL PS4)

| Offset | Field |
|---|---|
| 0–7 | `mipOffsets[0..1]`, u32 little endian: compressed offsets where the next two mips start |
| then | type, pixelFormat, poolId, flags, width, height, depth, sliceCount, mipCount, firstMip, chunk ID (GUID), mipSizes[15] |
| 108 | `chunkSize` |
| then | assetNameHash, textureGroup |

Important flags: `Swizzle` (0x800) means the data is PS4-tiled; `Ps4Swizzle` is 0x801. `SrgbGamma` marks colour textures.

### FirstMip
Many textures have `firstMip > 0`, for example a 2048×2048 texture whose bundle chunk only holds mips 1 and smaller.
- The **TOC chunk table** points at a separate **full-resolution copy** holding all mips.
- The **bundle copy** is byte for byte `fullCopy[mipOffsets[0]:]`, the tail of the full copy.
- `mipOffsets` refer to positions in the **full copy**.

---

## 4. Converting the image

Input can be PNG, JPG, BMP or DDS, any size. Done on the CPU with DirectXTex (`NHL24PS4TextureImporter.ConvertWithMips`):

1. Load the image and take its top mip.
2. Decompress if needed.
3. Resize to the texture's size **at FirstMip** (`width >> firstMip`).
4. Generate the full mip chain down to 1×1 (`mipCount - firstMip` levels).
5. Compress to the texture's format (BC1, BC3, BC5 or BC7, sRGB when the texture is sRGB), using parallel CPU compression. FMT's GPU path fails on BC7, so it is only a fallback.

A DDS that already matches the format and mip chain exactly is used as is.

### PS4 tiling
Every texture with the `Swizzle` bit is tiled (`Ps4Tiling.Tile` / `Untile`):
- Elements are 4×4 BC blocks, or single pixels for uncompressed formats.
- Elements are grouped in **8×8-element tiles**, stored tile after tile.
- Inside a tile the 64 elements are in Morton order: `x = bits 0,2,4` and `y = bits 1,3,5` of the index.
- Small mips are padded up to a whole tile, which is why a 512×512 BC3 chain takes 354,304 bytes, not 349,524.

`BuildChunk` writes mips FirstMip..MipCount-1 back to back, each tiled separately.

### Texture arrays (logos)
Logo textures are `TT_2dArray`, for example `branding_jersey_<brand>_<team>_<set>_color_array` (plus `_normal_array` and `_coeff_array`, and the pant versions). Facts verified by decoding:
- **Depth** is the number of layers: 5 for the jersey (note, NHL shield, adidas, End Plastic Waste, Stifel on the Blues), 2 for the pants.
- Storage is **mip-major**: mip 0 of every layer, then mip 1 of every layer, and so on, each mip tiled on its own. Layer *l* of mip *m* starts at `sum(mipSize[k] * layers for k < m) + l * mipSize[m]`.
- The separate 2D logo textures (`branding_jersey_..._0_color` and so on) are **not used** by the game; logos come from the arrays.

To replace layers, NHL24Kit copies each kept layer's mip chain out, builds the new layer like a 2D texture, and writes its mips back into those positions.

---

## 5. Compression

Chunks are a sequence of Frostbite blocks:

```
[u32 BE uncompressed size][u16 BE code][u16 BE compressed size][payload]
```

The high byte of `code` is the compression type, and its low nibble holds bits 16–19 of the compressed size. NHL 24 uses **Oodle Kraken** (type 0x11, code 0x1170). FMT writes Leviathan (0x19), which is not what the game uses, so NHL24Kit always re-encodes with Kraken (`FrostbiteBlocks.Encode`).

Keeping the original block layout matters, because `mipOffsets` point at block starts:
1. Try the original block splits **padded** to the original compressed sizes, so every offset stays identical.
2. Otherwise, keep the original uncompressed splits without padding. Every mip still starts on a block, at a new compressed offset.
3. Otherwise, use a natural layout.

When blocks move, `mipOffsets` are translated through each block's uncompressed position (`FrostbiteBlocks.TranslateOffset`), and the RES header is patched (section 7).

---

## 6. Finding every reference

This is where most of the bugs came from. For each texture, all of these must be found:

1. **TOC chunk table entries** with the chunk's ID that point at its stock location.
2. **Bundle chunk entries** for that chunk ID, found by reading the bundle records (FMT `TOCFile` with `ReadCasBundlesFromCasFiles`), with their TOC offset and size positions and their SHA1 position.
3. **Variants**: chunk table entries with the same ID pointing somewhere else. For FirstMip textures, that is the full-resolution copy.
4. **Duplicate bundle copies**: a bundle can keep its **own copy** of the same chunk at a different CAS offset. Found through the chunk's bundle list in FMT's index (`chunk.Bundles`); each identical copy is replaced too.
5. **Every RES copy**: each bundle that lists the chunk can carry its **own stored copy** of the texture header (the menu bundle and the gameplay bundle do). All are found by name in those bundles and all are patched.

Matching is always by **chunk ID and stock location together**. Matching by location alone also hits other textures that share identical data: `pant_base_coeff` had 747 references across other teams.

---

## 7. Writing

For each replaced texture, in this order:

1. **Full-resolution copy** (FirstMip > 0 only): build it with all mips, compress it like its original, append it, and repoint its chunk table entries.
2. **Bundle copy**: if the original bundle copy was the tail of the full copy, cut the new one from the new full copy at the translated `mipOffsets[0]`. Otherwise build it directly.
3. **Append** the chunk to its CAS and repoint every reference: write the new offset and size, big endian, at each TOC position.
4. **Duplicate bundle copies**: append and repoint them too.
5. **SHA1s**: queue new SHA1s for every bundle entry repointed:
   - chunk SHA1 = SHA1 of the compressed chunk, **or of the compressed full copy** for FirstMip textures
   - RES SHA1 = SHA1 of the compressed RES
6. **RES headers**: in every stored copy, check the old `mipOffsets` (and `chunkSize` when it held the compressed size), write the new values, recompress, append, and repoint.

Then **Commit**:

7. For every bundle with queued changes, read its record, patch the SHA1s (and the uncompressed chunk size, used by database writes), checking the old values first. Append the record and repoint the bundle's `Entries[0]` in the TOC.
8. Write each changed TOC and rebuild its signature.

### Undo (Restore)
The first time a file is touched, `KitState` records its stock length (`<game>-kit\stock.json`) and copies the stock TOC (`<game>-kit\stock_toc\`). It never overwrites those. Restore truncates each CAS back to its stock length and copies the TOCs back. Applies are incremental: an apply writes only what is new or changed on top of the game as it is, and assets are always looked up by their stock location (read from the stock TOC copies), so something replaced before is found again and replaced on top. A rebuild restores first and writes the whole list. See [APPLYING.md](APPLYING.md).

---

## 8. Rules learned in game

Each of these was found because something looked wrong in game.

| # | Rule | Symptom when broken |
|---|---|---|
| 1 | Skaters use the `*_base_*` textures (`jersey_..._base_color`, `pant_..._base_normal`, ...). `jersey_color` / `pant_color` are not used by skaters. | "Didn't change" |
| 2 | Generate every mip; the game draws small mips at normal distances. | Replacement only visible up close |
| 3 | Re-tile for PS4 (FMT stored the DDS header and never tiled). | Scrambled texture |
| 4 | Kraken compression, not Leviathan. | Ignored or broken data |
| 5 | Update the TOC chunk table, not only the bundle. | Old texture keeps loading |
| 6 | FirstMip textures need their full-resolution copy, and the bundle copy must be its tail. | Black or patchy gameplay |
| 7 | Translate `mipOffsets` when blocks move. | Black or patchy gameplay |
| 8 | Patch the SHA1 in every bundle record. | Data rejected |
| 9 | **Patch every RES copy** (menu and gameplay bundles each have one). | Fine in menus, **black jerseys in gameplay** |
| 10 | Keep base coeff maps stock (NHL 26 base coeffs turn gameplay jerseys black even though they look the same). | Black jerseys |
| 11 | **Fabric regions**: `jersey_..._base_color` alpha is a region index, not transparency. NHL 24 uses 51 (body), 102 (shoulder band, dimpled fabric), 153 (underarm patches), 204 (crest). NHL 26 uses 64/127/191/255 in a different layout. Keep the game's own alpha (`Alpha: auto`); `nodimples` also maps 102 to 51. | Dimples over the whole jersey |
| 12 | Logos come from the **arrays**, not the 2D logo textures. | "None of the branding changed" |
| 13 | Logo coeff layers from NHL 26 are noisy. Build clean ones from the logo's alpha: inside (128, 54, 225, 0), outside (126, 0, 255, 0). | Grainy, glittery logos; dark NHL shield |
| 14 | Replace every **duplicate bundle copy** of a chunk. | Gameplay socks stayed stock |
| 15 | The number font sheet also holds the number backing; match it as transparent-over-black. | Green number backings, brown numbers |

---

## 9. Using it

### From the app
- **NHL24Kit → Match textures** converts a whole NHL 26 uniform set, with all the rules above applied.
- **NHL24Kit → Replace any texture…** lets you browse every texture in the game, pick an image for it (or for each logo layer), and save it to the "Custom textures" mod list entry.
- **Apply to game** writes what is new or changed. **Rebuild from stock** starts over from stock and writes the whole list (needed to take things out). **Restore stock game** undoes everything.

### From the worker
The worker runs from `%LOCALAPPDATA%\NHL24Kit\runtime` (set up from your FMT install by the app).

```
NHL24Kit.Worker.exe find     <game> [words...]          list textures (all of them with no words)
NHL24Kit.Worker.exe texture  <game> <slot> <out dir>    dump one texture's original image(s) and info
NHL24Kit.Worker.exe refs     <game> <folder> <out dir>  dump every texture in a folder
NHL24Kit.Worker.exe update   <game> <plan.json>         write what is new or changed (incremental)
NHL24Kit.Worker.exe apply    <game> <plan.json>         restore stock, then write the plan (rebuild)
NHL24Kit.Worker.exe restore  <game>                     back to stock
NHL24Kit.Worker.exe inspect  <game> <slot>              header fields and every bundle record of a texture
NHL24Kit.Worker.exe where    <game> <slot>              which bundles list a texture, and each bundle's chunk copy
```

`<game>` is the extracted game folder (the one with `package.mft`). Close the game first: the worker refuses to write while `shadps4.exe` or `nhl24.exe` runs.

### Plan format

```json
{
  "replacements": [
    { "slot": "content/common/logo/stlouis/logo_stlouis_color", "source": "C:\\mods\\blues_logo.png" },
    { "slot": "content/character/skater/league/nhl/stlouis/adidas/home/jersey_adidas_stlouis_home_base_color",
      "source": "C:\\mods\\jersey.dds", "alpha": "nodimples" },
    { "slot": "content/character/skater/league/nhl/stlouis/adidas/home/branding_jersey_adidas_stlouis_home_color_array",
      "layers": { "0": "C:\\mods\\note.png", "4": "C:\\mods\\sponsor.png" } },
    { "slot": "content/character/skater/league/nhl/stlouis/adidas/home/branding_jersey_adidas_stlouis_home_coeff_array",
      "layers": { "0": "coeff:C:\\mods\\note.png", "4": "coeff:C:\\mods\\sponsor.png" } }
  ],
  "legacy": [
    { "name": "loc/eng_us.db", "source": "C:\\mods\\eng_us.db" }
  ]
}
```

- `alpha`: `auto` (default; keep the game's fabric regions when the source's differ), `nodimples`, or `source`.
- `layers`: array layer index → image. Layers not listed keep the original.
- `coeff:<image>`: build a clean logo coeff layer from that logo's shape.
- `legacy`: whole legacy files such as databases and localization. This uses the same writer, extended to rebuild shared data chunks and their index (see `LegacyWriter.cs`).

---

## 10. Troubleshooting

| What you see | Check |
|---|---|
| Nothing changed | Is it the texture the game draws? Skaters use `_base_` textures, logos use arrays. Use `where`/`inspect` and test with an obvious checkerboard. |
| Changed in menus, not in gameplay | Every RES copy and duplicate bundle copy patched? (rules 9, 14) |
| Black or patchy in gameplay | Mip offsets, full copy and tail (rules 6, 7); base coeff (rule 10). |
| Only changes up close | Mips missing (rule 2). |
| Dimples, odd fabric | Base colour alpha (rule 11). |
| Grainy logos | Coeff layers (rule 13). |
| Worker says "not where it was read from" or "does not hold the expected SHA1" | The TOC or bundle changed underneath it. Restore, then apply again. |

Test images that helped: `tools\TEST_magenta_checker_2048.png` (obvious) and `tools\TEST_noisy_checker_2048.png` (forces a block layout change, so it exercises the offset and RES rules).
