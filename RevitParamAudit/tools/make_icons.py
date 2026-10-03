"""Genera los iconos de los botones del Depurador (círculo gris con dibujo en trazo blanco, como EMASY DWG Tools).

Uso: python3 tools/make_icons.py   (requiere Pillow)
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "resources"
S = 1024  # se dibuja grande y se reduce con antialiasing
GRAY = (142, 142, 142, 255)
WHITE = (255, 255, 255, 255)


def u(v):
    return v * S / 100.0


def line(d, pts, w):
    d.line([(u(x), u(y)) for x, y in pts], fill=WHITE, width=int(u(w)), joint="curve")


def poly(d, pts, w):
    line(d, pts + [pts[0]], w)


def badge(d, w):
    """Círculo con una X abajo a la derecha: "depurar"."""
    cx, cy, r = 70, 70, 15
    d.ellipse([u(cx - r - 2), u(cy - r - 2), u(cx + r + 2), u(cy + r + 2)], fill=GRAY)
    d.ellipse([u(cx - r), u(cy - r), u(cx + r), u(cy + r)], outline=WHITE, width=int(u(w)))
    line(d, [(cx - 6, cy - 6), (cx + 6, cy + 6)], w)
    line(d, [(cx - 6, cy + 6), (cx + 6, cy - 6)], w)


def base():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([u(1), u(1), u(99), u(99)], fill=GRAY)
    return img, d


def sheets(w, detail=True):
    img, d = base()
    poly(d, [(20, 22), (74, 22), (74, 66), (20, 66)], w)  # hoja apaisada
    line(d, [(20, 56), (74, 56)], w)  # cajetín
    if detail:
        poly(d, [(26, 28), (46, 28), (46, 50), (26, 50)], w * 0.8)  # una vista en el plano
    badge(d, w)
    return img


def views(w, detail=True):
    img, d = base()
    # cubo 3D (vista)
    poly(d, [(26, 34), (46, 24), (66, 34), (46, 44)], w)
    line(d, [(26, 34), (26, 58), (46, 68), (46, 44)], w)
    line(d, [(66, 34), (66, 50)], w)
    if detail:
        line(d, [(46, 68), (56, 63)], w)
    badge(d, w)
    return img


def filters(w, detail=True):
    img, d = base()
    poly(d, [(22, 24), (72, 24), (52, 46), (52, 66), (42, 72), (42, 46)], w)  # embudo
    if detail:
        line(d, [(30, 32), (64, 32)], w * 0.7)
    badge(d, w)
    return img


def params(w, detail=True):
    img, d = base()
    poly(d, [(22, 18), (60, 18), (70, 28), (70, 80), (22, 80)], w)  # lista
    rows = (34, 49, 64) if detail else (38, 58)
    for y in rows:
        d.ellipse([u(28), u(y - 2.5), u(33), u(y + 2.5)], fill=WHITE)
        line(d, [(39, y), (58, y)], w)
    badge(d, w)
    return img


OUT.mkdir(exist_ok=True)
for name, draw in (("sheets", sheets), ("views", views), ("filters", filters), ("params", params)):
    draw(4.2).resize((32, 32), Image.LANCZOS).save(OUT / f"{name}32.png")
    draw(7.0, detail=False).resize((16, 16), Image.LANCZOS).save(OUT / f"{name}16.png")
    draw(2.6).resize((256, 256), Image.LANCZOS).save(OUT / f"{name}256.png")
print("iconos generados en", OUT)
