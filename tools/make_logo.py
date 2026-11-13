"""Generates the Alexbed HyperCopy logo (PNG + multi-size ICO + SVG)."""
import sys, os
from PIL import Image, ImageDraw, ImageFilter

out_dir = sys.argv[1]
S = 1024
SS = 4  # supersampling
W = S * SS

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))
C1, C2, C3 = (0, 198, 255), (64, 96, 255), (140, 48, 247)

# Diagonal three-stop gradient
grad = Image.new("RGB", (256, 256))
px = grad.load()
for y in range(256):
    for x in range(256):
        t = (x + y) / 510
        px[x, y] = lerp(C1, C2, t / 0.5) if t < 0.5 else lerp(C2, C3, (t - 0.5) / 0.5)
grad = grad.resize((W, W), Image.BICUBIC)

mask = Image.new("L", (W, W), 0)
m = int(W * 0.04)
ImageDraw.Draw(mask).rounded_rectangle([m, m, W - m, W - m], radius=int(W * 0.23), fill=255)
img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
img.paste(grad, (0, 0), mask)

# Soft top highlight
hl = Image.new("L", (W, W), 0)
ImageDraw.Draw(hl).ellipse([-W * 0.3, -W * 0.85, W * 1.3, W * 0.42], fill=46)
hl = Image.composite(hl, Image.new("L", (W, W), 0), mask)
img = Image.alpha_composite(img, Image.merge("RGBA", (Image.new("L", (W, W), 255),) * 3 + (hl,)))

def chevron(cx, cy, h, w, thick):
    """Right-pointing chevron as a polygon centred at (cx, cy)."""
    return [(cx - w / 2, cy - h / 2), (cx - w / 2 + thick, cy - h / 2), (cx + w / 2, cy),
            (cx - w / 2 + thick, cy + h / 2), (cx - w / 2, cy + h / 2), (cx + w / 2 - thick, cy)]

layer = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(layer)
h, w, th = W * 0.46, W * 0.25, W * 0.115
cy = W * 0.5
d.polygon(chevron(W * 0.40, cy, h, w, th), fill=(255, 255, 255, 150))
d.polygon(chevron(W * 0.60, cy, h, w, th), fill=(255, 255, 255, 255))
# speed lines
for i, (y, ln) in enumerate([(0.36, 0.13), (0.5, 0.17), (0.64, 0.13)]):
    x1 = W * 0.13
    d.rounded_rectangle([x1, W * y - W * 0.018, x1 + W * ln, W * y + W * 0.018], radius=W * 0.018,
                        fill=(255, 255, 255, 110))

shadow = layer.split()[3].filter(ImageFilter.GaussianBlur(W * 0.02))
shadow_img = Image.new("RGBA", (W, W), (20, 10, 80, 0))
shadow_img.putalpha(shadow.point(lambda a: int(a * 0.45)))
shadow_off = Image.new("RGBA", (W, W), (0, 0, 0, 0))
shadow_off.paste(shadow_img, (0, int(W * 0.012)))
img = Image.alpha_composite(img, shadow_off)
img = Image.alpha_composite(img, layer)

final = img.resize((S, S), Image.LANCZOS)
final.save(os.path.join(out_dir, "logo.png"))
final.resize((256, 256), Image.LANCZOS).save(os.path.join(out_dir, "logo-256.png"))
final.save(os.path.join(out_dir, "AlexbedHyperCopy.ico"),
           sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok")
