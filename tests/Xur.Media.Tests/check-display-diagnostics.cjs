const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const name=process.argv[2],api='http://127.0.0.1:18081',base='http://xur-vm.test:18081';
 const raw=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8'),jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true,args:['--host-resolver-rules=MAP xur-vm.test 127.0.0.1','--no-proxy-server']});
 try{
  const context=await browser.newContext();await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  assert.equal((await context.request.get(api+'/api/diagnostics/display')).status(),401);
  const response=await context.request.get(api+'/api/diagnostics/display',{headers:{Authorization:'Bearer '+jwt}});assert.equal(response.status(),200);
  const data=await response.json();assert.equal(data.agentBundle,data.controlBundle);assert(data.kernel);assert(data.modules.some(m=>m.name==='hyperv_fb'));assert(data.commands.hypervDrmModule);for(const d of data.drm){if(d.device)assert(d.device.startsWith('/sys/devices/'));if(d.driver)assert(d.driver.startsWith('/sys/bus/'));}assert(!JSON.stringify(data).includes(jwt));
  const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base+'/diagnostics');await page.waitForFunction(()=>document.querySelector('#diagnostic-copy')?.disabled===false);
  assert.equal(await page.evaluate(()=>window.isSecureContext),false);
  assert.equal(await page.locator('#diagnostic-devices article').count(),data.inventory.length);
  await page.evaluate(()=>document.addEventListener('copy',()=>window.diagnosticCopyEvent=true));
  await page.getByRole('button',{name:'Copy report',exact:true}).click();assert.equal(await page.locator('#diagnostic-copy-status').innerText(),'Report copied.');assert(await page.evaluate(()=>window.diagnosticCopyEvent));
  const copied=JSON.parse(await page.locator('#diagnostic-report').inputValue());assert.equal(copied.controlBundle,data.controlBundle);
  assert(await page.locator('#diagnostic-report').evaluate(e=>e.selectionStart===0&&e.selectionEnd===e.value.length));
  await page.getByRole('button',{name:'Refresh',exact:true}).click();await page.waitForFunction(()=>document.querySelector('#diagnostic-copy')?.disabled===false);
  fs.mkdirSync('.build/evidence/updates',{recursive:true});
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)));
   await page.screenshot({path:`.build/evidence/updates/display-diagnostics-${label}.png`,fullPage:true});
  }
  // Exercise the editor's explanatory link with the actual page script and a
  // test-only DOM removal of candidate GPUs, without altering installed inventory.
  const created=await context.request.post(api+'/api/profiles/create',{data:{},headers:{Authorization:'Bearer '+jwt}});assert.equal(created.status(),200);const profile=await created.json();
  await page.goto(base+'/profiles/edit?id='+profile.id);
  assert.equal(await page.locator('.recipe-choice').isVisible(),false);assert.equal(await page.locator('[name=recipe]').inputValue(),'gaming-workstation');
  await page.evaluate(()=>{
   for(const option of document.querySelectorAll('.gpu-choices option[value]:not([value=""])'))option.remove();
   document.querySelector('[name=recipe]').dispatchEvent(new Event('change',{bubbles:true}));
  });
  assert(await page.getByRole('link',{name:'Open Diagnostics',exact:true}).isVisible());
  await page.getByRole('link',{name:'Open Diagnostics',exact:true}).click();await page.waitForURL('**/diagnostics');
  assert.deepEqual(errors,[]);
  const receipt={suite:'DisplayDiagnostics',result:'Passed',bundle:data.controlBundle,anonymousApiDenied:true,realSysfsAndModuleReport:true,canonicalDeviceAndDriverPaths:true,agentAndControlVersionsMatch:true,insecureLanCopy:true,refresh:true,desktopAndMobile:true,missingGpuDiagnosticLink:true,workstationHasNoSecondDropdown:true};
  fs.writeFileSync('.build/evidence/updates/display-diagnostics.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
