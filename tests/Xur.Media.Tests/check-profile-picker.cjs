// Exercise the actual installed editor. Intercept only catalog searches for a deterministic race test.
const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';
 const raw=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8');
 const jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const context=await browser.newContext({hasTouch:true});await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const created=await context.request.post(base+'/api/profiles/create',{data:{},headers:{Authorization:'Bearer '+jwt}});assert.equal(created.status(),200);
  const profile=await created.json(),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base+'/profiles/edit?id='+profile.id);
  const type=()=>page.getByRole('combobox',{name:'Workload type',exact:true}).first();
  const recipe=()=>page.locator('.recipe-choice [role=combobox]').first();
  const list=()=>page.locator('.search-options:visible');
  async function chooseType(text){await type().click();await list().getByRole('option',{name:text,exact:true}).click();}
  await type().click();
  assert.deepEqual(await list().getByRole('option').allTextContents(),['Workstation','llama.cpp · Unsloth models','vLLM · Hugging Face models','vLLM-Omni · Supported models','Container']);
  await page.keyboard.press('ArrowDown');await page.keyboard.press('ArrowDown');await page.keyboard.press('Enter');assert.equal(await type().inputValue(),'llama.cpp · Unsloth models');
  await type().tap();await list().getByRole('option',{name:'Workstation',exact:true}).tap();assert.equal(await type().inputValue(),'Workstation');assert.equal(await recipe().isVisible(),false);assert.equal(await page.locator('[name=recipe]').inputValue(),'gaming-workstation');assert.equal(await page.locator('.gpu-choices [role=combobox]').count(),1);
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL('**/profiles');
  await page.goto(base+'/profiles/edit?id='+profile.id);assert.equal(await type().inputValue(),'Workstation');assert.equal(await recipe().isVisible(),false);assert.equal(await page.locator('[name=recipe]').inputValue(),'gaming-workstation');
  let searches=[];
  await page.route('**/api/catalog/search?*',async route=>{
   const url=new URL(route.request().url()),engine=url.searchParams.get('engine');searches.push(engine);
   if(engine==='vLLM')await new Promise(r=>setTimeout(r,700));
   await route.fulfill({json:[{id:engine+'/fixture',name:engine+' search result'}]});
  });
  await chooseType('llama.cpp · Unsloth models');assert.equal(await recipe().inputValue(),'');assert.equal(await page.locator('.gpu-choices select').count(),0);
  await recipe().click();assert(!(await list().innerText()).includes('Gaming workstation'));
  const cpu=page.locator('[name=recipe] option[data-engine="llama.cpp"][data-gpus="0"]').first(),cpuName=await cpu.textContent();
  await list().getByRole('option',{name:cpuName,exact:true}).click();
  await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL('**/profiles');
  await page.goto(base+'/profiles/edit?id='+profile.id);assert.equal(await type().inputValue(),'llama.cpp · Unsloth models');assert.equal(await recipe().inputValue(),cpuName);
  await chooseType('vLLM · Hugging Face models');await recipe().click();await page.waitForTimeout(300);
  await chooseType('vLLM-Omni · Supported models');await recipe().click();await page.waitForTimeout(1100);
  assert(!(await list().innerText()).includes('vLLM search result'));assert((await list().innerText()).includes('vLLM-Omni search result'));assert(!(await list().innerText()).includes(cpuName));
  await chooseType('Workstation');const before=searches.length;await page.waitForTimeout(500);assert.equal(searches.length,before);assert.equal(await recipe().isVisible(),false);assert.equal(await page.locator('[name=recipe]').inputValue(),'gaming-workstation');
  await page.getByRole('button',{name:'Add workload',exact:true}).click();assert.equal(await page.getByRole('combobox',{name:'Workload type',exact:true}).count(),2);
  assert.equal(await page.getByRole('combobox',{name:'Workload type',exact:true}).nth(1).inputValue(),'Workstation');
  await page.locator('.remove-workload').last().click();
  fs.mkdirSync('.build/evidence/updates',{recursive:true});
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await type().click();
   const box=await list().boundingBox();assert(box.x>=0&&box.x+box.width<=width+1);assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)));
   await page.screenshot({path:`.build/evidence/updates/profile-picker-${label}.png`,fullPage:true});await page.keyboard.press('Escape');
  }
  assert.deepEqual(errors,[]);
  const status=await (await context.request.get(base+'/api/application-updates')).json();
  const result={suite:'ProfilePicker',result:'Passed',bundle:status.current.id,distinctTypes:true,engineFilteredRecipes:true,workstationHasNoModelSearch:true,workstationSelectedWithoutSecondDropdown:true,staleSearchRejected:true,savedTypesPreserved:true,addedRowDefaults:true,keyboardAndTouch:true,desktopAndMobile:true};
  fs.writeFileSync('.build/evidence/updates/profile-picker.json',JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify(result));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
