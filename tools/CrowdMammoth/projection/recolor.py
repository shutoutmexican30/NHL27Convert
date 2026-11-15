import sys, glob, os, numpy as np
from PIL import Image
o, c = sys.argv[1], sys.argv[2]
lc = np.asarray(Image.open(c + "/ut/logo_utah_color.png").convert("RGB"), np.float32)
la = np.asarray(Image.open(c + "/ut/logo_utah_alpha.png").convert("L"), np.float32) / 255
ys, xs = np.where(la > 0.05); lc = lc[ys.min():ys.max()+1, xs.min():xs.max()+1]; la = la[ys.min():ys.max()+1, xs.min():xs.max()+1]
W = 560; H = int(W * la.shape[0] / la.shape[1])
lc = np.asarray(Image.fromarray(lc.astype(np.uint8)).resize((W, H), Image.LANCZOS), np.float32)
la = np.asarray(Image.fromarray((la * 255).astype(np.uint8)).resize((W, H), Image.LANCZOS), np.float32)[..., None] / 255
T = {"purple": (0x4F, 0x86, 0xBC), "red": (0x1B, 0x1B, 0x1B), "green": (0x6C, 0xAC, 0xE4), "sand": (0xF2, 0xF2, 0xF2)}
REF = {"purple": 0.55, "red": 0.45, "green": 0.35, "sand": 0.85}
def recol(img):
    r, g, b = img[..., 0] / 255, img[..., 1] / 255, img[..., 2] / 255
    mx, mn = img.max(2) / 255, img.min(2) / 255; d = mx - mn + 1e-6
    h = np.where(mx == r, ((g - b) / d) % 6, np.where(mx == g, (b - r) / d + 2, (r - g) / d + 4)) * 60
    s = np.where(mx > 0, d / (mx + 1e-6), 0); v = mx
    grey = (v * 255)[..., None] * np.ones(3, np.float32); out = img.copy()
    cls = {"purple": (h >= 240) & (h <= 320), "red": (h < 25) | (h > 320), "green": (h >= 90) & (h <= 200), "sand": (h >= 25) & (h <= 70)}
    full = {"purple": 0.6, "red": 0.6, "green": 0.45, "sand": 0.3}
    for k, m in cls.items():
        a = np.clip((s - 0.05) / (full[k] - 0.05), 0, 1); m = m & (a > 0) & (v > 0.04)
        shade = np.clip(v[m] / REF[k], 0.3, 1.5)[:, None]
        out[m] = (1 - a[m, None]) * grey[m] + a[m, None] * np.clip(np.array(T[k], np.float32)[None, :] * shade, 0, 255)
    return out
fs = sorted(glob.glob(o + "/f/*.png"))
for i, f in enumerate(fs):
    img = recol(np.asarray(Image.open(f).convert("RGB"), np.float32))
    X, Y = 1000 - W // 2, 425 - H // 2
    img[Y:Y+H, X:X+W] = img[Y:Y+H, X:X+W] * (1 - la) + lc * la
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(o + "/g/" + os.path.basename(f))
print(len(fs))
