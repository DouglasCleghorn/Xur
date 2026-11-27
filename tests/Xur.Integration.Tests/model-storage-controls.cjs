const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.route('https://models.test/**',route=>{
   const url=new URL(route.request().url());
   if(url.pathname.startsWith('/api/'))return route.fulfill({json:{topology:{links:[]}}});
   const asset=/\.(js|css|ttf|svg)$/.test(url.pathname);
   return route.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+url.pathname:'.build/fast/control-panel'+url.pathname+'.html'),contentType:url.pathname.endsWith('.js')?'application/javascript':url.pathname.endsWith('.css')?'text/css':url.pathname.endsWith('.ttf')?'font/ttf':url.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
  });
  for(const name of ['models','profile-edit']){
   await page.goto('https://models.test/'+name);
   await page.getByText('Model download storage',{exact:true}).click();
   const picker=page.getByLabel('Download location',{exact:true});
   await picker.selectOption('smb-'+'b'.repeat(32));
   assert.equal(await picker.inputValue(),'smb-'+'b'.repeat(32));
   assert(await picker.locator('option[value="smb-'+'a'.repeat(32)+'"]').isDisabled());
   const link=page.locator('#data-storage a').filter({hasText:'/var/lib/xur/models'});
   assert.equal(new URL(await link.getAttribute('href'),'https://models.test').searchParams.get('location'),'/var/lib/xur/models');
   if(name==='profile-edit'){
    const row=page.locator('.workload-editor').first();
    await row.locator('.catalog-engine').selectOption('llama.cpp',{force:true});
    await row.locator('[name=contextTokens]').fill('32768');
    assert.equal(await page.locator('#profile-form').evaluate(f=>new FormData(f).get('contextTokens')),'32768');
   }
  }
  await page.goto('https://models.test/storage');
  assert.equal(await page.locator('#storage-default option[value^="smb-"]').count(),0);
  assert.equal(await page.locator('#storage-unsloth option[value^="smb-"]').count(),0);
  assert.equal(await page.locator('#storage-models option[value^="smb-"]').count(),2);
  assert.deepEqual(errors,[]);
  console.log('Model download storage offers writable shares, rejects read-only shares, links folders, and preserves serving context form values.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
