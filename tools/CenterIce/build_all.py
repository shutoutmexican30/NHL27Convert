"""Build every changed NHL center-ice logo (NHL 27 slot -> NHL 24 slot) and print the ChunkWrite arguments."""
import os, subprocess, uuid, numpy as np
from PIL import Image, ImageDraw
root = r'C:\Users\devin\AppData\Local\CenterIceModdingSuite\centerice'
exe = r'C:\dev\NHL27Convert\tools\CenterIce\bin\Release\net8.0\CenterIce.exe'
# Team center ice keeps its slot in both games (Vegas 32, Seattle 33); 30 and 31 are All-Star logos and stay.
# Only Utah moved: NHL 27 slot 30 -> NHL 24 slot 22 (22 is still Arizona in NHL 27).
pairs = {s: s for s in range(34) if s not in (22, 30, 31)}
pairs[22] = 30
os.makedirs(os.path.join(root, 'all'), exist_ok=True)
args, rows = [], []
for s24, s27 in sorted(pairs.items()):
    a = np.asarray(Image.open(os.path.join(root, 'nhl24', f'ice_centerlogo_{s24}_color.png')).convert('RGBA'), dtype=np.int16)
    b = np.asarray(Image.open(os.path.join(root, 'nhl27', f'ice_centerlogo_{s27}_color.png')).convert('RGBA'), dtype=np.int16)
    if np.abs(a - b).mean() <= 1: continue
    res = os.path.join(root, 'nhl24', f'content~worlds~rink~textures~centerlogo~ice_centerlogo_{s24}_color.res')
    out = os.path.join(root, 'all', f'slot{s24}.chunk')
    r = subprocess.run([exe, 'build', res, os.path.join(root, 'nhl27', f'ice_centerlogo_{s27}_color.png'), out], capture_output=True, text=True)
    if r.returncode: raise SystemExit(r.stderr)
    guid = uuid.UUID(bytes_le=open(res, 'rb').read()[32:48])
    args.append(f'{guid}={out}')
    rows.append((s24, s27))
open(os.path.join(root, 'all', 'args.txt'), 'w').write('\n'.join(args))
print(len(rows), 'slots:', rows)
S = 128
sheet = Image.new('RGB', (S * 2 * 6 + 5 * 8, (S + 16) * ((len(rows) + 5) // 6)), (40, 40, 40))
d = ImageDraw.Draw(sheet)
for i, (s24, s27) in enumerate(rows):
    x0, y0 = (i % 6) * (2 * S + 8), (i // 6) * (S + 16)
    for j, f in enumerate((f'nhl24/ice_centerlogo_{s24}_color.png', f'nhl27/ice_centerlogo_{s27}_color.png')):
        bg = Image.new('RGBA', (S, S), (215, 228, 238, 255)); bg.alpha_composite(Image.open(os.path.join(root, f)).convert('RGBA').resize((S, S)))
        sheet.paste(bg.convert('RGB'), (x0 + j * S, y0 + 16))
    d.text((x0 + 2, y0 + 2), f'slot {s24}: before | after (27 #{s27})', fill='white')
sheet.save(os.path.join(root, 'centerice_all_before_after.png'))
