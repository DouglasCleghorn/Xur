const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';
 const raw=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8');
 const jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const context=await browser.newContext();await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage();await page.goto(base+'/updates');const panel=page.locator('#application-update');
  await panel.getByLabel('Update server').fill('pending.example');await page.waitForTimeout(7100);
  if(await panel.getByLabel('Update server').inputValue()!=='pending.example')throw Error('Polling erased the server address while typing');
  await panel.getByLabel('Update server').fill('192.168.0.134:8088');await panel.getByRole('button',{name:'Save server'}).click();
  if(await panel.getByLabel('Update server').inputValue()!=='http://192.168.0.134:8088')throw Error('Server was not saved');
  await panel.getByRole('button',{name:'Check for updates',exact:true}).click();
  await page.waitForFunction(async()=>{const s=await (await fetch('/api/application-updates')).json();return !s.busy && s.operation?.stage==='Complete';},null,{timeout:60000});
  await page.reload();
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.goto(base+'/updates');await page.screenshot({path:`.build/evidence/ui/application-updates-${label}.png`,fullPage:true});
   if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Updates overflow');
  }
  if((await context.request.post(base+'/api/application-updates',{data:{action:'update'}})).status()!==400)throw Error('Cookie mutation bypasses CSRF');
  const receipt={suite:'ApplicationUpdatesBrowser',result:'Passed',serverSetting:true,realRepositoryCheck:true,cookieCsrf:true,desktopAndMobile:true,media:JSON.parse(fs.readFileSync(`.build/vms/${name}/vm-manifest.json`))};
  fs.writeFileSync('.build/evidence/application-updates-ui.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
