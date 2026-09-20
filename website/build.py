#!/usr/bin/env python3
"""Dependency-free static build. No network, credentials, or server runtime."""
from pathlib import Path
import base64,hashlib,html,json,shutil
ROOT=Path(__file__).resolve().parents[1];SITE=ROOT/'website';OUT=SITE/'dist'
PAGES={
 '':('GPU workstations and local AI on Bazzite','Run workstations and local models on one Bazzite host, with GPU workload profiles managed from your browser.','home.html'),
 'download':('Download the online installer','Download the latest Xur online installer ISO and create a bootable USB with Rufus. Simple Windows instructions for your x86-64 GPU host.','download.html'),
 'guides':('Guides','Get started with Xur, create workload profiles, stream a desktop and connect to local language models.','guides.html'),
 'guides/getting-started':('Get started','Install Xur and create your first GPU workload profile.','getting-started.html'),
 'guides/workstations':('Workstations and Moonlight','Keep a desktop identity across profiles and stream it with Moonlight.','workstations.html'),
 'guides/models':('Local models and endpoints','Choose model workloads, connect clients and keep benchmark results.','models.html'),
 'guides/network-and-answers':('Network settings and answer YAML','Configure static IP addresses and provide a bootstrap answer file while retaining explicit disk approval.','network-and-answers.html'),
 'guides/updates':('Updates and recovery','Choose a release channel and understand application updates, OS staging and rollback.','updates.html'),
 '404':('Page not found','Find a Xur guide or return to the project home page.','404.html')}
def build():
 shutil.rmtree(OUT,ignore_errors=True);(OUT/'assets').mkdir(parents=True)
 for source in (SITE/'assets').iterdir():shutil.copy2(source,OUT/'assets'/source.name)
 app=ROOT/'src/Xur.Control/wwwroot'
 for name in ['xur-icon.svg','xur-icon-180.png','xur-icon-32.png']:
  shutil.copy2(app/'icons'/name,OUT/'assets'/name)
 for name in ['IBMPlexSans.ttf','OFL.txt']:shutil.copy2(app/'fonts'/name,OUT/'assets'/name)
 for name in ['xur-header.png','workstations-and-llm.png','speech-and-llm.png','control-panel.jpg','workstations.jpg','profile-editor.jpg','model-lab.jpg','update-channel.jpg']:shutil.copy2(ROOT/'docs/assets'/name,OUT/'assets'/name)
 shutil.copy2(ROOT/'LICENSE',OUT/'license.txt')
 (OUT/'examples').mkdir()
 shutil.copy2(ROOT/'docs/examples/xur.yml',OUT/'examples/xur.yml')
 template=(SITE/'template.html').read_text()
 hashes=set()
 for path,(title,description,file) in PAGES.items():
  url='https://xur.app/'+(path+'/' if path and path!='404' else '404.html' if path=='404' else '')
  graph=[{'@type':'WebSite','@id':'https://xur.app/#website','name':'Xur','url':'https://xur.app/','description':'Open-source GPU workload profiles for workstations and local AI.'},
   {'@type':'WebPage','@id':url,'name':title,'description':description,'url':url,'isPartOf':{'@id':'https://xur.app/#website'}}]
  if not path:graph.append({'@type':'SoftwareSourceCode','name':'Xur','description':description,'codeRepository':'https://github.com/DouglasCleghorn/Xur','license':'https://opensource.org/license/mit','programmingLanguage':['C#','Python'],'runtimePlatform':'Linux / Bazzite','url':'https://xur.app/'})
  if path and path!='404':
   crumbs=[('Home','https://xur.app/')]+([('Guides','https://xur.app/guides/')] if path.startswith('guides/') else [])+[(title,url)]
   graph.append({'@type':'BreadcrumbList','itemListElement':[{'@type':'ListItem','position':i+1,'name':name,'item':link} for i,(name,link) in enumerate(crumbs)]})
  data=json.dumps({'@context':'https://schema.org','@graph':graph},ensure_ascii=True).replace('<','\\u003c')
  hashes.add("'sha256-"+base64.b64encode(hashlib.sha256(data.encode()).digest()).decode()+"'")
  page=template.replace('{{robots}}','noindex, follow' if path=='404' else 'index, follow, max-image-preview:large').replace('{{structured_data}}','<script type="application/ld+json">'+data+'</script>').replace('{{title}}',html.escape(title)).replace('{{description}}',html.escape(description)).replace('{{url}}',url).replace('{{content}}',(SITE/'pages'/file).read_text())
  target=OUT/'404.html' if path=='404' else OUT/path/'index.html';target.parent.mkdir(parents=True,exist_ok=True);target.write_text(page)
 (OUT/'_headers').write_text((SITE/'_headers').read_text().replace("script-src 'self'","script-src 'self' "+' '.join(sorted(hashes))))
 (OUT/'robots.txt').write_text('User-agent: *\nAllow: /\nSitemap: https://xur.app/sitemap.xml\n')
 (OUT/'sitemap.xml').write_text('<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">'+''.join('<url><loc>https://xur.app/'+(p+'/' if p else '')+'</loc></url>' for p in PAGES if p!='404')+'</urlset>')
 files=[p for p in OUT.rglob('*') if p.is_file()]
 assert len(files)<20000 and all(p.stat().st_size<=25*1024**2 for p in files),'Static site asset budget exceeded'
 print(json.dumps({'files':len(files),'bytes':sum(p.stat().st_size for p in files),'output':str(OUT)}))
if __name__=='__main__':build()
