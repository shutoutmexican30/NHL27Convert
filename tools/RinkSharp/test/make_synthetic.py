"""Synthetic TheFaceoff.net-style rink layout for pipeline validation."""
from PIL import Image, ImageDraw
import math

W, H = 5000, 2236
PPM = 80.0                      # pixels per metre
CX, CY = W / 2, H / 2
M = 3.28084                     # ft per m

img = Image.new("RGBA", (W, H), (255, 255, 255, 255))
d = ImageDraw.Draw(img)

def X(z_m): return CX + z_m * PPM          # z along the rink
def Y(x_m): return CY + x_m * PPM          # x across
def f(feet): return feet / M               # feet -> metres

ICE = (238, 241, 244)
RED = (200, 32, 46)
BLUE = (0, 60, 166)
CREASE = (124, 190, 232)
INK = (60, 60, 66)

ice_w, ice_h = 60.96, 25.98
corner = 8.53

# Ice surface: rounded rectangle (drawn as polygon with arcs)
x0, y0 = X(-ice_w/2), Y(-ice_h/2)
x1, y1 = X(ice_w/2), Y(ice_h/2)
r = corner * PPM
d.rounded_rectangle([x0, y0, x1, y1], radius=r, fill=ICE + (255,))
# Dark board outline (TheFaceoff style dark line round the ice)
d.rounded_rectangle([x0, y0, x1, y1], radius=r, outline=INK + (255,), width=8)

# Centre logo: blue circle with a white stylised mark
d.ellipse([X(-f(15)), Y(-f(15)), X(f(15)), Y(f(15))], fill=BLUE + (255,))
d.ellipse([X(-f(9)), Y(-f(9)), X(f(9)), Y(f(9))], fill=(255,255,255,255))
d.polygon([(X(0), Y(-f(7))), (X(f(5)), Y(f(5))), (X(-f(5)), Y(f(5)))], fill=BLUE + (255,))

def circle(cz, cx_, rad, col, width=0, dot=True):
    d.ellipse([X(cz-rad), Y(cx_-rad), X(cz+rad), Y(cx_+rad)], outline=col + (255,), width=width or int(0.1*PPM))
    if dot:
        rr = f(0.5)
        d.ellipse([X(cz-rr), Y(cx_-rr), X(cz+rr), Y(cx_+rr)], fill=col + (255,))

# Centre circle (blue)
circle(0, 0, f(15), BLUE)

# Blue lines (25..26 ft from centre)
for s in (-1, 1):
    bx0, bx1 = sorted((X(s*f(25)), X(s*f(26))))
    d.rectangle([bx0, y0, bx1, y1], fill=BLUE + (255,))

goal_ft = 89.1   # where the game's nets stand (measured)
# Creases: 6 ft arc, 4 ft sides, shrunk like the game (0.8)
arc = 6 * 0.8 / M; side = 4 * 0.8 / M
for s in (-1, 1):
    gz = s * f(goal_ft)
    # crease fill: box + arc approximation
    d.pieslice([X(gz-arc if s>0 else gz-arc), Y(-side), X(gz+arc), Y(side)],
               start=-90 if s>0 else 90, end=90 if s>0 else 270, fill=CREASE + (255,))
    # crease outline red
    d.arc([X(gz-arc), Y(-side), X(gz+arc), Y(side)], start=-90 if s>0 else 90,
          end=90 if s>0 else 270, fill=RED + (255,), width=int(0.1*PPM))
    d.line([X(gz), Y(-side), X(gz), Y(side)], fill=RED + (255,), width=int(0.1*PPM))

# Goal lines (red) and trapezoid
for s in (-1, 1):
    gz = X(s * f(goal_ft))
    d.line([gz, y0, gz, y1], fill=RED + (255,), width=int(0.12*PPM))
    d.line([gz, Y(f(11)), X(s*(f(goal_ft)+f(11))), Y(f(14))], fill=RED + (255,), width=int(0.08*PPM))
    d.line([gz, Y(-f(11)), X(s*(f(goal_ft)+f(11))), Y(-f(14))], fill=RED + (255,), width=int(0.08*PPM))

# End zone circles (20 ft out from goal line, 22 ft each side), dots, hash marks
for s in (-1, 1):
    cz = s * (f(goal_ft) - f(20))
    circle(cz, f(22), f(15), RED)
    circle(cz, -f(22), f(15), RED)

# Neutral zone dots (5 ft inside blue lines, 22 ft each side)
for s in (-1, 1):
    dz = s * f(20)
    for xx in (f(22), -f(22)):
        rr = f(0.5)
        d.ellipse([X(dz-rr), Y(xx-rr), X(dz+rr), Y(xx+rr)], fill=RED + (255,))

# Grey nets drawn BEHIND the goal lines (TheFaceoff style) - these must get painted out
for s in (-1, 1):
    gz = s * f(goal_ft)
    back = s * (f(goal_ft) + f(2.6))
    net = [(gz, f(0.95)), (back, f(0.75)), (back, -f(0.75)), (gz, -f(0.95))]
    d.polygon([(X(a), Y(b)) for a, b in net], fill=(128, 128, 132, 255))
    d.line([(X(gz), Y(f(0.95))), (X(back), Y(f(0.75)))], fill=(90,90,94,255), width=6)
    d.line([(X(gz), Y(-f(0.95))), (X(back), Y(-f(0.75)))], fill=(90,90,94,255), width=6)

img.convert("RGB").save(r"C:\dev\NHL27Convert\tools\RinkSharp\test\synthetic_layout.png")
print("saved", img.size)
