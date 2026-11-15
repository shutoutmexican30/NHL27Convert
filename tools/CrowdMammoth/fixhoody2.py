import sys, numpy as np
from PIL import Image
exec(open(sys.argv[1] + "/mammoth.py", encoding="utf-8-sig").read().split("LAYOUTS = {")[0])
for k in ("regular", "playoff"):
    p = f"{d}/mammoth/crowd_outfit_male_hoody_utah_{k}_color.png"
    img = np.asarray(Image.open(p).convert("RGB"), np.float32).copy()
    print(k, erase_mask(img, (205, 735, 310, 775), 60, 0.95, inner_only=False))
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(p)
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).crop((0, 512, 512, 1024)).resize((256, 256)).save(f"{d}/hoody_{k}_tile.png")
