"""Gera o ícone do PDF Master (PDFMaster.App/Assets/PDFMaster.ico) em todos os tamanhos do Windows.

Desenho original: quadrado arredondado verde (cores do programa), folha branca com canto dobrado
e a etiqueta "PDF". Nos tamanhos pequenos (16 e 24 px) a etiqueta vira linhas de texto, que
continuam legíveis. Requer Pillow e a fonte DejaVu Sans Bold (licença livre).
Uso: python assets/gerar-icone.py
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
GREEN_TOP = (0, 163, 94)
GREEN_BOTTOM = (0, 92, 58)
GREEN_TEXT = (0, 120, 70)
FOLD = (203, 230, 216)


def gradient(size):
    image = Image.new("RGBA", (size, size))
    draw = ImageDraw.Draw(image)
    for y in range(size):
        t = y / max(1, size - 1)
        color = tuple(round(a + (b - a) * t) for a, b in zip(GREEN_TOP, GREEN_BOTTOM)) + (255,)
        draw.line([(0, y), (size, y)], fill=color)
    return image


def render(px):
    s = 1024  # desenha grande e reduz (bordas suaves)
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Fundo: quadrado arredondado em degradê.
    mask = Image.new("L", (s, s), 0)
    margin = 40 if px >= 32 else 16
    ImageDraw.Draw(mask).rounded_rectangle([margin, margin, s - margin, s - margin], radius=210, fill=255)
    image.paste(gradient(s), (0, 0), mask)

    # Folha com canto dobrado (sombra suave embaixo).
    left, top, right, bottom = 262, 170, 762, 854
    if px < 32:
        left, top, right, bottom = 232, 140, 792, 884
    fold = 150 if px >= 32 else 190
    page = [(left, top), (right - fold, top), (right, top + fold), (right, bottom), (left, bottom)]
    shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).polygon([(x + 14, y + 22) for x, y in page], fill=(0, 40, 25, 110))
    image.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(18)))
    draw = ImageDraw.Draw(image)
    draw.polygon(page, fill=(255, 255, 255, 255))
    draw.polygon([(right - fold, top), (right - fold, top + fold), (right, top + fold)], fill=FOLD + (255,))

    if px >= 32:
        # Linhas de texto e a etiqueta "PDF".
        for i, width in enumerate((330, 380, 300)):
            y = top + 120 + i * 70
            draw.rounded_rectangle([left + 60, y, left + 60 + width - (60 if i == 0 else 0), y + 26], radius=13, fill=(170, 205, 188, 255))
        band_top, band_bottom = bottom - 300, bottom - 70
        draw.rounded_rectangle([left - 70, band_top, right - 40, band_bottom], radius=48, fill=GREEN_TEXT + (255,))
        font = ImageFont.truetype(FONT, 190)
        text = "PDF"
        box = draw.textbbox((0, 0), text, font=font)
        tw, th = box[2] - box[0], box[3] - box[1]
        cx = (left - 70 + right - 40) / 2
        cy = (band_top + band_bottom) / 2
        draw.text((cx - tw / 2 - box[0], cy - th / 2 - box[1]), text, font=font, fill=(255, 255, 255, 255))
    else:
        # Tamanhos pequenos: só linhas grossas, que não viram borrão.
        for i in range(4):
            y = top + 250 + i * 140
            draw.rectangle([left + 90, y, right - 90 - (120 if i == 3 else 0), y + 70], fill=GREEN_TEXT + (255,))

    return image.resize((px, px), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = [render(px) for px in sizes]
    out = ROOT / "PDFMaster.App" / "Assets" / "PDFMaster.ico"
    images[-1].save(out, format="ICO", sizes=[(px, px) for px in sizes], append_images=images[:-1])
    render(256).save(ROOT / "assets" / "icone-256.png")
    # Prévia lado a lado (para conferir os tamanhos pequenos).
    preview = Image.new("RGBA", (sum(sizes) + 10 * len(sizes) + 10, 276), (255, 255, 255, 255))
    x = 10
    for px, img in zip(sizes, images):
        preview.alpha_composite(img, (x, 266 - px))
        x += px + 10
    preview.save(ROOT / "assets" / "icone-previa.png")
    print(out, out.stat().st_size, "bytes")


if __name__ == "__main__":
    main()
