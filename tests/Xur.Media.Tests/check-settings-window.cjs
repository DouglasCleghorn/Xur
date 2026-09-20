const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const name=process.argv[2];if(!name)throw Error('Pass the VM name');
 const privateCookies=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8');
 const session=privateCookies.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const context=await browser.newContext();
  await context.addCookies([{name:'xur.session',value:session,url:'http://127.0.0.1:18081',httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage();
  for(const [size,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});
   const r=await page.goto('http://127.0.0.1:18081/settings',{waitUntil:'networkidle'});
   if(r.status()!==200)throw Error('Settings unavailable');
   const text=await page.locator('.ip-addresses').allTextContents();
   if(!text.join(' ').includes('10.71.1.15')||!text.join(' ').includes('10.71.2.15'))throw Error('DHCP adapter addresses missing');
   if(text.some(t=>t.includes('127.0.0.1')||t.trim()==='::1'))throw Error('Loopback address visible');
   if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Horizontal overflow');
   await page.screenshot({path:`.build/evidence/ui/settings-${size}.png`,fullPage:true});
  }
  console.log(JSON.stringify({settings:'Passed',twoDhcpAdapters:true,loopbackExcluded:true,viewports:['1440x1000','390x844']}));
 }finally{await browser.close()}
})().catch(e=>{console.error(e.message);process.exitCode=1});
