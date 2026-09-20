// Install axe-core beside Playwright in ignored .build/browser (see website/README.md).
const {chromium}=require('../../.build/browser/node_modules/playwright');
const axe=require('../../.build/browser/node_modules/axe-core');
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true}),reports=[];
 try{
  const page=await browser.newPage();const root=path.resolve('website/dist');
  await page.route('https://api.github.com/**',r=>r.fulfill({json:[]}));
  await page.route('http://website.test/**',r=>{let p=new URL(r.request().url()).pathname;if(p.endsWith('/'))p+='index.html';const file=path.join(root,p);assert(file.startsWith(root+path.sep));const types={'.html':'text/html','.css':'text/css','.js':'text/javascript','.svg':'image/svg+xml','.jpg':'image/jpeg','.png':'image/png','.ttf':'font/ttf'};return r.fulfill({body:fs.readFileSync(file),contentType:types[path.extname(file)]||'text/plain'});});
  for(const width of [1440,390])for(const route of ['/','/download/','/guides/','/guides/getting-started/','/guides/models/','/guides/workstations/','/guides/updates/','/guides/network-and-answers/','/404.html']){
   await page.setViewportSize({width,height:1000});await page.goto('http://website.test'+route);await page.evaluate(()=>document.fonts.ready);await page.evaluate(axe.source);
   const result=await page.evaluate(()=>axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21aa','wcag22aa','best-practice']}}));
   reports.push({route,width,violations:result.violations});
  }
  fs.writeFileSync('.build/website/accessibility.json',JSON.stringify(reports,null,2));
  assert.deepEqual(reports.flatMap(r=>r.violations.map(v=>({route:r.route,width:r.width,id:v.id,nodes:v.nodes.map(n=>n.target)}))),[]);
  console.log(JSON.stringify({suite:'WebsiteAccessibility',result:'Passed',pages:9,widths:[1440,390],engine:axe.version,standard:'Automated WCAG 2.2 AA checks; not a conformance certification'}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
