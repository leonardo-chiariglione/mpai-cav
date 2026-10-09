"""Makes mkg.ico, the icon of the MPAI Knowledge Graph: a small graph - a hub and its neighbours - on a dark blue square,
in the colours of the viewer (AIM blue, data type green, L3 purple, standard amber)."""
import os
from PIL import Image, ImageDraw

here = os.path.dirname(os.path.abspath(__file__))
S = 256

def draw(size):
    k = size / S
    im = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=int(48 * k), fill=(18, 28, 56, 255))
    hub = (128 * k, 128 * k)
    nodes = [((52, 62), (22, 163, 74)), ((206, 58), (168, 85, 247)), ((52, 196), (245, 158, 11)),
             ((206, 198), (22, 163, 74)), ((128, 28), (245, 158, 11)), ((128, 232), (168, 85, 247))]
    for (x, y), _ in nodes:
        d.line([hub, (x * k, y * k)], fill=(148, 163, 184, 255), width=max(1, int(7 * k)))
    for (x, y), col in nodes:
        r = 20 * k
        d.ellipse([x * k - r, y * k - r, x * k + r, y * k + r], fill=col + (255,))
    r = 36 * k
    d.rounded_rectangle([hub[0] - r, hub[1] - r * 0.8, hub[0] + r, hub[1] + r * 0.8], radius=int(14 * k), fill=(59, 130, 246, 255), outline=(255, 255, 255, 255), width=max(1, int(5 * k)))
    return im

sizes = [16, 24, 32, 48, 64, 128, 256]
draw(256).save(os.path.join(here, 'mkg.ico'), sizes=[(s, s) for s in sizes], append_images=[draw(s) for s in sizes[:-1]])
draw(256).save(os.path.join(here, 'mkg-icon.png'))
print('mkg.ico, mkg-icon.png')
