#!/usr/bin/env python3
"""Reject remote page assets that make the local UI depend on internet access."""
from html.parser import HTMLParser
from pathlib import Path
import re
import sys
from source_files import source_files

ROOT = Path(__file__).resolve().parents[1]
REMOTE = re.compile(r'^(?:https?:)?//', re.I)
ASSETS = {'script': ('src',), 'img': ('src', 'srcset'),
          'source': ('src', 'srcset'), 'video': ('src', 'poster'),
          'audio': ('src',), 'iframe': ('src',), 'input': ('src',),
          'image': ('href', 'xlink:href')}
LINK_ASSETS = {'stylesheet', 'icon', 'preload', 'modulepreload',
               'prefetch', 'preconnect', 'dns-prefetch'}


def check_text(text, name):
    errors = []
    class Assets(HTMLParser):
        def handle_starttag(self, tag, attributes):
            attrs = dict(attributes)
            fields = ASSETS.get(tag, ())
            if tag == 'link' and LINK_ASSETS.intersection((attrs.get('rel') or '').lower().split()):
                fields = ('href',)
            for field in fields:
                value = (attrs.get(field) or '').strip()
                candidates = value.split(',') if field == 'srcset' else [value]
                if any(REMOTE.match(v.strip()) for v in candidates):
                    errors.append(f'{name}: remote {tag} {field}: {value}')
    if not name.endswith('.css'):
        Assets().feed(text)
    for url in re.findall(r'url\(\s*[\"\']?((?:https?:)?//[^\s)\"\']+)|@import\s+[\"\']((?:https?:)?//[^\"\']+)', text, re.I):
        errors.append(f'{name}: remote CSS asset: {url[0] or url[1]}')
    return errors


def check(root):
    return [error for path in source_files(root)
            if path.suffix in ('.html', '.razor', '.css')
            for error in check_text(path.read_text(), str(path.relative_to(root)))]


if __name__ == '__main__':
    errors = check(ROOT)
    if errors:
        print('\n'.join(errors), file=sys.stderr)
        raise SystemExit(1)
    print('HTML, Razor and CSS assets use local URLs; no runtime CDN references.')
