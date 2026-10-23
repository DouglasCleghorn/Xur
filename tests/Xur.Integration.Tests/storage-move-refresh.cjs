const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
  for(const fail of [false,true]){
   const page=await browser.newPage();let visits=0,job=null,finishUsage,finishUnload;
   const events=[],usage=new Promise(resolve=>finishUsage=resolve),unload=new Promise(resolve=>finishUnload=resolve);
   await page.route('https://storage.test/**',async route=>{
    const path=new URL(route.request().url()).pathname;
    const json=value=>route.fulfill({json:value});
    if(path.endsWith('.js'))return route.fulfill({body:fs.readFileSync('src/Xur.Control/wwwroot'+path),contentType:'application/javascript'});
    if(path==='/storage'){
     visits++;return route.fulfill({contentType:'text/html',body:`<section id="data-storage"><form data-group="default"><input name="__RequestVerificationToken" value="fixture"><select name="destination"><option value="a111-b222">Data drive</option></select><button>Move</button></form><p id="storage-move-status"></p></section><script src="/storage-locations.js"></script><script src="/storage-usage.js" data-scanning="true"></script>`});
    }
    if(path==='/api/storage/usage'){await usage;return json({scanning:false});}
    if(route.request().method()==='POST'){
     events.push(path);
     if(path==='/api/profiles/unload/preview')return json({id:'plan',digest:'digest'});
     if(path==='/api/storage/locations'){
      if(fail)return route.fulfill({status:500,json:{error:'Fixture copy failure'}});
      job={id:'move',stage:'Complete'};
     }
     return json({});
    }
    if(path==='/api/storage/locations')return json({busy:false,job});
    if(path==='/api/profiles')return json({active:{id:'8'}});
    if(path==='/api/profiles/progress'){await unload;return json({stage:'Complete'});}
    throw Error('Unexpected request: '+path);
   });
   await page.goto('https://storage.test/storage');
   await page.getByRole('button',{name:'Move',exact:true}).click();
   await page.waitForFunction(()=>document.querySelector('#data-storage').dataset.moving==='true');
   const completed=page.waitForResponse('https://storage.test/api/storage/usage');finishUsage();await completed;
   await page.waitForTimeout(100);assert.equal(visits,1,'An in-flight usage response must not reload during unload');
   finishUnload();
   if(fail){
    await page.getByText(/Fixture copy failure/).waitFor();
    assert.equal(await page.locator('#data-storage').getAttribute('data-moving'),'false');
    assert(await page.getByRole('button',{name:'Move',exact:true}).isEnabled());
    assert(!events.includes('/api/profiles/8/load'));
   }else{
    await page.waitForEvent('request',{predicate:r=>r.url()==='https://storage.test/storage'&&r.isNavigationRequest()});
    assert.deepEqual(events,['/api/profiles/unload/preview','/api/profiles/apply','/api/storage/locations','/api/profiles/8/load']);
   }
   await page.close();
  }
  console.log(JSON.stringify({suite:'StorageMoveRefresh',result:'Passed',inFlightUsage:true,profileReload:true,failureRecovery:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
