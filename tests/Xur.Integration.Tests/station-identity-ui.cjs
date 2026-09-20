const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('https://stations.test/**',r=>{
   const u=new URL(r.request().url());if(u.pathname.startsWith('/api/'))return r.fulfill({contentType:'application/json',body:'{"topology":{"links":[]}}'});
   const asset=/\.(css|js|ttf)$/.test(u.pathname);
   return r.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+u.pathname:'.build/fast/control-panel/profile-edit.html'),contentType:u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.ttf')?'font/ttf':'text/html'});
  });
  for(const width of [1440,390]) {
   await page.setViewportSize({width,height:1000});await page.goto('https://stations.test/profile-edit');
   assert.equal(await page.locator('[name=stationId]').inputValue(),'w1');
   assert.equal(await page.locator('[name=stationName]').inputValue(),'Gaming');
   assert(await page.locator('.station-user-choice').isHidden());
   await page.getByRole('combobox',{name:'Workstation',exact:true}).click();
   await page.getByRole('option',{name:'New workstation',exact:true}).click();
   assert.equal(await page.locator('[name=stationId]').inputValue(),'');assert(await page.locator('.station-user-choice').isVisible());
   await page.locator('[name=stationName]').fill('Another desktop');
   await page.getByRole('combobox',{name:'Workstation',exact:true}).click();await page.getByRole('option',{name:'Gaming · w1',exact:true}).click();
   assert.equal(await page.locator('[name=stationName]').inputValue(),'Gaming');assert.equal(await page.locator('[name=stationUser]').inputValue(),'legacy');
   await page.getByRole('button',{name:'Add workload',exact:true}).click();
   assert.equal(await page.locator('[name=stationId]').nth(1).inputValue(),'');assert.equal(await page.locator('[name=stationName]').nth(1).inputValue(),'');
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Profile editor overflow at '+width);
   await page.screenshot({path:'.build/fast/control-panel/station-identity-'+width+'.png',fullPage:true});
  }
  assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'StationIdentityUi',result:'Passed',reuseAndCreate:true,desktopAndMobile:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
