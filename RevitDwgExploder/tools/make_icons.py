"""Genera los iconos del botón (círculo gris con una hoja de plano "explotada" en trazo blanco).

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


def poly(draw, pts, width):
    pts = [(u(x), u(y)) for x, y in pts]
    draw.line(pts + [pts[0]], fill=WHITE, width=int(u(width)), joint="curve")


def line(draw, a, b, width):
    draw.line([(u(a[0]), u(a[1])), (u(b[0]), u(b[1]))], fill=WHITE, width=int(u(width)))


def render(stroke, detail=True):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([u(1), u(1), u(99), u(99)], fill=GRAY)

    g = 4.5  # separación de las piezas (explosión)
    # Hoja de plano de (27,24) a (71,76) partida en 4 piezas desde el centro (49,50); esquina doblada arriba a la derecha.
    tl = [(27, 24), (49, 24), (49, 50), (27, 50)]
    tr = [(49, 24), (61, 24), (71, 34), (71, 50), (49, 50)]
    bl = [(27, 50), (49, 50), (49, 76), (27, 76)]
    br = [(49, 50), (71, 50), (71, 76), (49, 76)]
    move = lambda pts, dx, dy: [(x + dx, y + dy) for x, y in pts]
    poly(d, move(tl, -g, -g), stroke)
    poly(d, move(tr, g, -g), stroke)
    poly(d, move(bl, -g, g), stroke)
    poly(d, move(br, g, g), stroke)
    if not detail:
        return img
    # pliegue de la esquina
    line(d, (61 + g, 24 - g), (61 + g, 34 - g), stroke)
    line(d, (61 + g, 34 - g), (71 + g, 34 - g), stroke)
    # "dibujo CAD" dentro de las piezas (como el logo: diagonales)
    line(d, (27 - g, 76 + g), (49 - g, 58 + g), stroke)
    line(d, (49 + g, 58 + g), (71 + g, 70 + g), stroke)
    line(d, (32 - g, 42 - g), (44 - g, 30 - g), stroke)
    return img


def save(size, stroke, name, detail=True):
    render(stroke, detail).resize((size, size), Image.LANCZOS).save(OUT / name)


OUT.mkdir(exist_ok=True)
save(32, 4.2, "icon32.png")
save(16, 7.5, "icon16.png", detail=False)
save(256, 2.6, "icon256.png")
print("iconos generados en", OUT)
