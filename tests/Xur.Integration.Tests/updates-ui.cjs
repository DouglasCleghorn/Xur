const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/fast');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--updates-render',path.join(out,'updates.html')],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();await page.route('http://updates.test/**',route=>{
   const url=new URL(route.request().url());
   const file=url.pathname==='/setup.css'?'src/Xur.Control/wwwroot/setup.css':url.pathname.startsWith('/fonts/')?'src/Xur.Control/wwwroot'+url.pathname:path.join(out,'updates.html');
   return route.fulfill({body:fs.readFileSync(file),contentType:url.pathname.endsWith('.css')?'text/css':url.pathname.endsWith('.ttf')?'font/ttf':'text/html'});
  });
  await page.goto('http://updates.test/');
  assert.equal(await page.locator('.tool-update-row').count(),13);
  assert.equal(await page.locator('.tool-update-row form, .tool-update-row button, .tool-update-row a').count(),0);
  assert.equal(await page.locator('.tool-update-row strong').count(),13);
  assert.equal(await page.getByRole('button',{name:'Update Xur',exact:true}).count(),1);
  assert.equal(await page.locator('#os-update').getByRole('button',{name:'Update',exact:true}).count(),1);
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.locator('.tool-update-row summary').first().click();
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),label+' layout overflows');
   await page.screenshot({path:path.join(out,'updates-'+label+'.png'),fullPage:true});
   await page.locator('.tool-update-row summary').first().click();
  }
  console.log(JSON.stringify({suite:'UpdatesUi',result:'Passed',render:'Real Razor component with test inventory',toolCount:13,versionsOnlyForBundledTools:true,desktopAndMobile:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
