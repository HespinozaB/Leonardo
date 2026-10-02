"""Genera los iconos del botón (círculo gris con una lista de parámetros y una lupa/visto en trazo blanco).

Uso: python3 tools/make_icons.py   (requiere Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "resources"
S = 1024
GRAY = (142, 142, 142, 255)
WHITE = (255, 255, 255, 255)


def u(v):
    return v * S / 100.0


def line(d, a, b, w):
    d.line([(u(a[0]), u(a[1])), (u(b[0]), u(b[1]))], fill=WHITE, width=int(u(w)))


def render(stroke, detail=True):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([u(1), u(1), u(99), u(99)], fill=GRAY)
    # Hoja de lista con tres filas (viñeta + línea), una tachada = parámetro residual.
    pts = [(24, 18), (62, 18), (72, 28), (72, 80), (24, 80)]
    pts = [(u(x), u(y)) for x, y in pts]
    d.line(pts + [pts[0]], fill=WHITE, width=int(u(stroke)), joint="curve")
    rows = (34, 49, 64) if detail else (38, 58)
    for y in rows:
        d.ellipse([u(30), u(y - 2.5), u(35), u(y + 2.5)], fill=WHITE)
        line(d, (41, y), (62, y), stroke)
    # Marca de depuración (papelera simple) abajo a la derecha, sobre fondo gris.
    cx, cy, r = 68, 68, 15
    d.ellipse([u(cx - r), u(cy - r), u(cx + r), u(cy + r)], fill=GRAY)
    d.ellipse([u(cx - r + 1.5), u(cy - r + 1.5), u(cx + r - 1.5), u(cy + r - 1.5)], outline=WHITE, width=int(u(stroke)))
    line(d, (cx - 6, cy - 6), (cx + 6, cy + 6), stroke)
    line(d, (cx - 6, cy + 6), (cx + 6, cy - 6), stroke)
    return img


OUT.mkdir(exist_ok=True)
render(4.2).resize((32, 32), Image.LANCZOS).save(OUT / "audit32.png")
render(7.0, detail=False).resize((16, 16), Image.LANCZOS).save(OUT / "audit16.png")
render(2.6).resize((256, 256), Image.LANCZOS).save(OUT / "audit256.png")
print("iconos generados en", OUT)
