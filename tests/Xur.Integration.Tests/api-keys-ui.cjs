const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/fast/api-keys');fs.mkdirSync(out,{recursive:true});
 execFileSync(path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--control-panel-render',out],{stdio:'pipe'});
 const html=fs.readFileSync(path.join(out,'api-keys.html'),'utf8').replace(/(<form id="api-key-create"[^>]*>)/,'$1<input name="__RequestVerificationToken" value="fixture-token">');
 let keys=[],secret='test-only-secret',posts=[];
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('http://keys.test/**',async r=>{
   const u=new URL(r.request().url());const json=data=>r.fulfill({contentType:'application/json',body:JSON.stringify(data)});
   if(u.pathname.startsWith('/icons/'))return r.fulfill({path:path.resolve('src/Xur.Control/wwwroot'+u.pathname)});
   if(u.pathname==='/setup.css'||u.pathname==='/api-keys.js')return r.fulfill({path:path.resolve('src/Xur.Control/wwwroot'+u.pathname)});
   if(r.request().method()==='POST'){
    assert.equal(r.request().headers().requestverificationtoken,'fixture-token');posts.push(u.pathname);
    if(u.pathname.endsWith('/revoke')){keys[0].revokedAt=new Date().toISOString();return json({});}
    const body=r.request().postDataJSON();keys=[{...body,id:'test-key',createdAt:new Date().toISOString(),expiresAt:new Date(Date.now()+86400000).toISOString(),requests:0}];return json({key:keys[0],token:secret});
   }
   if(u.pathname==='/api/api-keys')return json(keys);
   return r.fulfill({contentType:'text/html',body:html});
  });
  await page.goto('http://keys.test/settings/api-keys');await page.getByText('No API keys yet.').waitFor();
  await page.locator('[name=name]').fill('<img src=x onerror=alert(1)>');await page.getByRole('button',{name:'Create key',exact:true}).click();
  await page.locator('#api-key-created:not([hidden])').waitFor();assert.equal(await page.locator('#api-key-secret').inputValue(),secret);
  assert.equal(await page.locator('#api-key-list img').count(),0);
  await page.getByRole('button',{name:'Done',exact:true}).click();assert.equal(await page.locator('#api-key-secret').inputValue(),'');
  await page.setViewportSize({width:390,height:844});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'Mobile page must not overflow');
  page.on('dialog',d=>d.accept());await page.getByRole('button',{name:'Revoke',exact:true}).click();await page.getByText('Key revoked.',{exact:true}).waitFor();
  assert.equal(await page.getByRole('button',{name:'Revoke',exact:true}).count(),0);assert.equal(posts.length,2);assert.equal(errors.length,0);
  await page.screenshot({path:path.join(out,'mobile.png'),fullPage:true});
 }finally{await browser.close();}
 console.log(JSON.stringify({suite:'ApiKeysUI',result:'Passed',mobile:true,oneTimeSecret:true,revocation:true}));
})().catch(e=>{console.error(e);process.exit(1);});
