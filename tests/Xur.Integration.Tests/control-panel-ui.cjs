const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/fast/control-panel');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--control-panel-render',out],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
 const page=await browser.newPage();let mutations=0;
 await page.route('http://home.test/**',r=>{
  const u=new URL(r.request().url());if(r.request().method()==='POST'){mutations++;return r.fulfill({body:'accepted'});}
  if(u.pathname.startsWith('/api/'))return r.fulfill({status:503,body:'unavailable'});
  const asset=/\.(css|js|ttf)$/.test(u.pathname),file=asset?'src/Xur.Control/wwwroot'+u.pathname:path.join(out,u.pathname==='/monitoring'?'monitoring.html':'home.html');
  return r.fulfill({body:fs.readFileSync(file),contentType:u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.ttf')?'font/ttf':'text/html'});
 });
 for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
  await page.setViewportSize({width,height});await page.goto('http://home.test/');
  await page.getByRole('heading',{name:'Control panel',exact:true}).waitFor();
  assert.equal(await page.locator('#network-panels').count(),0);
  assert.equal(await page.locator('.control-resources').count(),0);
  await page.getByText('An update is ready.',{exact:false}).waitFor();
  await page.locator('.profile-workload').getByText('Gaming',{exact:true}).waitFor();
  assert.equal(await page.locator('form[action="/updates/all"]').count(),1);
  await page.locator('input[name=id][value="2"]').check();assert.equal(mutations,0,'Selection alone must not apply a profile');
  await page.locator('#home-reboot').click();assert(await page.getByRole('dialog').isVisible());
  await page.locator('#home-reboot-cancel').click();assert.equal(mutations,0,'Cancelling reboot must not send a mutation');
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),label+' home overflow');
  await page.screenshot({path:path.join(out,label+'.png'),fullPage:true});
  if(label==='mobile')await page.locator('.mobile-nav-more>summary').click();
  await page.getByRole('link',{name:'Monitoring',exact:true}).filter({visible:true}).click();await page.getByRole('heading',{name:'Monitoring',exact:true}).waitFor();
  assert.equal(await page.locator('.control-disk').count(),2);
  await page.getByText('Not mounted',{exact:true}).waitFor();
  assert.equal(await page.locator('[data-usage]').count(),5);
  assert.equal(await page.locator('[data-usage="0000:04:00.0"] meter').isVisible(),false);
  assert.equal(await page.locator('#network-panels').count(),1);
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),label+' monitoring overflow');
  assert.equal(await page.locator('.dashboard-metrics').count(),1);
 }
 console.log(JSON.stringify({suite:'ControlPanelUi',result:'Passed',desktopAndMobile:true,monitoringMoved:true,explicitLoad:true,rebootConfirmation:true,unknownVramNotZero:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
