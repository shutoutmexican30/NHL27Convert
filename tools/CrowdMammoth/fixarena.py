import sys, numpy as np
from PIL import Image, ImageDraw, ImageFont
ROOT = sys.argv[1]; sys.argv[1] = ROOT + "/crowd"
exec(open(ROOT + "/crowd/mammoth.py", encoding="utf-8-sig").read().split("LAYOUTS = {")[0])
A = ROOT + "/arena"
def load(n): return np.asarray(Image.open(f"{A}/az/{n}_color_0.png").convert("RGB"), np.float32).copy()
def save(img, n): Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(f"{A}/fix/{n}_color.png")
def none_keep(img): return np.zeros(img.shape[:2], bool)
def relogo(img, box, frac=0.9):
    r = erase_mask(img, box, 60, 0.95, inner_only=False)
    if r:
        bx0, by0, bx1, by1 = r[0]; w = int((bx1 - bx0) * frac); h = int(w * logo_a.shape[0] / logo_a.shape[1])
        h = min(h, int((by1 - by0) * 1.0)); w = int(h * logo_a.shape[1] / logo_a.shape[0]); cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2
        stamp(img, (int(cx - w / 2), int(cy - h / 2), int(cx + w / 2), int(cy + h / 2)))
    return r
def text(img, box, s, colour=(235, 235, 235)):
    x0, y0, x1, y1 = box; im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)); d = ImageDraw.Draw(im)
    size = y1 - y0
    while size > 6:
        f = ImageFont.truetype(r"C:\Windows\Fonts\arialbd.ttf", size); w = d.textlength(s, font=f)
        if w <= x1 - x0: break
        size -= 2
    d.text(((x0 + x1 - w) / 2, y0), s, font=f, fill=colour); return np.asarray(im, np.float32).copy()
# crest swaps
for n in ("entrance_arizona_logo", "cloth_tarp_arizona", "cloth_tarp_arizona_02"):
    img = load(n); H, W = img.shape[:2]; print(n, relogo(img, (int(W * .12), int(H * .12), int(W * .88), int(H * .88)))); save(img, n)
img = load("logos_conference_arizona"); H, W = img.shape[:2]; cw, ch = W // 4, H // 4
for i in range(4):
    for j in range(4): relogo(img, (i * cw + 4, j * ch + 4, (i + 1) * cw - 4, (j + 1) * ch - 4))
save(img, "logos_conference_arizona")
# wall text
img = load("wall_branding_arizona_black"); H, W = img.shape[:2]
img[:] = np.median(img.reshape(-1, 3), axis=0); img = text(img, (int(W * .05), int(H * .3), int(W * .95), int(H * .72)), "DELTA CENTER"); save(img, "wall_branding_arizona_black")
# painted metal: recolour, erase the ARIZONA ARENA block, write DELTA CENTER
img = load("metal_painted_arizona"); img = recolour(img, none_keep(img)); H, W = img.shape[:2]
r = erase_mask(img, (int(W * .08), int(H * .42), int(W * .70), int(H * .96)), 60, 0.95, inner_only=False); print("metal", r)
if r: x0, y0, x1, y1 = r[0]; img = text(img, (x0, y0 + (y1 - y0) // 3, x1, y0 + (y1 - y0) * 2 // 3), "DELTA CENTER")
save(img, "metal_painted_arizona")
# recolour-only pieces (+ erase small coyote marks left to the recolour)
for n in ("banner_arizona_roof", "banner_coyotes_branding", "scoreboard_metalpainted_arizona"):
    img = load(n); img = recolour(img, none_keep(img)); save(img, n)
print("done")

