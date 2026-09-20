const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const vm=process.argv[2],base='http://127.0.0.1:18081';
 const raw=fs.readFileSync(`.build/vms/${vm}/session.private.cookies`,'utf8');
 const jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 if(jwt.split('.').length!==3)throw Error('Session cookie is not a JWT');
 const browser=await chromium.launch({headless:true});
 try {
  const context=await browser.newContext({viewport:{width:1440,height:1000}});
  await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage();
  await page.goto(base+'/install/progress');
  await page.getByRole('button',{name:'Reboot into Xur',exact:true}).waitFor({timeout:10000});
  if(!await page.locator('.operation').count() || !await page.locator('#install-logs').count())throw Error('Operation box and logs are missing');
  if(await page.locator('pre').count()!==1)throw Error('Progress should have one log area, not a raw status dump');
  if(/prototype|qualification/i.test(await page.locator('body').innerText()))throw Error('Old release labels remain in progress output');
  await page.screenshot({path:'.build/evidence/ui/progress-desktop.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});
  await page.screenshot({path:'.build/evidence/ui/progress-mobile.png',fullPage:true});
  await page.getByRole('button',{name:'Reboot into Xur',exact:true}).click();
  await page.getByRole('heading',{name:'Restarting Xur',exact:true}).waitFor();
  await page.waitForURL(base+'/',{timeout:240000});
  await page.getByRole('heading',{name:'System',exact:true}).waitFor({timeout:30000});
  for(const [name,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});
   if(await page.locator('input[name="code"]').count())throw Error('Reboot lost authentication');
   if(await page.locator('a[href^="/install/"]').count())throw Error('Installed dashboard exposes installer navigation');
   for(const title of ['CPU','Memory','Storage','Uptime','Workloads'])if(!await page.getByRole('heading',{name:title,exact:true}).count())throw Error('Missing system dashboard section');
   if(!await page.getByRole('link',{name:'Tailscale',exact:true}).count())throw Error('Installed VPN navigation missing');
   await page.screenshot({path:`.build/evidence/ui/system-${name}.png`,fullPage:true});
  }
  if((await context.request.get(base+'/api/system')).status()!==200)throw Error('Authenticated system monitoring unavailable');
  console.log(JSON.stringify({suite:'RebootBrowser',waitsForNewBoot:true,jwtCookiePreserved:true,installedDashboardHasNoInstallerControls:true,systemMetrics:true,tailscaleRetained:true}));
 } finally {await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1});
