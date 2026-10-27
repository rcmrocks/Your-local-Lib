"""Welcome and header images for the NSIS wizard, in the app's paper and forest palette."""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
FONT_DIR = ROOT / "src" / "YourLocalLib" / "Assets" / "Fonts"
ICON = ROOT / "src" / "YourLocalLib" / "Assets" / "icon.png"
OUT = Path(__file__).resolve().parent

PAPER = (244, 239, 230)
INK = (30, 26, 22)
MUTED = (107, 98, 88)
FOREST = (30, 77, 58)
CREAM = (251, 248, 243)


def font(weight: str, size: int) -> ImageFont.FreeTypeFont:
    name = "SourceSerif4-Semibold.otf" if weight == "semi" else "SourceSerif4-Regular.otf"
    return ImageFont.truetype(str(FONT_DIR / name), size)


def center_text(draw: ImageDraw.ImageDraw, text: str, y: int, face: ImageFont.FreeTypeFont, fill, width: int) -> None:
    box = draw.textbbox((0, 0), text, font=face)
    x = (width - (box[2] - box[0])) // 2
    draw.text((x, y), text, font=face, fill=fill)


def welcome() -> Image.Image:
    width, height = 164, 314
    image = Image.new("RGB", (width, height), PAPER)
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 7, height), fill=FOREST)
    draw.rectangle((0, height - 8, width, height), fill=FOREST)

    icon = Image.open(ICON).convert("RGBA").resize((88, 88), Image.Resampling.LANCZOS)
    image.paste(icon, (38, 36), icon)

    center_text(draw, "Your Local", 146, font("semi", 20), INK, width)
    center_text(draw, "Lib", 176, font("regular", 16), MUTED, width)

    draw.rectangle((36, 224, 128, 225), fill=FOREST)
    center_text(draw, "A private catalog", 244, font("regular", 12), MUTED, width)
    center_text(draw, "for your shelves", 262, font("regular", 12), MUTED, width)
    return image


def header() -> Image.Image:
    width, height = 150, 57
    image = Image.new("RGB", (width, height), PAPER)
    icon = Image.open(ICON).convert("RGBA").resize((40, 40), Image.Resampling.LANCZOS)
    image.paste(icon, (6, 8), icon)
    draw = ImageDraw.Draw(image)
    draw.text((52, 8), "Your Local", font=font("semi", 14), fill=INK)
    draw.text((52, 30), "Lib", font=font("regular", 12), fill=MUTED)
    return image


def save_bmp(image: Image.Image, path: Path) -> None:
    image.save(path, "BMP")
    image.save(path.with_suffix(".png"), "PNG")


if __name__ == "__main__":
    save_bmp(welcome(), OUT / "welcome.bmp")
    save_bmp(header(), OUT / "header.bmp")
