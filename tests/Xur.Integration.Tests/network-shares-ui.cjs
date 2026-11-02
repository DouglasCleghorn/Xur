// Render fixtures first with Xur.Unit.Tests --control-panel-render .build/evidence/network-shares-render.
// This test takes no screenshots and uses only synthetic credentials and addresses.
const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage(),requests=[];
  const html=fs.readFileSync((process.env.XUR_RENDER_OUTPUT||'.build/evidence/network-shares-render')+'/network-shares.html','utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'')+'<script src="/network-shares.js"></script>';
  await page.route('https://smb.test/**',route=>{
   const request=route.request(),path=new URL(request.url()).pathname;
   if(request.method()==='POST'){requests.push({path,headers:request.headers(),body:request.postDataJSON()});return route.fulfill({contentType:'application/json',body:'{}'});}
   if(path==='/storage/network')return route.fulfill({contentType:'text/html',body:html});
   const file='src/Xur.Control/wwwroot'+path;
   return route.fulfill({body:fs.existsSync(file)?fs.readFileSync(file):'',contentType:path.endsWith('.js')?'application/javascript':path.endsWith('.css')?'text/css':'application/octet-stream'});
  });
  for(const width of [1440,390]){
   await page.setViewportSize({width,height:900});await page.goto('https://smb.test/storage/network');
   await page.getByRole('heading',{name:'Connected shares'}).waitFor();
   assert.equal(await page.locator('input[type=password]').count(),2);
   assert.equal(await page.locator('form:has(input[type=password])').evaluateAll(forms=>forms.every(f=>f.method==='post')),true,'Password forms never fall back to a GET');
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'No page overflow at '+width);
  }
  await page.getByRole('button',{name:'Test access',exact:true}).click();
  await page.getByText('SMB login and filesystem access verified.',{exact:true}).waitFor();
  assert.equal(requests.at(-1).body.action,'test');
  await page.getByText('Add connection',{exact:true}).click();
  const form=page.locator('#smb-connection-form');
  for(const [name,value] of Object.entries({name:'Test NAS',server:'nas.test',share:'data',username:'fixture-user',password:'synthetic-password',domain:'LAB'}))await form.locator('[name='+name+']').fill(value);
  await form.getByRole('button',{name:'Save connection'}).click();
  await page.waitForFunction(()=>document.querySelector('#smb-connection-form [name=password]').value==='');
  const saved=requests.find(r=>r.path==='/api/storage/network/connections');
  assert.equal(saved.body.password,'synthetic-password');assert.equal(saved.body.uid,1000);assert.equal(saved.body.readOnly,true);assert.equal(saved.body.encrypt,true);
  assert(saved.headers.requestverificationtoken,'Mutations include CSRF token');
  assert(!page.url().includes('synthetic-password'));
  console.log(JSON.stringify({suite:'NetworkSharesUi',result:'Passed',desktopAndMobile:true,csrf:true,passwordPostOnly:true,testFeedback:true}));
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
