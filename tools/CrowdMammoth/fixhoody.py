import sys, numpy as np
from PIL import Image
exec(open(sys.argv[1] + "/mammoth.py", encoding="utf-8-sig").read().split("LAYOUTS = {")[0])
for k in ("regular", "playoff"):
    p = f"{d}/mammoth/crowd_outfit_male_hoody_utah_{k}_color.png"
    img = np.asarray(Image.open(p).convert("RGB"), np.float32).copy()
    r = erase_mask(img, (140, 770, 370, 900), 60, 0.9, inner_only=False)
    w = 104; h = int(w * logo_a.shape[0] / logo_a.shape[1]); stamp(img, (256 - w // 2, 830 - h // 2, 256 + w // 2, 830 + h // 2))
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(p); print(k, r)
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).crop((0, 512, 512, 1024)).save(f"{d}/hoody_{k}_tile.png")
