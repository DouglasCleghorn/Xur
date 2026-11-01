const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const output=path.resolve(process.env.XUR_RENDER_OUTPUT||'.build/evidence/model-ui');
 const html=fs.readFileSync(path.join(output,'models.html'),'utf8');
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage(),errors=[],requests=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('http://models.test/**',async route=>{
   const url=new URL(route.request().url());
   if(route.request().method()==='POST'){
    requests.push({path:url.pathname,form:new URLSearchParams(route.request().postData())});
    return route.fulfill({body:'<!doctype html><p>Fixture deletion received.</p>',contentType:'text/html'});
   }
   if(url.pathname==='/models')return route.fulfill({body:html,contentType:'text/html'});
   const file=path.resolve('src/Xur.Control/wwwroot'+url.pathname);
   if(!fs.existsSync(file))return route.fulfill({status:404});
   return route.fulfill({path:file,contentType:url.pathname.endsWith('.js')?'application/javascript':url.pathname.endsWith('.css')?'text/css':undefined});
  });
  for(const [width,height] of [[1440,1000],[390,844]]){
   await page.setViewportSize({width,height});await page.goto('http://models.test/models');
   const gguf=page.getByRole('button',{name:'Delete model GGUF fixture <safe>',exact:true});
   const hf=page.getByRole('button',{name:'Delete model owner/fixture',exact:true});
   const external=page.getByRole('button',{name:'Delete model External fixture',exact:true});
   assert(await external.isDisabled());
   await page.locator('#model-filter').fill('owner/fixture');
   assert(!(await gguf.isVisible())&&await hf.isVisible());
   await page.locator('#model-filter').fill('');await gguf.click();
   const dialog=page.getByRole('dialog');assert(await dialog.isVisible());
   assert.equal(await dialog.locator('h2').textContent(),'Delete GGUF fixture <safe>?');
   assert.equal(await dialog.locator('h2 safe').count(),0);
   assert(!(await page.locator('#delete-model-repository').isVisible()));
   assert.equal(await dialog.locator('[name=path]').inputValue(),'/var/lib/xur/models/fixture.gguf');
   await dialog.getByRole('button',{name:'Cancel',exact:true}).click();assert(!(await dialog.isVisible()));assert.equal(requests.length,0);
   await hf.click();assert(await page.locator('#delete-model-repository').isVisible());
   const target=await dialog.locator('[name=deletePath]').inputValue();assert(target.endsWith('/models--owner--fixture'));
   assert.equal(await dialog.locator('[name=confirm]').inputValue(),'true');
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Models page fits viewport: '+JSON.stringify(await page.evaluate(()=>[...document.querySelectorAll('main *')].filter(el=>el.getBoundingClientRect().right>innerWidth+1).map(el=>({tag:el.tagName,class:el.className,width:el.getBoundingClientRect().width})))));
   assert(await dialog.evaluate(el=>el.getBoundingClientRect().right<=innerWidth),'Confirmation fits viewport');
   await page.keyboard.press('Escape');assert(!(await dialog.isVisible()));
  }
  await page.getByRole('button',{name:'Delete model owner/fixture',exact:true}).click();
  await Promise.all([page.waitForURL('**/models/delete'),page.getByRole('dialog').getByRole('button',{name:'Delete model',exact:true}).click()]);
  assert.equal(requests.length,1);assert.equal(requests[0].path,'/models/delete');
  assert.equal(requests[0].form.get('confirm'),'true');assert.equal(requests[0].form.get('__RequestVerificationToken'),'fixture-only');
  assert(requests[0].form.get('deletePath').endsWith('/models--owner--fixture'));
  assert(requests[0].form.get('path').endsWith('/snapshots/revision'));
  assert.deepEqual(errors,[]);
  console.log(JSON.stringify({suite:'ModelsUi',result:'Passed',desktopAndMobile:true,confirmationAndCancel:true,repositoryWarning:true,csrfAndConfirmedTarget:true,externalProtected:true}));
 }finally{await browser.close();}
})().catch(error=>{console.error(error.stack||error);process.exitCode=1;});
