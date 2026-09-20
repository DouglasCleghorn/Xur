const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
  const context=await browser.newContext({permissions:['clipboard-read','clipboard-write']});const page=await context.newPage();
  let targets=[{workload:{name:'Qwen <script>unsafe</script>',route:'chat',gpus:['0000:81:00.0'],recipe:{name:'Qwen',engine:'vLLM',hub:{repository:'owner/model'}}},instance:{instanceId:'ready-1'}}],fail=false,requests=0;
  await page.route('https://endpoints.test/**',async r=>{
   const u=new URL(r.request().url());
   if(u.pathname==='/api/model-lab/targets'){requests++;assert.equal(r.request().headers().requestverificationtoken,'fixture-token');return r.fulfill({status:fail?503:200,contentType:'application/json',body:JSON.stringify(fail?{}:targets)});}
   if(/\.(css|js|ttf|svg)$/.test(u.pathname))return r.fulfill({path:path.resolve('src/Xur.Control/wwwroot'+u.pathname)});
   const html=fs.readFileSync('.build/fast/control-panel/endpoints.html','utf8').replace('</head>','<meta name="xur-csrf" content="fixture-token"><script src="/app-fetch.js"></script></head>');
   return r.fulfill({contentType:'text/html',body:html});
  });
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.goto('https://endpoints.test/endpoints');await page.getByText('1 ready model',{exact:false}).waitFor();
   assert.equal(await page.getByLabel('Base URL',{exact:true}).inputValue(),'https://endpoints.test/inference/chat/v1');
   assert.equal(await page.getByLabel('Chat completions',{exact:true}).inputValue(),'https://endpoints.test/inference/chat/v1/chat/completions');
   assert.equal(await page.getByRole('link',{name:'Open Model list'}).getAttribute('href'),'https://endpoints.test/inference/chat/v1/models');
   assert.equal(await page.locator('.endpoint-card script').count(),0);
   await page.getByRole('button',{name:'Copy Base URL',exact:true}).click();assert.equal(await page.evaluate(()=>navigator.clipboard.readText()),'https://endpoints.test/inference/chat/v1');
   assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
   await page.screenshot({path:'.build/fast/control-panel/endpoints-'+label+'.png',fullPage:true});
  }
  targets=[];await page.getByRole('button',{name:'Refresh',exact:true}).click();await page.getByText('No ready LLM endpoints.',{exact:false}).waitFor();assert.equal(await page.locator('.endpoint-card').count(),0);
  fail=true;await page.getByRole('button',{name:'Refresh',exact:true}).click();await page.getByText('Could not refresh endpoints. Try again.',{exact:true}).waitFor();
  fail=false;await page.getByRole('button',{name:'Refresh',exact:true}).click();await page.getByText('No ready LLM endpoints.',{exact:false}).waitFor();
  console.log(JSON.stringify({suite:'EndpointsUi',result:'Passed',requests,copy:true,desktopAndMobile:true,emptyAndErrorRecovery:true,antiforgeryPolling:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
