const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage({acceptDownloads:true});
  const errors=[],violations=[];page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(()=>{window.cspViolations=[];document.addEventListener('securitypolicyviolation',e=>window.cspViolations.push(e.violatedDirective));});
  const root=path.resolve('website/dist');
  const csp=fs.readFileSync(root+'/_headers','utf8').split('\n').find(l=>l.includes('Content-Security-Policy:')).trim().slice('Content-Security-Policy: '.length);
  const base='https://github.com/DouglasCleghorn/Xur/releases/';
  let releases=[],failure=false,requested=[];
  await page.route('https://api.github.com/**',r=>{assert.equal(r.request().headers().authorization,undefined);return r.fulfill({status:failure?403:200,json:failure?{message:'Rate limited'}:releases});});
  await page.route('https://github.com/**',r=>{requested.push(r.request().url());return r.fulfill({headers:{'Content-Disposition':'attachment; filename="xur-installer-x86_64.iso"'},contentType:'application/octet-stream',body:'fixture only'});});
  await page.route('https://website.test/**',r=>{
   let file=new URL(r.request().url()).pathname;if(file.endsWith('/'))file+='index.html';file=path.join(root,file);assert(file.startsWith(root+path.sep));
   const types={'.html':'text/html','.css':'text/css','.js':'text/javascript','.svg':'image/svg+xml','.ttf':'font/ttf','.png':'image/png','.jpg':'image/jpeg'};
   return r.fulfill({body:fs.readFileSync(file),contentType:types[path.extname(file)]||'text/plain',headers:{'Content-Security-Policy':csp}});
  });
  function release(tag,date,pre=false,split=false){return {name:'Xur '+tag,tag_name:tag,prerelease:pre,draft:false,published_at:date,html_url:base+'tag/'+tag,assets:[{name:split?'xur-installer-x86_64.iso.part001':'xur-installer-x86_64.iso',size:1024**3,browser_download_url:base+'download/'+tag+'/xur-installer-x86_64.iso'}]};}
  releases=[release('v0.1.0','2026-09-10T00:00:00Z'),release('nightly-test','2026-09-20T00:00:00Z',true)];
  await page.goto('https://website.test/download/');await page.getByText('Your online installer is ready.',{exact:false}).waitFor();
  assert.equal(requested.length,0,'Reading instructions must not auto-download');
  assert((await page.locator('#download-meta').textContent()).includes('Nightly / pre-release'));
  const received=page.waitForEvent('download');await page.goto('https://website.test/download/?start=1');await received;
  assert.equal(requested.length,1);assert(requested[0].includes('/nightly-test/'));
  await page.getByText('Download requested.',{exact:false}).waitFor();
  for(const [channel,tag] of [['stable','v1.2.3'],['nightly','nightly-1.2.3']]){
   const name=`xur-${channel}-1.2.3-x86_64.iso`;
   releases=[release(tag,'2026-09-21T00:00:00Z',channel==='nightly')];
   releases[0].assets[0].name=name;releases[0].assets[0].browser_download_url=base+'download/'+tag+'/'+name;
   await page.goto('https://website.test/download/');await page.getByText('Your online installer is ready.',{exact:false}).waitFor();
   assert((await page.locator('#download-iso').getAttribute('href')).endsWith('/'+name));
  }
  for(const [channel,version] of [['stable','26.09.1'],['nightly','26.09.001']]){
   const tag=(channel==='stable'?'v':'nightly-')+version;
   const name=`xur-${channel}-${version}-x86_64.iso`;
   releases=[release(tag,'2026-09-22T00:00:00Z',channel==='nightly')];
   releases[0].assets[0].name=name;releases[0].assets[0].browser_download_url=base+'download/'+tag+'/'+name;
   await page.goto('https://website.test/download/');await page.getByText('Your online installer is ready.',{exact:false}).waitFor();
   assert((await page.locator('#download-iso').getAttribute('href')).endsWith('/'+name));
   assert((await page.locator('#download-meta').textContent()).includes(version));
  }
  releases=[];await page.goto('https://website.test/download/?start=1');await page.getByText('No newer installer was found.',{exact:false}).waitFor();assert(await page.locator('#download-iso').isVisible());assert.equal(requested.length,1);
  failure=true;await page.locator('#download-retry').click();await page.getByText('Could not check GitHub', {exact:false}).waitFor();failure=false;
  releases=[release('split','2026-09-20T00:00:00Z',false,true)];await page.locator('#download-retry').click();await page.getByText('No newer installer was found.',{exact:false}).waitFor();assert(await page.locator('#download-parts').isHidden());assert.equal(requested.length,1);
  releases=[release('bad','2026-09-20T00:00:00Z')];releases[0].assets[0].browser_download_url='https://example.com/installer.iso';await page.goto('https://website.test/download/?start=1');await page.getByText('Could not check GitHub',{exact:false}).waitFor();assert(await page.locator('#download-iso').isVisible());
  const noScript=await browser.newContext({javaScriptEnabled:false});
  const staticPage=await noScript.newPage();
  await staticPage.route('https://website.test/**',r=>{let file=new URL(r.request().url()).pathname;if(file.endsWith('/'))file+='index.html';return r.fulfill({body:fs.readFileSync(path.join(root,file)),contentType:file.endsWith('.html')?'text/html':'text/plain'});});
  await staticPage.goto('https://website.test/download/');assert(await staticPage.locator('#download-iso').isVisible());assert((await staticPage.locator('#download-iso').getAttribute('href')).endsWith('/xur-installer-x86_64.iso'));await noScript.close();
  assert.deepEqual(await page.evaluate(()=>window.cspViolations),[]);assert.deepEqual(errors,[]);
  console.log(JSON.stringify({suite:'WebsiteDownload',result:'Passed',automaticAndManual:true,noIsoAndRateLimit:true,multipart:true,externalUrlRejected:true,productionCsp:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
