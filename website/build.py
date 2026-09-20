#!/usr/bin/env python3
"""Dependency-free static build. No network, credentials, or server runtime."""
from pathlib import Path
import html,json,shutil
ROOT=Path(__file__).resolve().parents[1];SITE=ROOT/'website';OUT=SITE/'dist'
PAGES={
 '':('Your GPUs. Your workloads.','Run workstations and local models on one Bazzite host, with GPU workload profiles managed from your browser.','home.html'),
 'guides':('Guides','Get started with Xur, create workload profiles, stream a desktop and connect to local language models.','guides.html'),
 'guides/getting-started':('Get started','Install Xur and create your first GPU workload profile.','getting-started.html'),
 'guides/workstations':('Workstations and Moonlight','Keep a desktop identity across profiles and stream it with Moonlight.','workstations.html'),
 'guides/models':('Local models and endpoints','Choose model workloads, connect clients and keep benchmark results.','models.html'),
 'guides/updates':('Updates and recovery','Choose a release channel and understand application updates, OS staging and rollback.','updates.html'),
 '404':('Page not found','Find a Xur guide or return to the project home page.','404.html')}
def build():
 shutil.rmtree(OUT,ignore_errors=True);(OUT/'assets').mkdir(parents=True)
 for source in (SITE/'assets').iterdir():shutil.copy2(source,OUT/'assets'/source.name)
 app=ROOT/'src/Xur.Control/wwwroot'
 for name in ['xur-icon.svg','xur-icon-180.png','xur-icon-32.png']:
  shutil.copy2(app/'icons'/name,OUT/'assets'/name)
 for name in ['IBMPlexSans.ttf','OFL.txt']:shutil.copy2(app/'fonts'/name,OUT/'assets'/name)
 for name in ['xur-header.png','workstations-and-llm.png','speech-and-llm.png']:shutil.copy2(ROOT/'docs/assets'/name,OUT/'assets'/name)
 template=(SITE/'template.html').read_text()
 for path,(title,description,file) in PAGES.items():
  page=template.replace('{{title}}',html.escape(title)).replace('{{description}}',html.escape(description)).replace('{{url}}','https://xur.app/'+(path+'/' if path and path!='404' else '')).replace('{{content}}',(SITE/'pages'/file).read_text())
  target=OUT/'404.html' if path=='404' else OUT/path/'index.html';target.parent.mkdir(parents=True,exist_ok=True);target.write_text(page)
 for name in ['_headers','_redirects']:shutil.copy2(SITE/name,OUT/name)
 (OUT/'robots.txt').write_text('User-agent: *\nAllow: /\nSitemap: https://xur.app/sitemap.xml\n')
 (OUT/'sitemap.xml').write_text('<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">'+''.join('<url><loc>https://xur.app/'+(p+'/' if p else '')+'</loc></url>' for p in PAGES if p!='404')+'</urlset>')
 files=[p for p in OUT.rglob('*') if p.is_file()]
 assert len(files)<20000 and all(p.stat().st_size<=25*1024**2 for p in files),'Cloudflare Pages free-plan asset limit exceeded'
 print(json.dumps({'files':len(files),'bytes':sum(p.stat().st_size for p in files),'output':str(OUT)}))
if __name__=='__main__':build()
