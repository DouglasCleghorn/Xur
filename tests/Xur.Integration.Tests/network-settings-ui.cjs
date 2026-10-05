const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/evidence/network-ui');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--control-panel-render',out],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();let submitted,scanRequest,connectRequest,scanHeaders,connectHeaders,failScan=false,failConnect=false,delayScan=false,releaseScan,delayConnect=false,releaseConnect;
  const ssid='Cafe: <b>&"\\';
  let networks=[{ssid,bssid:'02:00:00:00:00:20',security:'WPA2',signal:80,keyManagement:'wpa-psk',supported:true,needsPassword:true},{ssid:'Guest',bssid:'02:00:00:00:00:21',security:'--',signal:60,keyManagement:'open',supported:true,needsPassword:false},{ssid:'Private',bssid:'02:00:00:00:00:22',security:'WPA3',signal:55,keyManagement:'sae',supported:true,needsPassword:true},{ssid:'Office',bssid:'02:00:00:00:00:23',security:'WPA2 802.1X',signal:40,keyManagement:'unsupported',supported:false,needsPassword:false}];
  const json=(route,body,status=200)=>route.fulfill({status,body:JSON.stringify(body),contentType:'application/json'});
  await page.route('http://network.test/**',route=>{
   const url=new URL(route.request().url());
   if(url.pathname==='/api/network/wifi/scan'){
    scanRequest=route.request().postDataJSON();scanHeaders=route.request().headers();
    const respond=()=>json(route,failScan?{error:'Wi-Fi adapter is unavailable. Check the supplicant.'}:networks,failScan?400:200);
    if(delayScan)return new Promise(resolve=>{releaseScan=()=>resolve(respond());});return respond();
   }
   if(url.pathname==='/api/network/wifi/connect'){
    connectRequest=route.request().postDataJSON();connectHeaders=route.request().headers();
    const respond=()=>json(route,failConnect?{error:'Could not connect to Wi-Fi. Check the password and signal, then retry.'}:{stage:'Kept',addresses:['192.0.2.30/24','2001:db8::30/64']},failConnect?400:200);
    if(delayConnect)return new Promise(resolve=>{releaseConnect=()=>resolve(respond());});return respond();
   }
   if(route.request().method()==='POST'){submitted=new URLSearchParams(route.request().postData());return route.fulfill({body:'Applied'});}
   if(url.pathname.startsWith('/api/'))return route.fulfill({status:503,body:'Unavailable'});
   const asset=/\.(css|js|ttf|svg)$/.test(url.pathname),file=asset?'src/Xur.Control/wwwroot'+url.pathname:path.join(out,url.pathname.endsWith('.html')?path.basename(url.pathname):'network-settings.html');
   return route.fulfill({body:fs.readFileSync(file),contentType:{'.js':'application/javascript','.css':'text/css','.ttf':'font/ttf','.svg':'image/svg+xml'}[path.extname(url.pathname)]||'text/html'});
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
  for(const width of [1440,390,320]){
   await page.setViewportSize({width,height:950});await page.goto('http://network.test/settings/network');
   const card=page.locator('.wifi-adapter').first(),second=page.locator('.wifi-adapter').nth(1),scan=card.locator('.wifi-scan'),select=card.locator('.wifi-network'),password=card.locator('.wifi-password'),submit=card.locator('.wifi-submit'),message=card.locator('.wifi-message');
   assert.equal(await page.locator('.wifi-adapter').count(),2);
   delayScan=true;await scan.click();await message.filter({hasText:'Scanning'}).waitFor();assert(await scan.isDisabled());assert(await card.locator('.wifi-connect').isHidden());
   assert.deepEqual(scanRequest,{interface:'wlan0',macAddress:'02:00:00:00:00:30'});assert.equal(scanHeaders.requestverificationtoken,'fixture-only');
   releaseScan();delayScan=false;await card.locator('.wifi-connect').waitFor();
   assert.equal(await select.locator('option').first().textContent(),ssid+' · 80% · WPA2');assert.equal(await card.locator('b').count(),0,'SSID must remain plain text');
   assert(await select.locator('option').last().isDisabled());assert(await password.isVisible());assert.equal(await password.getAttribute('type'),'password');
   await password.fill('  fixture password ');delayConnect=true;await submit.click();await message.filter({hasText:'Connecting'}).waitFor();
   assert(await scan.isDisabled());assert(await second.locator('.wifi-scan').isDisabled());assert.equal(await password.inputValue(),'');
   assert.deepEqual(connectRequest,{interface:'wlan0',macAddress:'02:00:00:00:00:30',ssid,bssid:'02:00:00:00:00:20',keyManagement:'wpa-psk',password:'  fixture password '});assert.equal(connectHeaders.requestverificationtoken,'fixture-only');
   releaseConnect();delayConnect=false;await message.filter({hasText:'Saved for automatic reconnection'}).waitFor();
   assert(await card.locator('.wifi-connect').isHidden());assert(!((await page.locator('body').textContent()).includes('fixture password')));
   assert.equal(await card.locator('.wifi-addresses a').first().getAttribute('href'),'https://192.0.2.30:8443/settings/network');
   assert.equal(await card.locator('.wifi-addresses a').last().getAttribute('href'),'https://[2001:db8::30]:8443/settings/network');
   await scan.click();await card.locator('.wifi-connect').waitFor();await select.selectOption('1');assert(await password.isHidden());await submit.click();await message.filter({hasText:'Saved for automatic reconnection'}).waitFor();assert.equal(connectRequest.password,'');assert.equal(connectRequest.keyManagement,'open');
   await second.locator('.wifi-scan').click();await second.locator('.wifi-connect').waitFor();assert.equal(scanRequest.interface,'wlan1');assert.equal(scanRequest.macAddress,'02:00:00:00:00:31');
   await second.locator('.wifi-network').selectOption('2');const otherPassword=second.locator('.wifi-password');assert.equal(await otherPassword.getAttribute('minlength'),'1');assert.equal(await otherPassword.getAttribute('maxlength'),'63');
   await otherPassword.fill('x');failConnect=true;await second.locator('.wifi-submit').click();await second.locator('.wifi-message[role=alert]').waitFor();assert((await second.locator('.wifi-message').textContent()).includes('Check the password'));assert.equal(await otherPassword.inputValue(),'');assert(await second.locator('.wifi-submit').isEnabled());failConnect=false;
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Wi-Fi settings overflow at '+width);
   await page.screenshot({path:path.join(out,'wifi-'+width+'.png'),fullPage:true});
  }
  await page.goto('http://network.test/settings/network');const card=page.locator('.wifi-adapter').first();failScan=true;await card.locator('.wifi-scan').click();await card.locator('.wifi-message[role=alert]').waitFor();assert(await card.locator('.wifi-connect').isHidden());assert(!(await card.locator('.wifi-message').textContent()).includes('No visible'));failScan=false;
  networks=[];await card.locator('.wifi-scan').click();await card.locator('.wifi-message').filter({hasText:'No visible networks'}).waitFor();assert(await card.locator('.wifi-connect').isHidden());
  networks=[{ssid:'Office',bssid:'02:00:00:00:00:23',security:'802.1X',signal:40,keyManagement:'unsupported',supported:false,needsPassword:false}];await card.locator('.wifi-scan').click();await card.locator('.wifi-message').filter({hasText:'No supported networks'}).waitFor();assert(await card.locator('.wifi-submit').isDisabled());
  for(const state of ['off','blocked','missing','unavailable','error','pending']){
   await page.goto('http://network.test/wifi-'+state+'.html');
   for(const scan of await page.locator('.wifi-scan').all())assert(await scan.isDisabled(),state+' must not scan');
   if(state==='off'){await page.getByRole('button',{name:'Enable Wi-Fi'}).click();assert.equal(submitted.get('__RequestVerificationToken'),'fixture-only');}
   if(state==='blocked')assert((await page.locator('body').textContent()).includes('hardware switch'));
   if(state==='missing')assert((await page.locator('body').textContent()).includes('No Wi-Fi adapters'));
   if(state==='unavailable')assert((await page.locator('body').textContent()).includes('supplicant'));
   if(state==='error')assert((await page.locator('body').textContent()).includes('Could not read'));
   if(state==='pending')assert((await page.locator('body').textContent()).includes('Keep or revert'));
  }
  console.log(JSON.stringify({suite:'NetworkSettingsUi',desktopAndMobile:true,modeDependentFields:true,stableAdapterIdentity:true,postPayload:true,wifi:{multipleAdapters:true,openWpa2Wpa3:true,csrf:true,passwordPrivacy:true,scanAndConnectErrors:true,emptyAndUnsupportedNetworks:true,radioAndUnavailableStates:true}}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
