import os, math
from PIL import Image, ImageDraw

src = os.path.join(os.environ['TEMP'], 'hotspots')
files = [f for f in os.listdir(src) if f.endswith('.png')]
def key(f):
    s, i = f[1:-4].split('_')
    return (int(s), int(i))
files.sort(key=key)

cell = 140  # thumbnail + label
cols = 16
thumb = 128
for group_name, pred in [('set0', lambda f: f.startswith('s0_')), ('set1', lambda f: f.startswith('s1_'))]:
    fs = [f for f in files if pred(f)]
    rows = math.ceil(len(fs) / cols)
    sheet = Image.new('RGB', (cols * cell, rows * cell + 30), (24, 24, 24))
    d = ImageDraw.Draw(sheet)
    d.text((8, 6), f"ice_hotspot {group_name}  ({len(fs)} slots)", fill=(255, 255, 0))
    for n, f in enumerate(fs):
        img = Image.open(os.path.join(src, f)).convert('RGBA')
        img.thumbnail((thumb, thumb))
        bg = Image.new('RGBA', (thumb, thumb), (60, 60, 60, 255))
        bg.paste(img, ((thumb - img.width) // 2, (thumb - img.height) // 2), img)
        x, y = (n % cols) * cell, 30 + (n // cols) * cell
        sheet.paste(bg.convert('RGB'), (x, y))
        d.text((x + 4, y + thumb + 2), f[1:-4], fill=(220, 220, 220))
    out = os.path.join(src, f"sheet_{group_name}.png")
    sheet.save(out)
    print(out, sheet.size)
