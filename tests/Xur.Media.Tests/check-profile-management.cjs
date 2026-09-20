// Installed application: real saved profiles, native desktop, llama.cpp, browser actions.
const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict'),{execFileSync}=require('node:child_process');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';
 const jwt=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8').match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try {
  const context=await browser.newContext({viewport:{width:1440,height:1000}});await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  const state=async()=>await(await context.request.get(base+'/api/profiles')).json();
  async function post(path,data={}){const r=await context.request.post(base+path,{headers:{Authorization:'Bearer '+jwt},data});assert(r.ok(),path+': '+await r.text());return r;}
  async function complete(){for(let i=0;i<180;i++){const s=await state();assert.notEqual(s.operation?.stage,'Failed',s.operation?.error);if(s.operation?.stage==='Complete'){await page.goto(base+'/profiles');return s;}await page.waitForTimeout(1000);}throw Error('Profile operation timed out');}
  async function load(id){await page.goto(base+'/profiles');await row(id).getByRole('button',{name:'Load profile',exact:true}).click();await page.waitForURL(base+'/profiles');return complete();}
  async function unload(){await page.getByRole('button',{name:'Unload profile',exact:true}).click();await page.waitForURL('**/profiles/review?*');assert.equal(await page.locator('h1').innerText(),'Unload profile');await page.getByRole('button',{name:'Unload profile',exact:true}).click();await page.waitForURL(base+'/profiles');return complete();}
  const row=id=>page.locator(`[data-profile="${id}"]`);
  const initial=await state();if(initial.active||initial.runtime.instances.length){await page.goto(base+'/profiles');await unload();}
  // Remove saved fixtures only in this disposable VM so the screenshots remain readable.
  for(const p of (await state()).profiles)await post(`/api/profiles/${p.id}/delete`,{revision:p.revision});
  const recipes=await(await context.request.get(base+'/api/recipes')).json();
  const model=recipes.find(r=>r.id==='smollm2-135m-cpu'),station=recipes.find(r=>r.id==='gaming-workstation');assert(model&&station);
  const gpu=(await state()).runtime.gpus.find(g=>g.cards?.length&&g.displays?.length);assert(gpu,'VM display adapter missing');
  const modelWork={id:'management-model',name:model.name,recipe:model,gpus:[],route:'management-model'};
  const stationWork={id:'management-desktop',name:station.name,recipe:station,gpus:[gpu.pci],route:'management-desktop'};
  async function create(workloads){const p=await(await post('/api/profiles/create')).json();return await(await post('/api/profiles',{...p,workloads})).json();}
  const ai=await create([modelWork]),desktop=await create([stationWork]),combined=await create([modelWork,stationWork]);
  const active=await load(combined.id);assert.equal(active.runtime.instances.length,2);const pids=active.runtime.instances.map(i=>[i.id,i.pid]);assert(pids.every(i=>i[1]>0));
  const chat=await post('/inference/management-model/v1/chat/completions',{model:'smollm2',messages:[{role:'user',content:'Say hello.'}],max_tokens:10});assert((await chat.json()).choices.length);
  // Delete is disabled for the loaded profile in the menu, and independently rejected by the API.
  await row(combined.id).locator('summary').click();assert(await row(combined.id).getByRole('button',{name:'Delete profile',exact:true}).isDisabled());await row(combined.id).locator('summary').click();
  assert.equal((await context.request.post(base+`/api/profiles/${combined.id}/delete`,{headers:{Authorization:'Bearer '+jwt},data:{revision:combined.revision}})).status(),409);
  // Duplicate through More retains exact workload IDs; it remains a normal editable profile.
  await row(combined.id).locator('summary').click();await row(combined.id).getByRole('button',{name:'Duplicate',exact:true}).click();await page.waitForURL('**/profiles/edit?*');const copyId=new URL(page.url()).searchParams.get('id');
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');await load(copyId);assert.deepEqual((await state()).runtime.instances.map(i=>[i.id,i.pid]),pids);
  await row(combined.id).locator('summary').click();await row(combined.id).getByRole('button',{name:'Delete profile',exact:true}).click();
  const dialog=page.getByRole('dialog');assert(await dialog.isVisible());await dialog.getByRole('button',{name:'Cancel',exact:true}).click();assert.equal((await state()).profiles.length,4);
  await row(combined.id).getByRole('button',{name:'Delete profile',exact:true}).click();await page.keyboard.press('Escape');assert(!await dialog.isVisible());
  await row(combined.id).getByRole('button',{name:'Delete profile',exact:true}).click();await dialog.getByRole('button',{name:'Delete profile',exact:true}).click();await page.waitForURL(base+'/profiles');assert.equal((await state()).profiles.length,3);assert.deepEqual((await state()).runtime.instances.map(i=>[i.id,i.pid]),pids);
  await page.getByRole('searchbox',{name:'Search profiles'}).fill(ai.name);assert.equal(await page.locator('[data-profile]:visible').count(),1);await page.getByRole('searchbox',{name:'Search profiles'}).fill('missing profile');assert(await page.getByText('No profiles match your search.',{exact:true}).isVisible());await page.getByRole('searchbox',{name:'Search profiles'}).fill('');
  for(const [label,width,height] of [['desktop',1440,1000],['tablet',900,1000],['mobile',390,844],['small-mobile',320,740]]){
   await page.setViewportSize({width,height});assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1),label+' overflow');
   const button=await page.getByRole('button',{name:'Create profile',exact:true}).boundingBox();assert(button.height>=(width<=700?44:36)&&button.height<50);
   await row(copyId).locator('summary').click();assert(await row(copyId).getByRole('link',{name:'Edit',exact:true}).isVisible());await row(copyId).locator('summary').click();
   if(width<=700){await page.locator('.mobile-nav-more summary').click();for(const title of ['Settings','Tailscale','Diagnostics'])assert(await page.getByRole('link',{name:title,exact:true}).isVisible());await page.locator('.mobile-nav-more summary').click();}
   await page.screenshot({path:`.build/evidence/updates/profiles-redesign-${label}.png`,fullPage:true});
  }
  // All secondary profile actions also remain reachable at phone width.
  await row(ai.id).locator('summary').click();assert(await row(ai.id).getByRole('button',{name:'Duplicate',exact:true}).isVisible());assert(await row(ai.id).getByRole('button',{name:'Delete profile',exact:true}).isVisible());await row(ai.id).locator('summary').click();
  const count=(await state()).profiles.length;await unload();const stopped=await state();assert.equal(stopped.active,null);assert.equal(stopped.runtime.instances.length,0);assert.equal(stopped.profiles.length,count);assert(stopped.operation.unload);
  assert.equal((await context.request.post(base+'/inference/management-model/v1/chat/completions',{headers:{Authorization:'Bearer '+jwt},data:{}})).status(),503);
  execFileSync('python3',['tests/Xur.Media.Tests/check-station-processes.py',name,stationWork.id,'--stopped']);
  await row(copyId).locator('summary').click();await row(copyId).getByRole('button',{name:'Delete profile',exact:true}).click();await page.getByRole('dialog').getByRole('button',{name:'Delete profile',exact:true}).click();await page.waitForURL(base+'/profiles');assert.equal((await state()).profiles.length,2);
  // Reload saved AI+desktop via a fresh copy for the next update's continuity checks.
  const restored=await create([modelWork,stationWork]);await load(restored.id);
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.goto(base+'/profiles/edit?id='+restored.id);await page.screenshot({path:`.build/evidence/updates/profile-editor-redesign-${label}.png`,fullPage:true});assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));
   await page.goto(base+'/');await page.screenshot({path:`.build/evidence/updates/dashboard-redesign-${label}.png`,fullPage:true});assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));
  }
  const anonymous=await browser.newContext();assert.equal((await anonymous.request.post(base+'/api/profiles/unload/preview',{data:{}})).status(),401);assert.equal((await anonymous.request.post(base+`/api/profiles/${ai.id}/delete`,{data:{revision:ai.revision}})).status(),401);await anonymous.close();
  assert.deepEqual(errors,[]);const update=await(await context.request.get(base+'/api/application-updates')).json();
  const receipt={suite:'ProfileManagement',result:'Passed',bundle:update.current.id,realLlamaCppInference:true,realNativeWorkstation:true,duplicatePreservesPids:true,deleteLeavesSharedWorkloadsRunning:true,deleteCancelAndEscape:true,loadedDeleteBlocked:true,unloadStopsModelAndDesktop:true,savedProfilesRetained:true,unloadRoutesRemoved:true,desktopTabletAndPhone:true,mobileMoreKeepsAllActions:true,anonymousMutationsDenied:true};
  fs.writeFileSync('.build/evidence/updates/profiles-management.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.stack);process.exitCode=1});
