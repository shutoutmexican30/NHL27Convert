import sys, numpy as np
from PIL import Image
sys.argv = [sys.argv[0], sys.argv[1]]
exec(open(sys.argv[1] + "/mammoth.py", encoding="utf-8-sig").read().split("LAYOUTS = {")[0])
def fix(name, boxes):
    p = f"{d}/mammoth/{name}.png"
    img = np.asarray(Image.open(p).convert("RGB"), np.float32).copy()
    for (x0, y0, x1, y1, logo) in boxes:
        r = erase_mask(img, (x0, y0, x1, y1), 45, 0.9, inner_only=False)
        if logo and r:
            _, (bx0, by0, bx1, by1) = r
            w = max(14, int((bx1 - bx0) * 1.1)); h = int(w * logo_a.shape[0] / logo_a.shape[1]); cx, cy = (bx0 + bx1) / 2, (by0 + by1) / 2
            stamp(img, (int(cx - w / 2), int(cy - h / 2), int(cx + w / 2), int(cy + h / 2)))
        print(name, (x0, y0, x1, y1), "erased" if r else "nothing")
    Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).save(p)
fix("crowd_prop_team_utah_color", [(5,35,95,130,1),(122,2,260,183,1),(264,5,336,76,1),(360,35,432,62,0),(15,185,85,505,0),(310,385,452,482,1)])
fix("crowd_prop_male_hat_utah_color", [(5,95,52,126,1),(80,105,122,127,0),(138,15,166,41,1),(140,105,186,127,0),(265,95,311,127,1),(295,1,346,41,1),(400,90,461,127,0),(195,130,236,171,1),(5,225,56,253,1),(125,190,186,253,0),(265,220,321,253,1),(390,220,451,253,1)])

