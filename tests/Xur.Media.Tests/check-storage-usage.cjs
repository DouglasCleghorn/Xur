const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';const jwt=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8').match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const c=await browser.newContext();await c.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);const page=await c.newPage();
  const r=await c.request.post(base+'/api/storage/usage/refresh',{headers:{Authorization:'Bearer '+jwt},data:{}});assert(r.ok());
  let data;for(let n=0;n<180;n++){data=await(await c.request.get(base+'/api/storage/usage')).json();assert(!data.error,data.error);if(!data.scanning)break;assert((await c.request.get(base+'/health')).ok());await page.waitForTimeout(1000);}assert(!data.scanning);
  assert(data.filesystems.some(f=>f.mounts.includes('/var')&&f.bytes>0&&f.used>0));assert.equal(new Set(data.filesystems.map(f=>f.source)).size,data.filesystems.length);
  assert(data.disks.some(d=>d.path==='/dev/vda'));assert(data.disks.some(d=>d.path==='/dev/vdb'&&d.filesystems.length===0));
  for(const name of ['Models','Containers & engines','User files','Xur application versions']){const category=data.categories.find(c=>c.name===name);assert(category&&category.bytes>0&&!category.error,name+' was not measured');}
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844],['small-mobile',320,740]]){
   await page.setViewportSize({width,height});await page.goto(base+'/storage');assert.equal(await page.locator('h1').innerText(),'Storage');assert(await page.getByRole('button',{name:'Refresh usage'}).isVisible());assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));await page.screenshot({path:`.build/evidence/updates/storage-usage-${label}.png`,fullPage:true});
   if(width<700){await page.locator('.mobile-nav-more summary').click();assert(await page.getByRole('link',{name:'Storage',exact:true}).isVisible());await page.getByRole('link',{name:'Storage',exact:true}).click();}
  }
  await page.getByRole('button',{name:'Refresh usage'}).click();await page.waitForURL(base+'/storage');assert(await page.getByRole('status').isVisible());
  const anonymous=await browser.newContext();assert.equal((await anonymous.request.get(base+'/api/storage/usage')).status(),401);assert.equal((await anonymous.request.post(base+'/api/storage/usage/refresh',{data:{}})).status(),401);await anonymous.close();
  assert.equal((await c.request.post(base+'/api/storage/usage/refresh',{data:{}})).status(),400);
  const update=await(await c.request.get(base+'/api/application-updates')).json();const receipt={suite:'StorageUsage',result:'Passed',bundle:update.current.id,realFindmntAndLsblk:true,realFolderUsage:true,bindMountsDeduplicated:true,unmountedDiskShown:true,responsiveWhileMeasuring:true,manualRefresh:true,desktopAndMobile:true,authenticatedOnly:true,capturedAt:data.capturedAt,filesystems:data.filesystems,categories:data.categories};fs.writeFileSync('.build/evidence/updates/storage-usage.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify({suite:receipt.suite,result:receipt.result,bundle:receipt.bundle}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.stack);process.exitCode=1});
