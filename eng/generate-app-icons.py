#!/usr/bin/env python3
"""Export Xur's geometric app mark. Requires Pillow (development only)."""
from pathlib import Path
from PIL import Image, ImageDraw
root = Path(__file__).resolve().parents[1] / 'src/Xur.Control/wwwroot/icons'
for size in (180, 192, 512):
    scale = 4 * size / 512
    image = Image.new('RGB', (size * 4, size * 4), '#15191e')
    draw = ImageDraw.Draw(image)
    for points in (((152,144),(216,144),(360,368),(296,368)), ((296,144),(360,144),(216,368),(152,368))):
        draw.polygon([(round(x*scale), round(y*scale)) for x,y in points], fill='#9cc4ff')
    draw.ellipse(tuple(round(v*scale) for v in (352,130,382,160)), fill='#6ed7b8')
    image.resize((size,size),Image.Resampling.LANCZOS).save(root/f'xur-{size}.png')
