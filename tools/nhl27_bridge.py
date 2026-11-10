"""Bridge from NHL Suite to the NHL 27 texture reader in D:\\Tools\\NHL27TextureViewer.

The viewer already reads NHL 27's PS5 archives (TOC/CAS, exact names, PS5
de-swizzle, BC1/3/5/7 decode) and keeps a full-game scan cache. This script
reuses its modules instead of re-implementing them:

  index  --viewer DIR --out FILE.json
      Reads the viewer's cache (cache/*.pkl) and writes one record per named,
      resolved texture. No game access; takes a few seconds.

  decode --viewer DIR --game ROOT --jobs FILE.json [--workers N]
      Decodes the top mip of each job to PNG using all CPU cores. A job is
      {"name", "pkg", "cas", "tree", "offset", "size", "width", "height",
       "format", "out"}. Prints one line per job: "OK <out>" or "ERR <name> <why>".

Nothing here writes to the game folder.
"""
import argparse
import glob
import json
import os
import pickle
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed


def _use_viewer(viewer):
    if viewer not in sys.path:
        sys.path.insert(0, viewer)


def cmd_index(args):
    seen = {}
    for path in glob.glob(os.path.join(args.viewer, "cache", "*.pkl")):
        if os.path.getsize(path) < 100:
            continue
        with open(path, "rb") as f:
            rows = pickle.load(f)
        for r in rows:
            name = r.get("name") or ""
            entry = r.get("entry")
            if not name or "/" not in name or r.get("generic_name") or not entry or not entry.get("magic_ok"):
                continue
            if name in seen:
                continue
            seen[name] = {
                "name": name,
                "width": r["width"], "height": r["height"],
                "slices": r.get("slicecount", 1), "mips": r.get("mipcount", 1),
                "firstMip": r.get("firstmip", 0),
                "format": r.get("guessed_format") or "",
                "pkg": entry["install_package"], "cas": entry["cas"],
                "tree": entry.get("tree", entry.get("source_root", "Data")),
                "offset": entry["offset"], "size": entry["size"],
                "exact": bool(r.get("name_exact")),
            }
    out = sorted(seen.values(), key=lambda x: x["name"])
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, separators=(",", ":"))
    print(f"OK {len(out)} textures")


_GAME = None
_VIEWER = None


def _init(viewer, game):
    global _GAME, _VIEWER
    _VIEWER, _GAME = viewer, game
    _use_viewer(viewer)


def _decode_one(job):
    from frostbite import catalog, cas_reader, image_decode
    from PIL import Image
    try:
        cas = catalog.cas_path(_GAME, job["pkg"], job["cas"], job.get("tree", "Data"))
        if cas is None:
            return "ERR", job["name"], "cas file missing"
        data = cas_reader.decompress_region(cas, job["offset"], job["size"])
        slices = max(1, int(job.get("slices", 1)))
        slice_outs = job.get("sliceOuts") or []
        if slices > 1 and len(slice_outs) != slices:
            return "ERR", job["name"], "array slice output list is incomplete"
        if len(data) % slices:
            return "ERR", job["name"], f"{len(data)} payload bytes do not divide into {slices} slices"
        stride = len(data) // slices
        rgba = None
        for layer in range(slices):
            part = data[layer * stride:(layer + 1) * stride]
            decoded = image_decode.decode_top_mip(job["width"], job["height"], job["format"], part)
            if decoded is None:
                return "ERR", job["name"], f"no decoder for {job['format']} or truncated slice {layer}"
            rgba = decoded
            output = slice_outs[layer] if slices > 1 else job["out"]
            os.makedirs(os.path.dirname(output), exist_ok=True)
            tmp = output + ".tmp.png"
            Image.fromarray(decoded, "RGBA").save(tmp)
            os.replace(tmp, output)
        if slices > 1:
            os.makedirs(os.path.dirname(job["out"]), exist_ok=True)
            tmp = job["out"] + ".tmp.png"
            Image.fromarray(rgba, "RGBA").save(tmp)
            os.replace(tmp, job["out"])
        return "OK", job["out"], ""
    except Exception as e:  # report and keep going
        return "ERR", job["name"], str(e).replace("\n", " ")


def cmd_decode(args):
    with open(args.jobs, "r", encoding="utf-8") as f:
        jobs = json.load(f)
    workers = args.workers or os.cpu_count() or 4
    with ProcessPoolExecutor(max_workers=min(workers, max(1, len(jobs))),
                             initializer=_init, initargs=(args.viewer, args.game)) as ex:
        futures = [ex.submit(_decode_one, j) for j in jobs]
        for fut in as_completed(futures):
            status, what, why = fut.result()
            print(f"{status} {what} {why}".rstrip(), flush=True)


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    a = sub.add_parser("index")
    a.add_argument("--viewer", required=True)
    a.add_argument("--out", required=True)
    d = sub.add_parser("decode")
    d.add_argument("--viewer", required=True)
    d.add_argument("--game", required=True)
    d.add_argument("--jobs", required=True)
    d.add_argument("--workers", type=int, default=0)
    args = p.parse_args()
    _use_viewer(args.viewer)
    {"index": cmd_index, "decode": cmd_decode}[args.cmd](args)


if __name__ == "__main__":
    main()
