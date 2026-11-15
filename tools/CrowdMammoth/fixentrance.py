import sys, numpy as np
from PIL import Image
ROOT = sys.argv[1]; sys.argv[1] = ROOT + "/crowd"
exec(open(ROOT + "/crowd/mammoth.py", encoding="utf-8-sig").read().split("LAYOUTS = {")[0])
img = np.asarray(Image.open(ROOT + "/arena/az/entrance_arizona_logo_color_0.png").convert("RGB"), np.float32).copy()
H, W = img.shape[:2]; yy, xx = np.mgrid[0:H, 0:W]; r = np.hypot(xx - W / 2, yy - H / 2).astype(int)
inside = (xx >= 190) & (xx < 855) & (yy >= 110) & (yy < 790)
prof = np.zeros((r.max() + 1, 3), np.float32)
for k in range(r.max() + 1):
    m = (r == k) & ~inside
    if m.any(): prof[k] = np.median(img[m], axis=0)
for k in range(1, len(prof)):
    if not prof[k].any(): prof[k] = prof[k - 1]
for k in range(len(prof) - 2, -1, -1):
    if not prof[k].any(): prof[k] = prof[k + 1]
img[inside] = prof[r[inside]]
w = 600; h = int(w * logo_a.shape[0] / logo_a.shape[1]); stamp(img, (W // 2 - w // 2, H // 2 - h // 2 - 10, W // 2 + w // 2, H // 2 + h // 2 - 10))
Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(ROOT + "/arena/fix/entrance_arizona_logo_color.png")
Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).resize((256, 256)).save(ROOT + "/arena/entrance_check.png")
