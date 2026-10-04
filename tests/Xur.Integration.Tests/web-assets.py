#!/usr/bin/env python3
"""Exercise the offline asset rule without blocking external navigation links."""
import importlib.util
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'eng'))
spec = importlib.util.spec_from_file_location('web_assets', ROOT / 'eng/check-web-assets.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
assert not module.check(ROOT)
for markup in ['<script src="https://cdn.example/a.js"></script>',
               '<link rel="stylesheet" href="//cdn.example/a.css">',
               '<img srcset="/local.png 1x, https://cdn.example/a.png 2x">',
               '<link rel="preconnect" href="https://cdn.example">',
               '<style>@import "https://cdn.example/a.css";</style>',
               '<style>a{background:url(//cdn.example/a.png)}</style>']:
    assert module.check_text(markup, 'page.razor'), markup
assert not module.check_text('<script src="/vendor/a.js"></script>'
                             '<link rel="stylesheet" href="/app.css">'
                             '<a href="https://github.com/example">Source</a>'
                             '<link rel="canonical" href="https://example.com">', 'page.html')
assert module.check_text('@font-face{src:url("https://cdn.example/font.ttf")}', 'app.css')
assert not module.check_text('@font-face{src:url("/fonts/font.ttf")}', 'app.css')
print('Remote assets are rejected; local assets and external navigation remain valid.')
