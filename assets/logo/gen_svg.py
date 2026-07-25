"""Regenerates the SocketSet marks: `python gen_svg.py` writes the .svg files beside this script.

The geometry is code rather than hand-authored path data, so a mark can be re-cut — different shard
count, different weights — without redrawing it. The colours are the house palette shared with
Dapper and protobuf-net. `ring` is the one packed as the package icon; see Directory.Build.props.

Marks that use the ink colour tag it `class="ink"` instead of filling it, so a host page can flip it
white on a dark ground. `ring` is pure colour and needs no such handling, which is part of why it is
the one that ships.
"""
import math, os

CY, PK, OR, LI = "#00ABC5", "#ED0F69", "#F58220", "#B2D135"
C = 128.0
OUT = os.path.dirname(os.path.abspath(__file__))

def pt(r, deg):
    a = math.radians(deg)
    return C + r * math.cos(a), C + r * math.sin(a)

def arc(r, a0, a1, color, w):
    x0, y0 = pt(r, a0)
    x1, y1 = pt(r, a1)
    large = 1 if (a1 - a0) % 360 > 180 else 0
    return (f'<path d="M {x0:.2f} {y0:.2f} A {r} {r} 0 {large} 1 {x1:.2f} {y1:.2f}" '
            f'fill="none" stroke="{color}" stroke-width="{w}" />')

def svg(name, body):
    doc = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" '
           'role="img" aria-label="SocketSet mark">\n  ' + "\n  ".join(body) + "\n</svg>\n")
    open(os.path.join(OUT, name + ".svg"), "w", encoding="utf-8").write(doc)
    return doc

# 1. RING — four arcs of one ring buffer; io_uring and RIO are both rings.
gap = 9
svg("ring", [arc(88, -90 + i * 90 + gap, -90 + (i + 1) * 90 - gap, c, 32)
             for i, c in enumerate((CY, PK, OR, LI))])

# 2. LANES — four independent channels, each pinned to its own shard.
lanes = []
for i, c in enumerate((CY, PK, OR, LI)):
    y = 46 + i * 54
    h = 30
    lanes.append(f'<path d="M 24 {y} H 168 L 168 {y - 14} L 224 {y + h / 2:.0f} '
                 f'L 168 {y + h + 14} L 168 {y + h} H 24 Z" fill="{c}" />')
svg("lanes", lanes)

# 3. SET — a set of four ports; the name of the library, drawn literally.
cells = []
for i, c in enumerate((CY, PK, OR, LI)):
    x = 22 + (i % 2) * 116
    y = 22 + (i // 2) * 116
    s, hole, r = 96, 34, 16
    hx, hy = x + (s - hole) / 2, y + (s - hole) / 2
    cells.append(
        f'<path fill="{c}" fill-rule="evenodd" d="'
        f'M {x + r} {y} H {x + s - r} A {r} {r} 0 0 1 {x + s} {y + r} V {y + s - r} '
        f'A {r} {r} 0 0 1 {x + s - r} {y + s} H {x + r} A {r} {r} 0 0 1 {x} {y + s - r} '
        f'V {y + r} A {r} {r} 0 0 1 {x + r} {y} Z '
        f'M {hx + 8} {hy} H {hx + hole - 8} A 8 8 0 0 1 {hx + hole} {hy + 8} '
        f'V {hy + hole - 8} A 8 8 0 0 1 {hx + hole - 8} {hy + hole} H {hx + 8} '
        f'A 8 8 0 0 1 {hx} {hy + hole - 8} V {hy + 8} A 8 8 0 0 1 {hx + 8} {hy} Z" />')
svg("set", cells)

# 4. APERTURE — four bars pinwheeled around an open port. Each is the 90-degree
#    rotation of the last: (x,y) -> (256-y, x).
# The far end of each bar meets the side of the next, so the four close a square
# port rather than floating apart.
bars, quad = [], (32.0, 32.0, 180.0, 76.0)  # x0 y0 x1 y1
for c in (CY, PK, OR, LI):
    x0, y0, x1, y1 = quad
    bars.append(f'<rect x="{min(x0, x1)}" y="{min(y0, y1)}" width="{abs(x1 - x0)}" '
                f'height="{abs(y1 - y0)}" fill="{c}" />')
    quad = (256 - y0, x0, 256 - y1, x1)
svg("aperture", bars)

# 5. SLOTS — a ring of slots with four in flight: the free-list, drawn.
# Live slots are more than twice the radius of free ones, so the four colours still
# carry the mark when it shrinks to a favicon.
dots, n = [], 10
for i in range(n):
    x, y = pt(84, -90 + i * (360 / n))
    live = (CY, PK, OR, LI)
    if i < 4:
        dots.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="19" fill="{live[i]}" />')
    else:
        dots.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="8" class="ink" opacity="0.28" />')
svg("slots", dots)

print("\n".join(sorted(os.listdir(OUT))))
