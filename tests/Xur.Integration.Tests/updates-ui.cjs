const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/fast');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--updates-render',path.join(out,'updates.html')],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();await page.route('http://updates.test/**',route=>{
   const url=new URL(route.request().url());
   if(url.pathname==='/api/updates')return route.fulfill({body:JSON.stringify({automatic:true,window:{id:'123',startsAt:Date.now()/1000+900,version:'45',state:'Waiting'}}),contentType:'application/json'});
   if(url.pathname==='/api/application-updates')return route.fulfill({body:JSON.stringify({current:{id:'current'},busy:false}),contentType:'application/json'});
   if(url.pathname.endsWith('.js'))return route.fulfill({body:fs.readFileSync('src/Xur.Control/wwwroot'+url.pathname),contentType:'application/javascript'});
   const file=url.pathname==='/setup.css'?'src/Xur.Control/wwwroot/setup.css':(url.pathname.startsWith('/fonts/')||url.pathname.startsWith('/icons/'))?'src/Xur.Control/wwwroot'+url.pathname:path.join(out,'updates.html');
   return route.fulfill({body:fs.readFileSync(file),contentType:url.pathname.endsWith('.css')?'text/css':url.pathname.endsWith('.ttf')?'font/ttf':url.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
  });
  await page.goto('http://updates.test/');
  assert.equal(await page.locator('.tool-update-row').count(),18);
  assert.equal(await page.locator('.tool-update-row form, .tool-update-row button, .tool-update-row a').count(),0);
  assert.equal(await page.locator('.tool-update-row strong').count(),18);
  for(const name of ['vLLM · AMD','vLLM · AMD gfx1103','vLLM · Intel','vLLM-Omni · AMD','vLLM-Omni · Intel'])
   assert.equal(await page.getByText(name,{exact:true}).count(),1);
  assert.equal(await page.getByRole('button',{name:'Update Xur',exact:true}).count(),1);
  assert.equal(await page.locator('#os-update').getByRole('button',{name:'Update',exact:true}).count(),1);
  assert.equal(await page.locator('input[name="time"]').inputValue(),'03:00');
  assert.equal(await page.locator('input[name="days"]:checked').count(),1);
  assert.equal(await page.locator('input[name="days"]:checked').inputValue(),'6');
  assert.equal(await page.locator('input[name="warningMinutes"]').inputValue(),'15');
  await page.getByRole('button',{name:'Skip this window',exact:true}).waitFor({state:'visible'});
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.locator('.tool-update-row summary').first().click();
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),label+' layout overflows');
   await page.screenshot({path:path.join(out,'updates-'+label+'.png'),fullPage:true});
   await page.locator('.tool-update-row summary').first().click();
  }
  console.log(JSON.stringify({suite:'UpdatesUi',result:'Passed',render:'Real Razor component with test inventory',toolCount:18,versionsOnlyForBundledTools:true,desktopAndMobile:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
