"""Builds docs/social-preview.png (1280x640) for the GitHub repository card."""
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter
logo_path, shot_path, out_path = sys.argv[1:4]
W, H = 1280, 640
img = Image.new("RGB", (W, H), (13, 16, 23))
glow = Image.new("L", (W, H), 0)
ImageDraw.Draw(glow).ellipse([700, -300, 1500, 400], fill=90)
glow = glow.filter(ImageFilter.GaussianBlur(120))
img = Image.composite(Image.new("RGB", (W, H), (64, 96, 255)), img, glow)
def font(name, size):
    for n in (name, "segoeui.ttf"):
        try: return ImageFont.truetype(n, size)
        except OSError: pass
    return ImageFont.load_default()
logo = Image.open(logo_path).convert("RGBA").resize((120, 120), Image.LANCZOS)
img.paste(logo, (70, 92), logo)
d = ImageDraw.Draw(img)
d.text((70, 240), "Alexbed", font=font("segoeuib.ttf", 66), fill=(234, 238, 246))
d.text((70, 318), "HyperCopy", font=font("segoeuib.ttf", 66), fill=(90, 150, 255))
d.text((72, 420), "Copy and move terabytes", font=font("segoeui.ttf", 30), fill=(200, 206, 220))
d.text((72, 460), "at full disk speed.", font=font("segoeui.ttf", 30), fill=(200, 206, 220))
d.text((72, 540), "Free · Portable · Open source · Windows", font=font("segoeui.ttf", 22), fill=(138, 148, 169))
shot = Image.open(shot_path).convert("RGB")
shot = shot.resize((int(shot.width * 0.62), int(shot.height * 0.62)), Image.LANCZOS)
x, y = 560, 95
shadow = Image.new("L", (W, H), 0)
ImageDraw.Draw(shadow).rectangle([x + 10, y + 18, x + shot.width + 10, y + shot.height + 18], fill=160)
shadow = shadow.filter(ImageFilter.GaussianBlur(24))
img = Image.composite(Image.new("RGB", (W, H), (0, 0, 0)), img, shadow)
img.paste(shot, (x, y))
ImageDraw.Draw(img).rectangle([x, y, x + shot.width - 1, y + shot.height - 1], outline=(39, 47, 65), width=2)
img.save(out_path)
print("ok", img.size)
