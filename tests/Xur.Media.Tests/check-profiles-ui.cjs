const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';
 const raw=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8');
 const jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try {
  const context=await browser.newContext({viewport:{width:1440,height:1000}});
  await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage();
  async function completed(){
   for(let i=0;i<120;i++){
    const response=await context.request.get(base+'/api/profiles');const state=await response.json();
    if(state.operation?.stage==='Failed')throw Error(state.operation.error);
    if(state.operation?.stage==='Complete'){await page.reload();return state;}
    await new Promise(r=>setTimeout(r,1000));
   }throw Error('Browser profile apply timed out');
  }
  const section=id=>page.locator(`article[data-profile="${id}"]`);
  async function apply(id){
   await section(id).getByRole('button',{name:'Load profile',exact:true}).click();
   await page.waitForURL(base+'/profiles');
   return completed();
  }
  await page.goto(base+'/profiles');
  await page.getByRole('button',{name:'Create profile',exact:true}).click();
  await page.waitForURL(/\/profiles\/edit\?id=[0-9]+$/);
  const first=new URL(page.url()).searchParams.get('id');
  if(await page.locator('h1').innerText()!==`Profile ${first}`)throw Error('Default profile name missing');
  if(await page.locator('#profile-form input:not([type=hidden]):not([role=combobox])').count()!==1)throw Error('Expected editable profile name');
  if(await page.locator('#profile-form input[name=workloadName],#profile-form input[name=route]').count())throw Error('Editor exposes manual names or routes');
  const recipes=await (await context.request.get(base+'/api/recipes')).json();const recipe=recipes.find(r=>r.vendor==='CPU');
  await page.getByRole('combobox',{name:'Workload type',exact:true}).click();await page.getByRole('option',{name:'llama.cpp · Unsloth models',exact:true}).click();
  const picker=page.getByRole('combobox',{name:'Model',exact:true}).first();
  await picker.fill('no such workload');
  if(!await page.getByText('No matches',{exact:true}).isVisible())throw Error('Dropdown search did not filter options');
  await picker.fill(recipe.name);await picker.press('ArrowDown');await picker.press('Enter');
  if(await page.locator('select[name=recipe]').inputValue()!==recipe.id)throw Error('Keyboard search did not select the recipe');
  // A new row starts blank and can be removed without disturbing the first.
  await page.getByRole('button',{name:'Add workload',exact:true}).click();
  if(await page.locator('.workload-editor').count()!==2)throw Error('Add workload failed');
  await page.getByRole('button',{name:'Remove workload',exact:true}).last().click();
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/ui/profile-edit-${label}.png`,fullPage:true});
   if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Editor overflows viewport');
  }
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');
  const saved=await (await context.request.get(base+'/api/profiles')).json();const definition=saved.profiles.find(p=>p.id===first).workloads[0];
  if(!definition.id||definition.name!==recipe.name||!definition.route)throw Error('Workload defaults missing');
  const started=await apply(first);const original=started.runtime.instances.find(i=>i.id===definition.id);
  if(!original||original.pid<=0)throw Error('Browser did not start a real model');
  // Independently choosing the same recipe must also retain the runtime ID.
  await page.getByRole('button',{name:'Create profile',exact:true}).click();await page.waitForURL(/\/profiles\/edit\?id=[0-9]+$/);
  const second=new URL(page.url()).searchParams.get('id');
  await page.getByRole('combobox',{name:'Workload type',exact:true}).click();await page.getByRole('option',{name:'llama.cpp · Unsloth models',exact:true}).click();
  await page.getByRole('combobox',{name:'Model',exact:true}).fill(recipe.name);
  await page.getByRole('option',{name:recipe.name,exact:true}).click();
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');
  const switched=await apply(second);
  if(switched.runtime.instances.find(i=>i.id===definition.id)?.pid!==original.pid)throw Error('Selecting the same workload restarted its model');
  await section(first).locator('summary').click();
  await section(first).getByRole('button',{name:'Duplicate',exact:true}).click();await page.waitForURL(/\/profiles\/edit\?id=[0-9]+$/);
  const third=new URL(page.url()).searchParams.get('id');
  if(await page.locator('input[name=workloadId]').inputValue()!==definition.id)throw Error('Duplicate changed stable workload ID');
  await page.getByRole('button',{name:'Remove workload',exact:true}).click();
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');
  const stopped=await apply(third);if(stopped.runtime.instances.length)throw Error('Empty profile did not stop models');
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/ui/profiles-${label}.png`,fullPage:true});
   if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Profile page overflows viewport');
  }
  const receipt={suite:'ProfilesBrowser',result:'Passed',createSaveImmediateLoad:true,defaultNameAndIntegerProfileId:true,editableProfileName:true,keyboardSearch:true,realModelStartAndStop:true,unchangedPidAcrossBrowserSwitch:true,duplicatePreservesWorkloadId:true,removeWorkload:true,desktopAndMobile:true,media:JSON.parse(fs.readFileSync(`.build/vms/${name}/vm-manifest.json`))};
  fs.writeFileSync('.build/evidence/profile-ui.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 } finally {await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1});
