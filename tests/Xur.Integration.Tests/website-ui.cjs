const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const root=path.resolve('website/dist');fs.mkdirSync('.build/website',{recursive:true});
 const browser=await chromium.launch({headless:true});
 try {
  const page=await browser.newPage(), errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('http://website.test/**',route=>{
   let p=new URL(route.request().url()).pathname;if(p.endsWith('/'))p+='index.html';
   const file=path.join(root,p);assert(file.startsWith(root+path.sep));
   const types={'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png','.jpg':'image/jpeg','.ttf':'font/ttf'};
   return route.fulfill({status:fs.existsSync(file)?200:404,body:fs.existsSync(file)?fs.readFileSync(file):'Missing file',contentType:types[path.extname(file)]||'text/plain'});
  });
  for(const width of [1440,768,390,320]){
   await page.route('https://api.github.com/**',r=>r.fulfill({json:[]}));
   await page.setViewportSize({width,height:1000});await page.goto('http://website.test/');
   assert.equal(await page.locator('.header').getByRole('link',{name:'Profiles',exact:true}).count(),0);
   assert.equal(await page.locator('.github-icon').count(),1);
   await page.getByRole('button',{name:'Model serving',exact:true}).click();
   assert((await page.locator('[data-slot="1"]').textContent()).startsWith('Speech generation'));
   assert.equal(await page.getByRole('button',{name:'Model serving',exact:true}).getAttribute('aria-pressed'),'true');
   await page.getByRole('button',{name:'Desktops + AI',exact:true}).click();
   assert((await page.locator('[data-slot="1"]').textContent()).startsWith('Workstation A'));
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Home overflow at '+width);
   await page.locator('img').evaluateAll(imgs=>Promise.all(imgs.map(img=>{img.loading='eager';return img.decode();})));
   await page.screenshot({path:'.build/website/home-'+width+'.png',fullPage:true});
   await page.getByRole('link',{name:'Get started',exact:true}).click();
   assert(await page.getByRole('heading',{name:'Start with one profile.',exact:true}).isVisible());
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Guide overflow at '+width);
   await page.locator('img').evaluateAll(imgs=>Promise.all(imgs.map(img=>{img.loading='eager';return img.decode();})));
   await page.screenshot({path:'.build/website/guide-'+width+'.png',fullPage:true});
  }
  function walk(dir){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(path.join(dir,e.name)):[path.join(dir,e.name)]);}
  for(const file of walk(root).filter(p=>p.endsWith('.html'))){
   const markup=fs.readFileSync(file,'utf8');assert(!markup.includes('{{'),'Unresolved template '+file);
   for(const data of markup.matchAll(/<script type="application\/ld\+json">(.*?)<\/script>/g))assert(JSON.parse(data[1])['@graph']);
   for(const match of fs.readFileSync(file,'utf8').matchAll(/(?:href|src)="(\/[^"]*)"/g)){
    let p=match[1].split(/[?#]/)[0];if(p.endsWith('/'))p+='index.html';assert(fs.existsSync(path.join(root,p)),'Broken link '+match[1]);
   }
  }
  for(const route of ['/download/','/guides/','/guides/models/','/guides/workstations/','/guides/updates/']){
   for(const width of [1440,390,320]){
    await page.setViewportSize({width,height:1000});await page.goto('http://website.test'+route);
    assert.equal(await page.locator('h1').count(),1);assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),route+' overflow '+width);
    assert((await page.locator('link[rel=canonical]').getAttribute('href')).startsWith('https://xur.app/'));
    await page.locator('img').evaluateAll(imgs=>Promise.all(imgs.map(img=>{img.loading='eager';return img.decode();})));
   await page.screenshot({path:'.build/website/'+route.split('/').filter(Boolean).join('-')+'-'+width+'.png',fullPage:true});
   }
  }
  await page.goto('http://website.test/');await page.keyboard.press('Tab');assert.equal(await page.locator(':focus').textContent(),'Skip to content');await page.keyboard.press('Enter');assert.equal(await page.locator(':focus').getAttribute('id'),'main');
  assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'WebsiteUi',result:'Passed',responsiveWidths:[1440,768,390,320],localLinks:true,profileInteraction:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
