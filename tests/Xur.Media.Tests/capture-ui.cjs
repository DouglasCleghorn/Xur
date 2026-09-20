const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
  const browser=await chromium.launch({headless:true});
  try {
  const page=await browser.newPage();
  const base=process.argv[2]||'http://127.0.0.1:18081';
  fs.mkdirSync('.build/evidence/ui',{recursive:true});
  for(const [name,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
    await page.setViewportSize({width,height});
    const response=await page.goto(base+'/',{waitUntil:'networkidle'});
    if(response.status()!==200 || !await page.getByRole('heading',{name:"Enter your access code."}).count())throw Error('Token prompt absent');
    if(await page.locator('nav,aside,header').count())throw Error('Dashboard chrome exposed before login');

    if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Horizontal overflow');
    await page.goto(base+'/login',{waitUntil:'networkidle'});
    await page.screenshot({path:`.build/evidence/ui/login-${name}.png`,fullPage:true});
  }
  const vm=process.argv[3];
  if(vm){
    const raw=fs.readFileSync(`.build/vms/${vm}/session.private.cookies`,'utf8');
    const session=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
    await page.context().addCookies([{name:'xur.session',value:session,url:base,httpOnly:true,sameSite:'Strict'}]);
    for(const [name,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
      await page.setViewportSize({width,height});await page.goto(base+'/',{waitUntil:'networkidle'});
      if(!await page.getByRole('heading',{name:"Install Xur",exact:true}).count())throw Error('Authenticated dashboard absent');
      if(await page.locator('input[type="radio"]').count()===0)throw Error('Disk selection missing from dashboard');
      const text=await page.locator('body').innerText();
      if(/prototype|qualification|Your disks stay protected|YOUR HARDWARE|signed in|Answer-file discovery/i.test(text))throw Error('Removed installer text remains');
      await page.screenshot({path:`.build/evidence/ui/setup-${name}.png`,fullPage:true});
    }
  }
  console.log(JSON.stringify({ui:'Passed',anonymousDashboardHidden:true,viewports:['1440x1000','390x844']}));
  } finally { await browser.close(); }
})().catch(e=>{console.error(e.message);process.exitCode=1});
