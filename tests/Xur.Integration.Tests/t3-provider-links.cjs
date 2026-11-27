const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('node:child_process'),fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
(async()=>{
 const out=path.resolve('.build/evidence/t3-provider-links');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--artifacts-path','.build/dotnet','--','--control-panel-render',out],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();let instance='codex',multiple=false,limit=32768;const connections=[],errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('http://t3-setup.test/**',route=>{
   const request=route.request(),url=new URL(request.url());
   if(url.pathname==='/api/t3-code/status')return route.fulfill({json:{state:'Running',installed:true,enabled:true}});
   if(url.pathname==='/api/t3-code/setup'){
    const body=request.postDataJSON();return route.fulfill({json:{state:body.action==='status'?'Signed in':'Succeeded',message:'Fixture account',providerInstanceId:body.provider==='codex'?instance:null}});
   }
   if(url.pathname==='/api/t3-code/models')return route.fulfill({json:{models:[{workloadId:'12',name:'Local model',engine:'llama.cpp',state:'Ready',profiles:['Coding'],modelIds:multiple?['model','second/model']:['model'],contextLimits:{model:limit,'second/model':16384}}],connections:[]}});
   if(url.pathname==='/api/t3-code/connect'){connections.push(request.postDataJSON());return route.fulfill({json:{id:'xur_fixture'}});}
   if(url.pathname.startsWith('/api/'))return route.fulfill({json:{}});
   const asset=/\.(js|css|ttf|svg)$/.test(url.pathname);
   return route.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+url.pathname:path.join(out,'t3-code.html')),
    contentType:url.pathname.endsWith('.js')?'application/javascript':url.pathname.endsWith('.css')?'text/css':url.pathname.endsWith('.ttf')?'font/ttf':url.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
  });
  await page.goto('http://t3-setup.test/');
  const card=page.locator('[data-provider="codex"]');
  await card.getByRole('button',{name:'Sign in again',exact:true}).waitFor();
  assert.equal(new URL(await card.getByRole('link',{name:'Open Codex in T3'}).getAttribute('href')).searchParams.get('instanceId'),'codex');
  instance='personal_codex';
  await card.getByRole('button',{name:'Sign in again',exact:true}).click();
  await card.getByRole('link',{name:'Continue in T3'}).waitFor();
  for(const name of ['Open Codex in T3','Continue in T3'])
   assert.equal(new URL(await card.getByRole('link',{name,exact:true}).getAttribute('href')).searchParams.get('instanceId'),instance);
  await page.reload();
  await card.getByRole('button',{name:'Sign in again',exact:true}).waitFor();
  assert.equal(new URL(await card.getByRole('link',{name:'Open Codex in T3'}).getAttribute('href')).searchParams.get('instanceId'),instance);
  const row=page.locator('.t3-model'),model=row.getByLabel('Served model'),context=row.getByLabel('Context limit',{exact:false});
  assert.equal(await model.inputValue(),'model');assert(await model.getAttribute('readonly')!==null);
  assert.equal(await context.inputValue(),'');assert.match(await context.getAttribute('placeholder'),/32,768/);
  await row.getByRole('button',{name:'Connect to T3',exact:true}).click();
  await row.getByRole('link',{name:'Open model in T3'}).waitFor();
  assert.equal(connections[0].modelId,'model');assert.equal(connections[0].contextTokens,0);
  multiple=true;await page.getByRole('button',{name:'Refresh models',exact:true}).click();
  await row.locator('select').first().selectOption('second/model');
  assert.equal(await context.getAttribute('max'),'16384');
  await context.fill('8192');await row.getByRole('button',{name:'Connect to T3',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('.t3-model').textContent.includes('Connected.'));
  assert.equal(connections[1].modelId,'second/model');assert.equal(connections[1].contextTokens,8192);
  multiple=false;limit=0;await page.getByRole('button',{name:'Refresh models',exact:true}).click();
  await row.locator('input[readonly]').waitFor();
  assert(await context.getAttribute('required')!==null);
  await context.fill('24576');await row.getByRole('button',{name:'Connect to T3',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('.t3-model').textContent.includes('Connected.'));
  assert.equal(connections[2].contextTokens,24576);
  assert.deepEqual(errors,[]);
  console.log('T3 provider links follow the reused instance after sign-in and page reload.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
