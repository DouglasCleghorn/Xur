const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const root=path.resolve('website/dist'),evidence='.build/evidence/website';fs.mkdirSync(evidence,{recursive:true});
 const browser=await chromium.launch({headless:true});
 try {
  const page=await browser.newPage(), errors=[];page.on('pageerror',e=>errors.push(e.message));
  const unexpectedOrigins = new Set();
  function serveSite(route) {
    const url = new URL(route.request().url());
    if (url.origin === 'https://api.github.com' && new URL(route.request().frame().url()).pathname === '/download/') {
      return route.fulfill({json:[]});
    }
    if (url.origin !== 'http://website.test') {
      unexpectedOrigins.add(url.origin);
      return route.abort();
    }
    const name = url.pathname.endsWith('/') ? url.pathname + 'index.html' : url.pathname;
    const file = path.join(root, name);
    assert(file.startsWith(root + path.sep));
    return fs.existsSync(file) ? route.fulfill({path:file}) : route.fulfill({status:404,body:'Missing file'});
  }
  await page.route('**/*', serveSite);
  for(const width of [1440,768,390,320]){
   await page.setViewportSize({width,height:1000});await page.goto('http://website.test/');
   assert.equal(await page.locator('.header').getByRole('link',{name:'Profiles',exact:true}).count(),0);
   assert.equal(await page.locator('.github-icon').count(),1);
   await page.getByRole('button',{name:'Model serving',exact:true}).click();
   assert((await page.locator('[data-slot="1"]').textContent()).startsWith('Speech generation'));
   assert.equal(await page.getByRole('button',{name:'Model serving',exact:true}).getAttribute('aria-pressed'),'true');
   await page.getByRole('button',{name:'Desktops + AI',exact:true}).click();
   assert((await page.locator('[data-slot="1"]').textContent()).startsWith('Workstation A'));
   const tabs = page.getByRole('tablist', {name:'Control panel views'});
   const coding = tabs.getByRole('tab', {name:'Code with T3'});
   await coding.focus();
   for (const title of ['Stream a desktop', 'Test a model', 'Share a folder', 'Code with T3']) {
     await page.keyboard.press('ArrowRight');
     const active = tabs.getByRole('tab', {selected:true});
     assert.equal(await active.innerText(),title);
     const panel = page.getByRole('tabpanel', {name:title});
     assert(await panel.isVisible());
     for (const img of await panel.locator('picture img').all()) {
       const source = await img.evaluate(async el => {
         el.loading = 'eager';
         await el.decode();
         return el.currentSrc;
       });
       assert.equal(source.endsWith('-phone.jpg'),width<=512,'Wrong screenshot for '+width);
     }
     assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),title+' overflow at '+width);
   }
   await tabs.getByRole('tab',{name:'Share a folder'}).click();
   await page.getByRole('link',{name:'Connect a shared folder'}).click();
   assert.equal(new URL(page.url()).pathname,'/guides/network-shares/');
   await page.goto('http://website.test/');
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Home overflow at '+width);
   await page.locator('img').evaluateAll(imgs=>Promise.all(imgs.map(img=>{img.loading='eager';return img.decode();})));
   await page.screenshot({path:evidence+'/home-'+width+'.png',fullPage:true});
   await page.getByRole('link',{name:'Get started',exact:true}).click();
   assert(await page.getByRole('heading',{name:'Set up Xur from your phone.',exact:true}).isVisible());
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Guide overflow at '+width);
   await page.locator('img').evaluateAll(imgs=>Promise.all(imgs.map(img=>{img.loading='eager';return img.decode();})));
   await page.screenshot({path:evidence+'/guide-'+width+'.png',fullPage:true});
  }
  function walk(dir){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(path.join(dir,e.name)):[path.join(dir,e.name)]);}
  for(const file of walk(root).filter(p=>p.endsWith('.html'))){
   const markup=fs.readFileSync(file,'utf8');assert(!markup.includes('{{'),'Unresolved template '+file);
   for(const data of markup.matchAll(/<script type="application\/ld\+json">(.*?)<\/script>/g))assert(JSON.parse(data[1])['@graph']);
   for(const match of fs.readFileSync(file,'utf8').matchAll(/(?:href|src|srcset)="(\/[^"]*)"/g)){
    let p=match[1].split(/[?#]/)[0];if(p.endsWith('/'))p+='index.html';assert(fs.existsSync(path.join(root,p)),'Broken link '+match[1]);
   }
  }
  for(const route of ['/download/','/guides/','/guides/setup/','/guides/getting-started/','/guides/models/','/guides/workstations/','/guides/t3-code/','/guides/network-shares/','/guides/network-and-answers/','/guides/updates/']){
   for(const width of [1440,390,320]){
    await page.setViewportSize({width,height:1000});await page.goto('http://website.test'+route);
    assert.equal(await page.locator('h1').count(),1);assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),route+' overflow '+width);
    assert((await page.locator('link[rel=canonical]').getAttribute('href')).startsWith('https://xur.app/'));
    await page.locator('img').evaluateAll(imgs=>Promise.all(imgs.map(img=>{img.loading='eager';return img.decode();})));
   await page.screenshot({path:evidence+'/'+route.split('/').filter(Boolean).join('-')+'-'+width+'.png',fullPage:true});
   }
  }
  await page.goto('http://website.test/');await page.keyboard.press('Tab');assert.equal(await page.locator(':focus').textContent(),'Skip to content');await page.keyboard.press('Enter');assert.equal(await page.locator(':focus').getAttribute('id'),'main');
  const plain = await browser.newPage({javaScriptEnabled:false,viewport:{width:390,height:844}});
  await plain.route('**/*', serveSite);
  await plain.goto('http://website.test/');
  assert.equal(await plain.locator('.tour-view:visible').count(),4);
  assert(await plain.locator('.tour-tabs').isHidden());
  assert(await plain.locator('.profile-buttons').isHidden());
  await plain.getByRole('link',{name:'Set up T3 Code'}).click();
  assert.equal(new URL(plain.url()).pathname,'/guides/t3-code/');
  await plain.close();
  assert.deepEqual([...unexpectedOrigins],[]);
  assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'WebsiteUi',result:'Passed',responsiveWidths:[1440,768,390,320],localLinks:true,profileInteraction:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
