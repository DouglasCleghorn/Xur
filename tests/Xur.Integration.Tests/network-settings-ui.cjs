const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/evidence/network-ui');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--control-panel-render',out],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();let submitted;
  await page.route('http://network.test/**',route=>{
   const url=new URL(route.request().url());
   if(route.request().method()==='POST'){submitted=new URLSearchParams(route.request().postData());return route.fulfill({body:'Applied'});}
   if(url.pathname.startsWith('/api/'))return route.fulfill({status:503,body:'Unavailable'});
   const asset=/\.(css|js|ttf|svg)$/.test(url.pathname),file=asset?'src/Xur.Control/wwwroot'+url.pathname:path.join(out,'network-settings.html');
   return route.fulfill({body:fs.readFileSync(file),contentType:url.pathname.endsWith('.js')?'application/javascript':url.pathname.endsWith('.css')?'text/css':'text/html'});
  });
  for(const width of [1440,390]){
   await page.setViewportSize({width,height:950});await page.goto('http://network.test/settings/network');
   await page.getByRole('heading',{name:'Network settings',exact:true}).waitFor();
   assert(await page.locator('[name="ipv4.addresses"]').isEnabled());
   assert(await page.locator('[name="ipv6.addresses"]').isDisabled());
   await page.locator('[name="ipv4.method"]').selectOption('auto');
   assert(await page.locator('[name="ipv4.addresses"]').isDisabled());
   assert(await page.locator('[name="ipv4.gateway"]').isDisabled());
   assert(await page.locator('[name="ipv4.dns"]').isEnabled());
   await page.locator('[name="ipv6.method"]').selectOption('disabled');assert(await page.locator('[name="ipv6.dns"]').isDisabled());
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Network settings overflow at '+width);
   await page.getByRole('button',{name:'Apply network settings'}).click();
   assert.equal(submitted.get('interface'),'eno1');assert.equal(submitted.get('macAddress'),'02:00:00:00:00:10');
   assert.equal(submitted.get('ipv4.method'),'auto');assert.equal(submitted.has('ipv4.addresses'),false);assert.equal(submitted.has('ipv4.gateway'),false);
  }
  console.log(JSON.stringify({suite:'NetworkSettingsUi',desktopAndMobile:true,modeDependentFields:true,stableAdapterIdentity:true,postPayload:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
