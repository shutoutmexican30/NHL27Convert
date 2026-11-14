# Arizona -> Utah Mammoth crowd clothing, painted into NHL 24's own Arizona sheets (layout kept, safe class).
# Step 1: recolour team colours (shading kept): Coyotes red -> Mammoth black, sand -> Mountain Blue, Kachina green ->
#         darker Mountain Blue. White, black, jeans, shoes, skin untouched (shoes/jeans tiles excluded explicitly).
# Step 2: per 512 tile, erase the chest crest (blob that differs from the body colour inside the crest zone) and the
#         shoulder patches, then stamp the Mammoth logo (NHL 27 logo_utah colour + alpha) fitted to the crest box.
import sys, os, json
import numpy as np
from PIL import Image

d = sys.argv[1]
out = f"{d}/mammoth"; os.makedirs(out, exist_ok=True)

ROCK_BLACK = np.array([0x1B, 0x1B, 0x1B], np.float32)
MOUNTAIN_BLUE = np.array([0x6C, 0xAC, 0xE4], np.float32)
DEEP_BLUE = np.array([0x4F, 0x86, 0xBC], np.float32)

logo_c = np.asarray(Image.open(f"{d}/ut/logo_utah_color.png").convert("RGB"), np.float32)
logo_a = np.asarray(Image.open(f"{d}/ut/logo_utah_alpha.png").convert("L"), np.float32) / 255
ys, xs = np.where(logo_a > 0.05)
logo_c = logo_c[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
logo_a = logo_a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]

def hsv(img):
    r, g, b = img[..., 0] / 255, img[..., 1] / 255, img[..., 2] / 255
    mx, mn = np.maximum(np.maximum(r, g), b), np.minimum(np.minimum(r, g), b)
    dlt = mx - mn + 1e-6
    h = np.where(mx == r, ((g - b) / dlt) % 6, np.where(mx == g, (b - r) / dlt + 2, (r - g) / dlt + 4)) * 60
    s = np.where(mx > 0, (mx - mn) / (mx + 1e-6), 0)
    return h, s, mx

def recolour(img, keep):
    """Blend-aware: a team-coloured pixel is (1-a)*grey + a*colour; the colour part is swapped, the grey part kept,
    so red->white fades become black->white fades. img float RGB 0..255; keep: pixels never touched."""
    h, s, v = hsv(img)
    grey = (v * 255)[..., None] * np.ones(3, np.float32)
    res = img.copy()
    def swap(hmask, s_full, target, ref_v, s_min):
        a = np.clip((s - s_min) / (s_full - s_min), 0, 1)
        m = hmask & (a > 0) & ~keep
        shade = np.clip(v[m] / ref_v, 0.35, 1.5)[:, None]
        res[m] = (1 - a[m, None]) * grey[m] + a[m, None] * np.clip(target[None, :] * shade, 0, 255)
    red_h = ((h < 22) | (h > 325)) & (v > 0.1)
    sand_h = (h >= 25) & (h <= 60) & (v > 0.5) & (s < 0.5)
    green_h = (h >= 100) & (h <= 190) & (v > 0.1)
    swap(red_h, 0.7, ROCK_BLACK, 0.5, 0.06)
    swap(sand_h, 0.25, MOUNTAIN_BLUE, 0.88, 0.05)
    swap(green_h, 0.45, DEEP_BLUE, 0.3, 0.12)
    return res
def body_colour(tile, box):
    x0, y0, x1, y1 = box
    border = np.concatenate([tile[y0, x0:x1], tile[y1 - 1, x0:x1], tile[y0:y1, x0], tile[y0:y1, x1 - 1]])
    return np.median(border, axis=0)

def blob_box(tile, box, body, thr=55):
    x0, y0, x1, y1 = box
    region = tile[y0:y1, x0:x1]
    diff = np.abs(region - body[None, None, :]).sum(axis=2) > thr
    if diff.sum() < 30: return None, None
    ys, xs = np.where(diff)
    return (x0 + xs.min(), y0 + ys.min(), x0 + xs.max() + 1, y0 + ys.max() + 1), diff

def erase(tile, box, body):
    x0, y0, x1, y1 = box
    # body colour with a gentle vertical shading taken from the columns just outside the box
    left = tile[y0:y1, max(0, x0 - 3)].astype(np.float32); right = tile[y0:y1, min(tile.shape[1] - 1, x1 + 2)].astype(np.float32)
    col = (left + right) / 2
    for i, x in enumerate(range(x0, x1)):
        t = i / max(1, x1 - x0 - 1)
        tile[y0:y1, x] = left * (1 - t) + right * t
    _ = col

def stamp(tile, box, scale=1.0):
    x0, y0, x1, y1 = box
    bw, bh = x1 - x0, y1 - y0
    lh, lw = logo_a.shape
    k = min(bw / lw, bh / lh) * scale
    w, h = max(4, int(lw * k)), max(4, int(lh * k))
    c = np.asarray(Image.fromarray(logo_c.astype(np.uint8)).resize((w, h), Image.LANCZOS), np.float32)
    a = np.asarray(Image.fromarray((logo_a * 255).astype(np.uint8)).resize((w, h), Image.LANCZOS), np.float32)[..., None] / 255
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    X, Y = cx - w // 2, cy - h // 2
    X, Y = max(0, X), max(0, Y)
    w, h = min(w, tile.shape[1] - X), min(h, tile.shape[0] - Y)
    tile[Y:Y + h, X:X + w] = tile[Y:Y + h, X:X + w] * (1 - a[:h, :w]) + c[:h, :w] * a[:h, :w]

# Male jersey layout (one 512 tile): keep-out rects and logo zones, tile pixels.
MALE_KEEP = [(372, 0, 512, 142), (388, 388, 512, 512), (0, 392, 128, 512)]
MALE_CREST = (165, 292, 350, 462)
MALE_PATCHES = [(160, 222, 250, 292), (290, 222, 372, 292)]
LOGO_W = 128

def dilate(m, r):
    out = m.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out |= np.roll(np.roll(m, dy, 0), dx, 1)
    return out

def components(mask):
    """4-connected labels of a small bool mask (pure python BFS)."""
    H, W = mask.shape; lab = np.zeros((H, W), np.int32); n = 0
    for y0 in range(H):
        for x0 in range(W):
            if not mask[y0, x0] or lab[y0, x0]: continue
            n += 1; stack = [(y0, x0)]; lab[y0, x0] = n
            while stack:
                y, x = stack.pop()
                for yy, xx in ((y + 1, x), (y - 1, x), (y, x + 1), (y, x - 1)):
                    if 0 <= yy < H and 0 <= xx < W and mask[yy, xx] and not lab[yy, xx]:
                        lab[yy, xx] = n; stack.append((yy, xx))
    return lab, n

def erase_mask(tile, zone, thr, max_frac, inner_only=True):
    """Erase the logo blobs of a zone: pixels unlike the zone's body colour, in blobs that do not touch the zone
    border (so a background edge crossing the zone is never filled), filled with the body colour."""
    x0, y0, x1, y1 = zone
    body = body_colour(tile, zone)
    region = tile[y0:y1, x0:x1]
    diff = np.abs(region - body[None, None, :]).sum(axis=2) > thr
    lab, n = components(diff)
    keepm = np.zeros_like(diff)
    for k in range(1, n + 1):
        m = lab == k
        ys, xs = np.where(m)
        if inner_only and (ys.min() == 0 or xs.min() == 0 or ys.max() == diff.shape[0] - 1 or xs.max() == diff.shape[1] - 1): continue
        if m.sum() < 6: continue
        keepm |= m
    if keepm.sum() < 20 or keepm.mean() > max_frac: return None
    sizes = [(int((lab == k).sum()), k) for k in range(1, n + 1) if (keepm & (lab == k)).any()]
    big = lab == max(sizes)[1]
    by, bx = np.where(big)
    region[dilate(keepm, 2)] = body
    ys, xs = np.where(keepm)
    return (x0 + xs.min(), y0 + ys.min(), x0 + xs.max() + 1, y0 + ys.max() + 1), (x0 + bx.min(), y0 + by.min(), x0 + bx.max() + 1, y0 + by.max() + 1)
LAYOUTS = {
    # keep-out rects, crest zone, patch zones, logo width (per 512 tile)
    "jersey": (MALE_KEEP, MALE_CREST, MALE_PATCHES, 128),
    "hoody": ([(352, 0, 512, 158), (356, 354, 512, 512)], (165, 185, 350, 425), [(105, 215, 182, 298), (332, 215, 410, 298)], 104),
    "tshirt": ([(354, 0, 512, 156), (354, 354, 512, 512)], (150, 268, 356, 500), [], 104),
}

def do_tile(tile, report, name, layout):
    keep_rects, crest_zone, patch_zones, logo_w = LAYOUTS[layout]
    keep = np.zeros(tile.shape[:2], bool)
    for x0, y0, x1, y1 in keep_rects: keep[y0:y1, x0:x1] = True
    tile = recolour(tile, keep)
    crest = erase_mask(tile, crest_zone, 60, 0.7)
    patches = [p for z in patch_zones if (p := erase_mask(tile, z, 60, 0.45))]
    if crest:
        _, (bx0, by0, bx1, by1) = crest          # the largest blob = the old crest
        cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2
        w = int(min(logo_w * 1.15, max(logo_w * 0.6, (bx1 - bx0) * 1.15))); h = int(w * logo_a.shape[0] / logo_a.shape[1])
        stamp(tile, (int(cx - w / 2), int(cy - h / 2), int(cx + w / 2), int(cy + h / 2)), 1.0)
    report.append(f"{name}: crest {crest[0] if crest else None}, patches {[p[0] for p in patches]}")
    return tile

def do_male_tile(tile, report, name):
    return do_tile(tile, report, name, "jersey")

def process(src, dst, layout, report):
    img = np.asarray(Image.open(src).convert("RGB"), np.float32).copy()
    H, W = img.shape[:2]
    if layout == "paint":
        h, s_, v = hsv(img); paint = (s_ > 0.55) & (((h < 15) | (h > 340)) | ((h > 100) & (h < 190)))
        img[paint] = np.clip(MOUNTAIN_BLUE[None, :] * np.clip(v[paint] / 0.6, 0.4, 1.4)[:, None], 0, 255); report.append(f"{os.path.basename(src)}: face paint {int(paint.sum())} px")
    elif layout == "recolour":
        img = recolour(img, np.zeros(img.shape[:2], bool)); report.append(f"{os.path.basename(src)}: recolour only")
    else:
        for ty in range(0, H, 512):
            for tx in range(0, W, 512):
                img[ty:ty + 512, tx:tx + 512] = do_tile(img[ty:ty + 512, tx:tx + 512].copy(), report, f"{os.path.basename(src)} tile {tx // 512},{ty // 512}", layout)
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(dst)

def layout_of(n):
    if "_jersey_" in n: return "jersey"
    if "_hoody_" in n: return "hoody"
    if "_tshirt_" in n: return "tshirt"
    if "prop_male_hat" in n or "prop_team" in n: return "recolour"
    if "crowd_head_" in n and n.endswith("_color"): return "paint"
    return None

if __name__ == "__main__":
    import glob
    report = []
    only = sys.argv[2] if len(sys.argv) > 2 else ""
    for f in sorted(glob.glob(f"{d}/az/*_color_0.png")):
        n = os.path.basename(f)[:-6]
        lay = layout_of(n)
        if not lay or only not in n: continue
        process(f, f"{out}/{n.replace('arizona', 'utah')}.png", lay, report)
    open(f"{out}/report.txt", "w").write("\n".join(report)); print(len(report), "tiles/sheets")

