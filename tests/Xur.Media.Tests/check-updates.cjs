const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';
 const raw=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8');
 const jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try {
  const context=await browser.newContext({viewport:{width:1440,height:1000}});
  await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage();await page.goto(base+'/updates');
  if(!await page.getByRole('heading',{name:'Operating system',exact:true}).count())throw Error('OS updates page missing');
  async function idle(){
   for(let i=0;i<180;i++){
    const response=await context.request.get(base+'/api/updates');if(!response.ok())throw Error('Update status unavailable');
    const status=await response.json();
    if(status.operation?.stage==='Failed')throw Error(status.operation.message);
    if(!status.busy)return status;
    await new Promise(r=>setTimeout(r,1000));
   }throw Error('OS operation timed out');
  }
  await page.getByRole('heading',{name:'Operating system',exact:true}).locator('..').getByRole('button',{name:'Check for updates',exact:true}).click();
  await new Promise(r=>setTimeout(r,2000));let state=await idle();
  if(state.operation?.action!=='check'||state.operation?.stage!=='Complete')throw Error('Real upstream check did not complete');
  await page.getByRole('button',{name:'Pause automatic updates',exact:true}).waitFor({state:'visible'});
  await page.waitForFunction(()=>{const button=[...document.querySelectorAll('button')].find(b=>b.textContent.trim()==='Pause automatic updates');return button && !button.disabled;},null,{timeout:20000});
  await page.getByRole('button',{name:'Pause automatic updates',exact:true}).click();await new Promise(r=>setTimeout(r,1000));state=await idle();
  if(state.automatic)throw Error('Pause did not persist');
  await page.reload();await page.getByRole('button',{name:'Enable automatic updates',exact:true}).click();await new Promise(r=>setTimeout(r,1000));state=await idle();
  if(!state.automatic)throw Error('Enable did not persist');
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.goto(base+'/updates');await page.screenshot({path:`.build/evidence/ui/updates-${label}.png`,fullPage:true});
   if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Updates page overflows viewport');
  }
  const anonymous=await browser.newContext();
  if((await anonymous.request.get(base+'/api/updates')).status()!==401)throw Error('Anonymous update status exposed');
  if((await anonymous.request.post(base+'/api/updates',{data:{action:'stage'}})).status()!==401)throw Error('Anonymous mutation accepted');
  if((await context.request.post(base+'/api/updates',{data:{action:'stage'}})).status()!==400)throw Error('Cookie API mutation bypassed CSRF');
  const receipt={suite:'UpdatesBrowser',result:'Passed',upstreamCheck:true,pauseAndEnable:true,authenticationAndCsrf:true,desktopAndMobile:true,version:state.current.version,media:JSON.parse(fs.readFileSync(`.build/vms/${name}/vm-manifest.json`))};
  fs.writeFileSync('.build/evidence/updates-ui.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 } finally {await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1});
