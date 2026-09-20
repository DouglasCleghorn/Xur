const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';
 const jwt=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8').match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const context=await browser.newContext({viewport:{width:1440,height:1000}});await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage();page.setDefaultTimeout(60000);
  const state=async()=>await(await context.request.get(base+'/api/profiles')).json();
  const anonymous=await browser.newContext();
  if((await anonymous.request.get(base+'/api/catalog/search?q=Qwen')).status()!==401)throw Error('Anonymous catalog request was accepted');
  await anonymous.close();
  for(const [engine,query] of [['llama.cpp','Qwen'],['vLLM','Qwen'],['vLLM-Omni','']]){
   const result=await context.request.get(base+'/api/catalog/search?engine='+encodeURIComponent(engine)+'&q='+encodeURIComponent(query));
   if(!result.ok()||(await result.json()).length<2)throw Error('Upstream catalog search failed: '+engine);
  }
  const section=id=>page.locator(`article[data-profile="${id}"]`);
  async function apply(id){
   await page.goto(base+'/profiles');await section(id).getByRole('button',{name:'Load profile',exact:true}).click();
   for(let n=0;n<300;n++){const s=await state();if(s.operation?.stage==='Failed')throw Error(s.operation.error);if(s.operation?.stage==='Complete'){await page.goto(base+'/profiles');return s;}await new Promise(r=>setTimeout(r,1000));}
   throw Error('Profile did not finish');
  }
  await page.goto(base+'/profiles');await page.getByRole('button',{name:'Create profile',exact:true}).click();await page.waitForURL(/\/profiles\/edit\?id=[0-9]+$/);
  const first=new URL(page.url()).searchParams.get('id');
  await page.getByRole('combobox',{name:'Workload type',exact:true}).click();await page.getByRole('option',{name:'llama.cpp · Unsloth models',exact:true}).click();
  const picker=page.getByRole('combobox',{name:'Model',exact:true});await picker.fill('SmolLM2-135M');
  await page.getByRole('option',{name:'unsloth/SmolLM2-135M-Instruct-GGUF',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('[name=recipe]').value.startsWith('model-'));
  const modelRecipe=await page.locator('[name=recipe]').inputValue();
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/ui/model-catalog-${label}.png`,fullPage:true});}
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');
  const original=await apply(first);const model=original.active.workloads[0],running=original.runtime.instances.find(i=>i.id===model.id);
  if(model.recipe.id!==modelRecipe || !model.recipe.files?.length || !/^[a-f0-9]{40}$/.test(model.recipe.files[0].asset.revision))throw Error('Catalog selection was not pinned');
  if(!/unslothai\/unsloth\/tree\/[a-f0-9]{40}\//.test(model.recipe.settingsSource)||!model.recipe.command.includes('--temp'))throw Error('Unsloth settings were not imported');
  const completion=await context.request.post(base+`/inference/${model.route}/v1/chat/completions`,{headers:{Authorization:'Bearer '+jwt},data:{model:'model',messages:[{role:'user',content:'Say hello.'}],max_tokens:12}});
  if(!completion.ok()||!(await completion.json()).choices?.length)throw Error('Real catalog model inference failed');
  await section(first).locator('summary').click();
  await section(first).getByRole('button',{name:'Duplicate',exact:true}).click();await page.waitForURL(/\/profiles\/edit\?id=[0-9]+$/);const combined=new URL(page.url()).searchParams.get('id');
  await page.getByRole('button',{name:'Add workload',exact:true}).click();if(await page.locator('.recipe-choice').last().isVisible())throw Error('Workstation asks for a second dropdown');
  if(!(await page.locator('[name="gpus-1"]').inputValue()))throw Error('Connected display GPU was not selected');
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/ui/gaming-profile-${label}.png`,fullPage:true});}
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');
  const desktop=await apply(combined);if(desktop.runtime.instances.find(i=>i.id===model.id)?.pid!==running.pid)throw Error('Workstation startup restarted the model');
  const workstation=desktop.active.workloads.find(w=>w.recipe.kind==='Workstation'),instance=desktop.runtime.instances.find(i=>i.id===workstation.id);
  if(!instance||instance.pid<=0||instance.endpoint!=='')throw Error('Native workstation did not start');
  fs.writeFileSync(`.build/vms/${name}/workstation-test.json`,JSON.stringify({modelId:model.id,stationId:workstation.id,stationPid:instance.pid,modelPid:running.pid,first,combined}));
  // Read real desktop processes and capture the visible VM separately while it runs.
  const {execFileSync}=require('node:child_process');execFileSync('python3',['tests/Xur.Media.Tests/check-station-processes.py',name,workstation.id]);
  const stopped=await apply(first);if(stopped.runtime.instances.length!==1||stopped.runtime.instances[0].pid!==running.pid)throw Error('Workstation teardown interrupted the model');
  execFileSync('python3',['tests/Xur.Media.Tests/check-station-processes.py',name,workstation.id,'--stopped']);
  await apply(combined);
  execFileSync('python3',['tests/Xur.Media.Tests/check-station-reboot.py',name,workstation.id]);
  const afterReboot=await state(),rebootPid=afterReboot.runtime.instances.find(i=>i.id===model.id).pid;
  const afterStop=await apply(first);if(afterStop.runtime.instances.find(i=>i.id===model.id)?.pid!==rebootPid)throw Error('Post-reboot workstation teardown interrupted the model');
  await section(first).locator('summary').click();await section(first).getByRole('link',{name:'Edit',exact:true}).click();await page.getByRole('button',{name:'Remove workload',exact:true}).click();await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');await apply(first);
  const receipt={suite:'CatalogAndWorkstation',result:'Passed',liveUnslothSearch:true,upstreamCatalogSearches:['llama.cpp','vLLM','vLLM-Omni'],unslothSettingsPinned:true,immutableSelection:true,realGgufInference:true,nativePlasmaStart:true,vulkanExercise:true,stationStop:true,stationReboot:true,modelPidPreserved:true,media:JSON.parse(fs.readFileSync(`.build/vms/${name}/vm-manifest.json`))};
  fs.writeFileSync('.build/evidence/catalog-workstation.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.stack);process.exitCode=1});
