"""Read-only: find NHL 27's legacy file index (the chunk listing names like loc/eng_us.db) in the PS5 dump's
globals.toc chunks and extract one legacy file to an output folder. Uses the NHL 27 texture viewer's own PS5 readers.
Usage: python -I find_loc27.py <viewer dir> <nhl27 app0 root> <out dir> [name-substring]
"""
import os
import struct
import sys

viewer, root, out = sys.argv[1], sys.argv[2], sys.argv[3]
want = sys.argv[4] if len(sys.argv) > 4 else "eng_us"
sys.path.insert(0, viewer)
from frostbite.toc import SbToc  # noqa: E402
from frostbite.cas_reader import decompress_region, DecodeError  # noqa: E402
from frostbite.catalog import cas_path  # noqa: E402

os.makedirs(out, exist_ok=True)
chunks = {}  # guid bytes (as stored in the toc) -> record; Patch wins
for tree in ("Data", "Patch"):
    p = os.path.join(root, tree, "Ps5", "globals.toc")
    if not os.path.isfile(p):
        continue
    t = SbToc(p)
    for g, rec in t.guid_to_entry_map().items():
        chunks[g] = rec
print(f"globals chunks: {len(chunks)}")


def read(rec):
    path = cas_path(root, rec["install_package"], rec["cas"], rec["tree"])
    if path is None:
        return None
    try:
        return decompress_region(path, rec["offset"], rec["size"])
    except (DecodeError, OSError):
        return None


needle = want.encode()
index = None
for g, rec in chunks.items():
    if rec["size"] > 8_000_000:
        continue
    data = read(rec)
    if data and needle in data and b".db\x00" in data:
        names = [s for s in data.split(b"\x00") if needle in s]
        print(f"index candidate {g.hex()} size {len(data)}: {names[:8]}")
        index = (g, data)
        break
if index is None:
    print("no index chunk lists", want)
    sys.exit(2)

g, data = index
open(os.path.join(out, "legacy_index.bin"), "wb").write(data)
# NHL 24's record: u64 name offset, u64 stored start, u64 stored end, u64 raw offset, u64 raw size, 16-byte chunk id.
pos = data.find(needle)
name_start = data.rfind(b"\x00", 0, pos) + 1
name_off = name_start
target = struct.pack("<Q", name_off)
for at in range(0, len(data) - 56, 8):
    if data[at:at + 8] == target:
        so, se, ro, rs = struct.unpack_from("<4Q", data, at + 8)
        cid = data[at + 40:at + 56]
        name = data[name_start:data.find(b"\x00", name_start)].decode()
        print(f"record @{at}: {name} stored {so}-{se} raw {ro}+{rs} chunk {cid.hex()}")
        # The TOC guid table stores ids in a different byte order in some builds: try both.
        rec = chunks.get(cid) or chunks.get(cid[::-1])
        if rec is None:
            for k in chunks:
                if sorted(k) == sorted(cid):
                    rec = chunks[k]
                    break
        if rec is None:
            print("chunk not in globals.toc")
            sys.exit(3)
        raw = read(rec)
        blob = raw[ro:ro + rs] if rs else raw
        dst = os.path.join(out, os.path.basename(name))
        open(dst, "wb").write(blob)
        print(f"wrote {dst} ({len(blob)} bytes)")
        sys.exit(0)
print("record for the name was not found in the index layout")
sys.exit(4)
