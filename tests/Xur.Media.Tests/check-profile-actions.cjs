const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081',raw=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8'),jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const c=await browser.newContext();await c.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);const page=await c.newPage();const state=async()=>(await c.request.get(base+'/api/profiles')).json();
  const before=await state();assert(before.active);const original=before.active.id,pids=before.runtime.instances.map(i=>[i.id,i.pid]);
  await page.goto(base+'/profiles');const row=id=>page.locator(`[data-profile="${id}"]`);
  assert(!await row(original).getByRole('link',{name:'Edit',exact:true}).isVisible());await row(original).getByRole('button',{name:'Preview',exact:true}).click();await page.waitForURL('**/profiles/review?*');assert.deepEqual((await state()).runtime.instances.map(i=>[i.id,i.pid]),pids);
  await page.getByRole('link',{name:'Cancel',exact:true}).click();await row(original).locator('summary').click();await row(original).getByRole('button',{name:'Duplicate',exact:true}).click();await page.waitForURL('**/profiles/edit?*');
  const copy=new URL(page.url()).searchParams.get('id');await page.getByLabel('Profile name',{exact:true}).fill('My workstation and AI');await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');assert.equal((await state()).profiles.find(p=>p.id===copy).name,'My workstation and AI');
  await row(copy).getByRole('button',{name:'Load profile',exact:true}).click();await page.waitForURL(base+'/profiles');assert(!page.url().includes('review'));
  for(let i=0;i<120;i++){const s=await state();if(s.operation?.stage==='Failed')throw Error(s.operation.error);if(s.active?.id===copy&&s.operation?.stage==='Complete')break;await page.waitForTimeout(500);}
  assert.equal((await state()).active.id,copy);assert.deepEqual((await state()).runtime.instances.map(i=>[i.id,i.pid]),pids);
  for(const [label,width,height]of[['desktop',1440,1000],['mobile',390,844]]){await page.setViewportSize({width,height});await page.goto(base+'/profiles');assert(!await row(copy).getByRole('link',{name:'Edit',exact:true}).isVisible());await row(copy).locator('summary').click();assert(await row(copy).getByRole('link',{name:'Edit',exact:true}).isVisible());await page.screenshot({path:`.build/evidence/updates/profile-actions-${label}.png`,fullPage:true});assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));}
  await row(copy).getByRole('link',{name:'Edit',exact:true}).click();
  for(const [label,width,height]of[['desktop',1440,1000],['mobile',390,844]]){await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/updates/profile-name-${label}.png`,fullPage:true});assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));}
  const query=encodeURIComponent('https://huggingface.co/lued/Qwen3.8-27B-INT8-W8A16-MTP');const matches=await(await c.request.get(base+'/api/catalog/search?engine=vLLM&q='+query)).json();assert(matches.some(m=>m.id==='lued/Qwen3.8-27B-INT8-W8A16-MTP'));
  // Real upstream lookup through the actual searchable dropdown, without a GPU launch.
  await page.getByRole('button',{name:'Add workload',exact:true}).click();const last=page.locator('.workload-editor').last();await last.getByRole('combobox',{name:'Workload type',exact:true}).click();await page.getByRole('option',{name:'vLLM · Hugging Face models',exact:true}).click();await last.getByRole('combobox',{name:'Model',exact:true}).fill('https://huggingface.co/lued/Qwen3.8-27B-INT8-W8A16-MTP');await page.getByRole('option',{name:'lued/Qwen3.8-27B-INT8-W8A16-MTP',exact:true}).waitFor({timeout:45000});
  const bundle=(await(await c.request.get(base+'/api/application-updates')).json()).current.id;const receipt={suite:'ProfileActions',result:'Passed',bundle,editableName:true,immediateLoad:true,separatePreview:true,editUnderMore:true,unchangedWorkloadPids:true,fullHuggingFaceUrlInVllmPicker:true,desktopAndPhone:true};fs.writeFileSync('.build/evidence/updates/profile-actions.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
