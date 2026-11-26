"""Read-only: extract one legacy file from NHL 27's PS5 dump.
NHL 27 legacy index (a globals.toc chunk): header u32 2, u32 0x50, ..., u32 count @12; 28-byte records from 0x60
(u64 name hash, u32 x2 stored range, u32 raw offset, u32 raw size, u32 chunk index); chunk id table (16 bytes each)
at header u32 @48; string pool at header u32 @56; name offsets (u32, into the pool) at header u32 @64.
Usage: python -I extract_loc27.py <viewer dir> <app0 root> <index.bin> <out dir> <name>...
"""
import os, struct, sys
viewer, root, index_path, out = sys.argv[1:5]
names = sys.argv[5:]
sys.path.insert(0, viewer)
from frostbite.toc import SbToc
from frostbite.cas_reader import decompress_region
from frostbite.catalog import cas_path

d = open(index_path, "rb").read()
count = struct.unpack_from("<I", d, 12)[0]
ids_at, pool_at, names_at = (struct.unpack_from("<I", d, o)[0] for o in (48, 56, 64))
chunks = {}
for tree in ("Data", "Patch"):
    p = os.path.join(root, tree, "Ps5", "globals.toc")
    if os.path.isfile(p):
        chunks.update(SbToc(p).guid_to_entry_map())
os.makedirs(out, exist_ok=True)
for i in range(count):
    no = struct.unpack_from("<I", d, names_at + 4 * i)[0]
    name = d[pool_at + no:d.find(b"\x00", pool_at + no)].decode()
    if name not in names:
        continue
    _, _, s0, s1, ro, rs, ci = struct.unpack_from("<7I", d, 0x60 + 28 * i)
    cid = d[ids_at + 16 * ci:ids_at + 16 * ci + 16]
    rec = chunks.get(cid) or chunks.get(cid[::-1])
    if rec is None:
        print(name, "chunk", cid.hex(), "not found"); continue
    raw = decompress_region(cas_path(root, rec["install_package"], rec["cas"], rec["tree"]), rec["offset"], rec["size"])
    blob = raw[ro:ro + rs]
    open(os.path.join(out, os.path.basename(name)), "wb").write(blob)
    print(f"{name}: chunk {ci} ({rec['tree']}) raw {len(raw)}, file {ro}+{rs} -> {len(blob)} bytes, starts {blob[:16].hex()}")
