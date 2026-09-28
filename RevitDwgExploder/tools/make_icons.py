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


def render_finder(stroke, detail=True):
    """Buscador DWG's: hoja de plano con una lupa encima."""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([u(1), u(1), u(99), u(99)], fill=GRAY)
    poly(d, [(24, 20), (54, 20), (64, 30), (64, 78), (24, 78)], stroke)
    line(d, (54, 20), (54, 30), stroke)
    line(d, (54, 30), (64, 30), stroke)
    if detail:
        line(d, (31, 36), (47, 36), stroke * 0.8)
        line(d, (31, 45), (43, 45), stroke * 0.8)
    # lupa (con fondo gris para que tape la hoja)
    cx, cy, r = 62, 60, 13
    d.ellipse([u(cx - r - stroke), u(cy - r - stroke), u(cx + r + stroke), u(cy + r + stroke)], fill=GRAY)
    d.ellipse([u(cx - r), u(cy - r), u(cx + r), u(cy + r)], outline=WHITE, width=int(u(stroke)))
    line(d, (cx + r * 0.7, cy + r * 0.7), (82, 82), stroke * 1.4)
    return img


def pdf_badge(d, y, height, text_size):
    """Etiqueta "PDF" blanca en la parte baja del icono."""
    from PIL import ImageFont
    try:
        font = ImageFont.truetype("DejaVuSans-Bold.ttf", int(u(text_size)))
    except OSError:
        font = ImageFont.load_default()
    d.rounded_rectangle([u(22), u(y), u(78), u(y + height)], radius=int(u(4)), fill=WHITE)
    d.text((u(50), u(y + height / 2)), "PDF", fill=GRAY, font=font, anchor="mm")


def render_pdf(stroke, finder=False, detail=True):
    """Explotar PDF: hoja explotada (o con lupa) y la etiqueta PDF."""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse([u(1), u(1), u(99), u(99)], fill=GRAY)
    if finder:
        poly(d, [(28, 14), (56, 14), (66, 24), (66, 62), (28, 62)], stroke)
        line(d, (56, 14), (56, 24), stroke)
        line(d, (56, 24), (66, 24), stroke)
        cx, cy, r = 64, 48, 11
        d.ellipse([u(cx - r - stroke), u(cy - r - stroke), u(cx + r + stroke), u(cy + r + stroke)], fill=GRAY)
        d.ellipse([u(cx - r), u(cy - r), u(cx + r), u(cy + r)], outline=WHITE, width=int(u(stroke)))
        line(d, (cx + r * 0.7, cy + r * 0.7), (80, 62), stroke * 1.3)
    else:
        g = 3.5
        move = lambda pts, dx, dy: [(x + dx, y + dy) for x, y in pts]
        poly(d, move([(30, 14), (49, 14), (49, 38), (30, 38)], -g, -g), stroke)
        poly(d, move([(49, 14), (60, 14), (70, 24), (70, 38), (49, 38)], g, -g), stroke)
        poly(d, move([(30, 38), (49, 38), (49, 62), (30, 62)], -g, g), stroke)
        poly(d, move([(49, 38), (70, 38), (70, 62), (49, 62)], g, g), stroke)
        if detail:
            line(d, (30 - g, 62 + g), (49 - g, 46 + g), stroke)
            line(d, (49 + g, 46 + g), (70 + g, 58 + g), stroke)
    pdf_badge(d, 68, 18, 14)
    return img


def save(size, stroke, name, detail=True):
    render(stroke, detail).resize((size, size), Image.LANCZOS).save(OUT / name)


OUT.mkdir(exist_ok=True)
save(32, 4.2, "icon32.png")
save(16, 7.5, "icon16.png", detail=False)
save(256, 2.6, "icon256.png")
render_finder(4.2).resize((32, 32), Image.LANCZOS).save(OUT / "finder32.png")
render_finder(6.5, detail=False).resize((16, 16), Image.LANCZOS).save(OUT / "finder16.png")
render_finder(2.6).resize((256, 256), Image.LANCZOS).save(OUT / "finder256.png")
render_pdf(4.0).resize((32, 32), Image.LANCZOS).save(OUT / "pdf32.png")
render_pdf(6.0, detail=False).resize((16, 16), Image.LANCZOS).save(OUT / "pdf16.png")
render_pdf(2.6).resize((256, 256), Image.LANCZOS).save(OUT / "pdf256.png")
render_pdf(4.0, finder=True).resize((32, 32), Image.LANCZOS).save(OUT / "pdffinder32.png")
render_pdf(6.0, finder=True).resize((16, 16), Image.LANCZOS).save(OUT / "pdffinder16.png")
render_pdf(2.6, finder=True).resize((256, 256), Image.LANCZOS).save(OUT / "pdffinder256.png")
print("iconos generados en", OUT)
