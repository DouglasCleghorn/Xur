const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  const page=await browser.newPage();const errors=[],saved=[];page.on('pageerror',e=>errors.push(e.message));
  const out='.build/evidence/profile-editor';fs.mkdirSync(out,{recursive:true});
  await page.route('https://stations.test/**',r=>{
   const u=new URL(r.request().url());
   if(u.pathname==='/profiles/save'){saved.push(new URLSearchParams(r.request().postData()));return r.fulfill({contentType:'text/html',body:'Profile saved'});}
   if(u.pathname==='/api/station-users')return r.fulfill({json:{name:'Alex',username:'xuruser-alex'}});
   if(u.pathname.startsWith('/api/'))return r.fulfill({contentType:'application/json',body:'{"topology":{"links":[]}}'});
   const asset=/\.(css|js|ttf|svg)$/.test(u.pathname);
   return r.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+u.pathname:'.build/fast/control-panel/'+(u.pathname==='/workstations'?'workstations':'profile-edit')+'.html'),contentType:u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.ttf')?'font/ttf':u.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
  });
  for(const width of [1440,900,390,320]) {
   await page.setViewportSize({width,height:1000});await page.goto('https://stations.test/profile-edit');
   assert(await page.getByRole('textbox',{name:'Profile name',exact:true}).isHidden());
   await page.getByRole('button',{name:'Rename profile',exact:true}).click();
   const name=page.getByRole('textbox',{name:'Profile name',exact:true});
   assert(await name.evaluate(e=>e===document.activeElement));assert(await page.locator('#profile-title').isHidden());
   await name.fill('Discard this name');await name.press('Escape');
   assert.equal(await page.locator('#profile-title').textContent(),'AI and gaming');
   await page.getByRole('button',{name:'Rename profile',exact:true}).click();await name.fill('   ');await name.press('Enter');
   assert(await name.isVisible());assert.equal(await page.locator('#profile-form [name=name]').inputValue(),'AI and gaming');
   await name.fill('Gaming and speech');await name.press('Enter');
   assert.equal(await page.locator('#profile-title').textContent(),'Gaming and speech');assert(await name.isHidden());
   assert.equal(await page.locator('#profile-form [name=name]').inputValue(),'Gaming and speech');
   assert(await page.getByRole('button',{name:'Rename profile',exact:true}).evaluate(e=>e===document.activeElement));
   assert.equal(await page.locator('[name=stationId]').inputValue(),'w1');
   assert.equal(await page.locator('[name=stationName]').inputValue(),'Gaming');
   assert(await page.locator('.station-user-choice').isHidden());assert(await page.locator('.station-new-name').isHidden());
   await page.screenshot({path:out+'/existing-'+width+'.png',fullPage:true});
   await page.getByRole('combobox',{name:'Workstation',exact:true}).click();
   await page.getByRole('option',{name:'New workstation',exact:true}).click();
   assert.equal(await page.locator('[name=stationId]').inputValue(),'');assert(await page.locator('.station-user-choice').isVisible());assert(await page.locator('.station-new-name').isVisible());
   assert.equal(await page.getByRole('combobox',{name:'Workstation',exact:true}).inputValue(),'New workstation');
   const gpu=await page.locator('.gpu-choices').boundingBox(),stationName=await page.locator('.station-new-name').boundingBox(),stationUser=await page.locator('.station-user-choice').boundingBox();
   assert(stationName.y>=gpu.y+gpu.height&&stationUser.y>=gpu.y+gpu.height,'New desktop name and user follow GPU selection');
   await page.getByRole('combobox',{name:'User',exact:true}).click();await page.getByRole('option',{name:'Add user…',exact:true}).click();
   await page.getByRole('dialog').getByRole('textbox',{name:'Name',exact:true}).fill('Alex');await page.getByRole('button',{name:'Add user',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
   assert.equal(await page.locator('[name=stationUser]').inputValue(),'xuruser-alex');
   await page.locator('[name=stationName]').fill('Another desktop');
   await page.screenshot({path:out+'/new-'+width+'.png',fullPage:true});
   await page.getByRole('combobox',{name:'Workload type',exact:true}).click();await page.getByRole('option',{name:'vLLM · Hugging Face models',exact:true}).click();
   assert(await page.locator('.station-new-name').isHidden());assert(await page.locator('.station-user-choice').isHidden());assert(await page.getByRole('combobox',{name:'Model',exact:true}).isVisible());
   await page.getByRole('combobox',{name:'Workload type',exact:true}).click();await page.getByRole('option',{name:'Workstation',exact:true}).click();
   await page.getByRole('combobox',{name:'Workstation',exact:true}).click();await page.getByRole('option',{name:'Gaming · w1',exact:true}).click();
   assert.equal(await page.locator('[name=stationName]').inputValue(),'Gaming');assert.equal(await page.locator('[name=stationUser]').inputValue(),'legacy');
   await page.getByRole('button',{name:'Add workload',exact:true}).click();
   assert.equal(await page.locator('[name=stationId]').nth(1).inputValue(),'');assert.equal(await page.locator('[name=stationName]').nth(1).inputValue(),'');
   const rows=page.locator('.workload-editor');
   await rows.nth(0).locator('.station-devices summary').click();
   await rows.nth(0).getByText('Serial: hub-serial',{exact:false}).first().waitFor();assert.equal(await rows.nth(0).getByText('Duplicate serial number.',{exact:false}).count(),2);
   await rows.nth(0).locator('.station-primary').check();await rows.nth(0).locator('.station-usb').first().check();
   await rows.nth(1).locator('.station-devices summary').click();await rows.nth(1).locator('.station-primary').check();
   assert(!(await rows.nth(0).locator('.station-primary').isChecked()),'Only one primary station');
   assert.equal(await rows.nth(1).locator('.station-usb:checked').count(),0,'A new workstation must not inherit USB claims');
   await rows.nth(1).locator('.station-usb').nth(1).check();await rows.nth(0).getByRole('button',{name:'Remove workload',exact:true}).click();
   const values=await page.locator('#profile-form').evaluate(form=>Array.from(new FormData(form).entries()));
   assert(values.some(([k,v])=>k==='primary-0'),'Primary input renumbered after removal');
   assert(values.some(([k,v])=>k==='usb-0'&&v==='usb:'+'b'.repeat(64)),'USB selection survives row removal');
   assert(!values.some(([k])=>k==='usb-1'),'No stale row assignment names');
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Profile editor overflow at '+width);
   await page.screenshot({path:out+'/devices-'+width+'.png',fullPage:true});
   await page.getByRole('button',{name:'Rename profile',exact:true}).click();await name.fill('Saved from editor');
   await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL('**/profiles/save');
   assert.equal(saved.at(-1).get('name'),'Saved from editor','Saving commits an open rename editor');
   assert.equal(saved.at(-1).get('stationName'),'');assert.equal(saved.at(-1).get('stationUser'),'temporary');
  }
  await page.goto('https://stations.test/workstations');
  await page.getByRole('heading',{name:'Workstations',exact:true}).waitFor();
  await page.locator('.station-settings>summary').click();
  assert(await page.getByRole('button',{name:'Delete workstation',exact:true}).isDisabled(),'Referenced workstation cannot be deleted');
  await page.getByText('New workstation',{exact:true}).click();await page.getByRole('button',{name:'Create workstation',exact:true}).waitFor();
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Workstation management overflow');
  assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'StationIdentityUi',result:'Passed',reuseAndCreate:true,renameAndCancel:true,submittedName:true,newFieldsBelowGpu:true,engineSwitch:true,addUser:true,widths:[1440,900,390,320]}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
