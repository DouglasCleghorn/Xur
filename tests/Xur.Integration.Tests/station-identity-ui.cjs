// XUR_BROWSER_ENGINE=webkit runs the same checks in Playwright WebKit, not Safari.
const playwright=require(process.env.XUR_BROWSER_MODULE||'../../.build/browser/node_modules/playwright');
const engine=process.env.XUR_BROWSER_ENGINE||'chromium';
const touch=process.env.XUR_BROWSER_TOUCH==='1';
if(!['chromium','webkit','firefox'].includes(engine))throw Error('Unsupported XUR_BROWSER_ENGINE: '+engine);
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await playwright[engine].launch({headless:true});
 try {
  const page=await browser.newPage({hasTouch:touch});const errors=[],saved=[],created=[];page.on('pageerror',e=>errors.push(e.message));
  const out='.build/evidence/profile-editor';fs.mkdirSync(out,{recursive:true});
  await page.route('https://stations.test/**',r=>{
   const u=new URL(r.request().url());
   if(u.pathname==='/profiles/save'){saved.push(new URLSearchParams(r.request().postData()));return r.fulfill({contentType:'text/html',body:'Profile saved'});}
   if(u.pathname==='/api/station-users'){created.push(JSON.parse(r.request().postData()));return r.fulfill({json:{name:'Alex',username:'xuruser-alex'}});}
   if(u.pathname.startsWith('/api/'))return r.fulfill({contentType:'application/json',body:'{"topology":{"links":[]}}'});
   const asset=/\.(css|js|ttf|svg)$/.test(u.pathname);
   return r.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+u.pathname:(process.env.XUR_UI_FIXTURES||'.build/fast/control-panel')+'/'+(u.pathname==='/workstations'?'workstations':u.pathname.startsWith('/profile-edit-')?u.pathname.slice(1):'profile-edit')+'.html'),contentType:u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.ttf')?'font/ttf':u.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
  });
  const formValues=()=>page.locator('#profile-form').evaluate(form=>Array.from(new FormData(form).entries()));
  async function assertPickerClosed(select,value){
   const input=select.locator('..').getByRole('combobox');
   assert.equal(await select.inputValue(),value,'Pointer/keyboard selection must update the submitted select');
   const expected=(value||await select.getAttribute('name')==='stationId')?await select.locator('option:checked').textContent():'';
   assert.equal(await input.inputValue(),expected,'Visible and submitted selections must agree');
   assert.equal(await input.getAttribute('aria-expanded'),'false','Selection must close the popup');
   assert.equal(await input.getAttribute('aria-activedescendant'),null);
   assert(await input.evaluate(e=>e===document.activeElement),'Choosing an option must retain combobox focus');
   assert.equal(await page.evaluate(()=>window.getSelection().toString()),'','Choosing must not select option/label text');
   assert.equal(await page.locator('#profile-form').evaluate(e=>e.dataset.submissions||'0'),'0','Picker interaction must not submit the profile');
  }
  const point=locator=>touch?locator.tap():locator.click();
  async function pointerChoose(select,value){
   const input=select.locator('..').getByRole('combobox');
   // Obtain the label from the real select rather than hard-code GPU descriptions.
   const text=await select.locator('option').evaluateAll((options,value)=>options.find(o=>o.value===value).textContent,value);
   await point(input);
   assert(await input.evaluate(e=>e.closest('label').control===e),'The wrapping label must explicitly target the combobox');
   const option=page.getByRole('option',{name:text,exact:true});
   assert.equal(await option.evaluate(e=>e.tagName),'BUTTON','Options must be native controls, not selectable label text');
   assert.equal(await option.getAttribute('type'),'button','Choosing an option must not submit the profile');
   await point(option);await assertPickerClosed(select,value);
  }
  for(const width of [1440,900,390,320]) {
   await page.setViewportSize({width,height:1000});await page.goto('https://stations.test/profile-edit-empty');
   const editors=page.locator('.workload-editor');
   assert(await editors.nth(0).locator('.station-primary').isChecked(),'The first workstation defaults to primary');
   assert(await editors.nth(0).locator('.station-usb').first().isVisible(),'USB devices stay visible for a primary');
   assert(await editors.nth(0).locator('.station-usb').first().isDisabled(),'Primary USB choices are automatic');
   await page.getByRole('button',{name:'Add workload',exact:true}).click();
   assert(!(await editors.nth(1).locator('.station-primary').isChecked()),'Adding a workstation preserves the existing primary');
   assert(await editors.nth(1).locator('.station-usb').first().isEnabled(),'Secondary USB choices remain selectable');
   await pointerChoose(editors.nth(0).locator('.catalog-engine'),'vLLM');
   assert(await editors.nth(0).locator('.station-primary').isDisabled());
   await page.getByRole('button',{name:'Add workload',exact:true}).click();
   assert(await editors.nth(2).locator('.station-primary').isChecked(),'A new workstation defaults to primary after the old primary becomes a model');
   await editors.nth(2).getByRole('button',{name:'Remove workload',exact:true}).click();
   await pointerChoose(editors.nth(0).locator('.catalog-engine'),'Workstation');
   assert(await editors.nth(0).locator('.station-primary').isChecked(),'Changing a model to a workstation defaults to primary when none exists');
   await page.goto('https://stations.test/profile-edit-primary');
   const persisted=page.locator('.station-usb:checked');
   assert.equal(await persisted.count(),2,'Connected and disconnected USB selections survive rendering a saved primary');
   for(const input of await persisted.all()){assert(await input.isVisible());assert(await input.isDisabled());}
   let usb=(await formValues()).filter(([key])=>key==='usb-0').map(([,value])=>value);
   assert.deepEqual(usb,['usb:'+'b'.repeat(64),'usb:'+'d'.repeat(64)],'Disabled primary selections are still submitted');
   await page.locator('.station-primary').uncheck();
   for(const input of await persisted.all())assert(await input.isEnabled());
   usb=(await formValues()).filter(([key])=>key==='usb-0').map(([,value])=>value);
   assert.deepEqual(usb,['usb:'+'b'.repeat(64),'usb:'+'d'.repeat(64)],'Switching to secondary preserves USB settings without duplicate form values');
   await page.goto('https://stations.test/profile-edit');
   assert(!(await page.locator('.station-primary').isChecked()),'Opening an existing profile preserves its saved primary choice');
   assert(await page.locator('.station-usb').first().isVisible(),'USB devices do not require expanding a section');
   await page.locator('#profile-form').evaluate(form=>form.addEventListener('submit',()=>form.dataset.submissions=String(Number(form.dataset.submissions||0)+1)));
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
   const newStation=page.getByRole('option',{name:'New workstation',exact:true});
   assert.equal(await newStation.evaluate(e=>e.tagName),'BUTTON','Options must be native controls, not selectable label text');
   assert.equal(await newStation.getAttribute('type'),'button','Choosing an option must not submit the profile');
   await point(newStation);await assertPickerClosed(page.locator('[name=stationId]'),'');
   assert.equal(await page.locator('[name=stationId]').inputValue(),'');assert(await page.locator('.station-user-choice').isVisible());assert(await page.locator('.station-new-name').isVisible());
   assert.equal(await page.getByRole('combobox',{name:'Workstation',exact:true}).inputValue(),'New workstation');
   const station=page.locator('[name=stationId]'),stationInput=page.getByRole('combobox',{name:'Workstation',exact:true});
   await stationInput.fill('no matching desktop');
   assert.equal(await page.getByRole('option').count(),0);assert(await page.getByText('No matches',{exact:true}).isVisible());
   await stationInput.press('Escape');await assertPickerClosed(station,'');
   await stationInput.click();await stationInput.fill('gAmInG');
   assert.equal(await page.getByRole('option').count(),1,'Search must filter without changing the selected workstation');
   assert.equal(await station.inputValue(),'');await stationInput.press('ArrowDown');
   const activeId=await stationInput.getAttribute('aria-activedescendant');
   assert.equal(await page.locator('[id="'+activeId+'"]').getAttribute('aria-selected'),'true');
   await stationInput.press('Enter');await assertPickerClosed(station,'w1');
   await pointerChoose(station,'');
   const gpuSelect=page.locator('.gpu-choices select').first();
   const gpuValue=await gpuSelect.inputValue();assert(gpuValue,'Fixture must provide a selected GPU');
   const gpuInput=gpuSelect.locator('..').getByRole('combobox');
   await gpuInput.click();await gpuInput.press('ArrowDown');await gpuInput.press('Enter');await assertPickerClosed(gpuSelect,'');
   await pointerChoose(gpuSelect,gpuValue);
   assert(await gpuInput.evaluate(e=>e.selectionStart===e.selectionEnd),'Choosing a different GPU must not merely highlight its input text');
   const userSelect=page.locator('[name=stationUser]'),userInput=page.getByRole('combobox',{name:'User',exact:true});
   await pointerChoose(userSelect,'doug');await pointerChoose(userSelect,'temporary');
   await userInput.click();await userInput.fill('dOuG');assert.equal(await page.getByRole('option').count(),1);
   await userInput.press('Enter');await assertPickerClosed(userSelect,'doug');
   await page.locator('[name=stationName]').fill('Unsaved desktop');
   const unsaved=await formValues(),beforeCreate=created.length;
   // Cancel both by the visible button and Escape, including an already-open user picker.
   for(const cancel of ['button','Escape']){
    await point(userInput);await point(page.locator('.add-profile-user').first());
    const dialog=page.getByRole('dialog'),dialogName=dialog.getByRole('textbox',{name:'Name',exact:true});
    assert(await dialogName.evaluate(e=>e===document.activeElement));await dialogName.fill('Do not create');
    if(cancel==='button')await dialog.getByRole('button',{name:'Cancel',exact:true}).click();else await dialogName.press('Escape');
    await dialog.waitFor({state:'hidden'});assert.deepEqual(await formValues(),unsaved,'Cancel must preserve unsaved profile fields');
    assert.equal(created.length,beforeCreate,'Cancel must not call the user API');
    assert.equal(await userInput.getAttribute('aria-expanded'),'false','Returning focus from dialog must close the user picker');
    assert.equal(await page.locator('#profile-form').evaluate(e=>e.dataset.submissions||'0'),'0');
   }
   const gpu=await page.locator('.gpu-choices').boundingBox(),stationName=await page.locator('.station-new-name').boundingBox(),stationUser=await page.locator('.station-user-choice').boundingBox();
   assert(stationName.y>=gpu.y+gpu.height&&stationUser.y>=gpu.y+gpu.height,'New desktop name and user follow GPU selection');
   await page.locator('.add-profile-user').first().click();
   await page.getByRole('dialog').getByRole('textbox',{name:'Name',exact:true}).fill('Alex');await page.getByRole('dialog').getByRole('button',{name:'Add user',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
   assert.equal(await page.locator('[name=stationUser]').inputValue(),'xuruser-alex');
   assert.deepEqual(created.at(-1),{name:'Alex'});assert.equal(created.length,beforeCreate+1);
   await pointerChoose(userSelect,'temporary');await pointerChoose(userSelect,'xuruser-alex');
   await page.locator('[name=stationName]').fill('Another desktop');
   await page.screenshot({path:out+'/new-'+width+'.png',fullPage:true});
   await pointerChoose(page.locator('.catalog-engine'),'vLLM');
   assert(await page.locator('.station-new-name').isHidden());assert(await page.locator('.station-user-choice').isHidden());assert(await page.getByRole('combobox',{name:'Model',exact:true}).isVisible());
   const engineInput=page.getByRole('combobox',{name:'Workload type',exact:true});
   await engineInput.click();await engineInput.fill('workstation');await engineInput.press('ArrowUp');await engineInput.press('Enter');
   await assertPickerClosed(page.locator('.catalog-engine'),'Workstation');
   await page.getByRole('combobox',{name:'Workstation',exact:true}).click();await page.getByRole('option',{name:'Gaming · w1',exact:true}).click();
   assert.equal(await page.locator('[name=stationName]').inputValue(),'Gaming');assert.equal(await page.locator('[name=stationUser]').inputValue(),'legacy');
   await page.getByRole('button',{name:'Add workload',exact:true}).click();
   assert.equal(await page.locator('[name=stationUser]').nth(1).locator('option[value=xuruser-alex]').count(),1,'New rows must include the newly created user');
   assert.equal(await page.locator('[name=stationId]').nth(1).inputValue(),'');assert.equal(await page.locator('[name=stationName]').nth(1).inputValue(),'');
   const rows=page.locator('.workload-editor');
   assert(await rows.nth(0).locator('.station-usb').first().isVisible());
   await rows.nth(0).getByText('Serial: hub-serial',{exact:false}).first().waitFor();assert.equal(await rows.nth(0).getByText('Duplicate serial number.',{exact:false}).count(),2);
   assert(await rows.nth(0).locator('.station-primary').isChecked());assert(!(await rows.nth(1).locator('.station-primary').isChecked()));
   await rows.nth(0).locator('.station-primary').uncheck();await rows.nth(0).locator('.station-usb').first().check();await rows.nth(0).locator('.station-primary').check();
   assert(await rows.nth(0).locator('.station-usb').first().isChecked());assert(await rows.nth(0).locator('.station-usb').first().isDisabled());
   assert.equal(await rows.nth(1).locator('.station-usb:checked').count(),0,'A new workstation must not inherit USB claims');
   await rows.nth(1).locator('.station-usb').nth(1).check();await rows.nth(1).locator('.station-primary').check();
   assert(!(await rows.nth(0).locator('.station-primary').isChecked()),'Only one primary station');
   assert(await rows.nth(0).locator('.station-usb').first().isEnabled(),'Moving primary restores the old primary USB controls');
   assert(await rows.nth(0).locator('.station-usb').first().isChecked(),'Moving primary retains the old primary USB selections');
   assert(await rows.nth(1).locator('.station-usb').nth(1).isDisabled());
   await rows.nth(0).getByRole('button',{name:'Remove workload',exact:true}).click();
   const values=await page.locator('#profile-form').evaluate(form=>Array.from(new FormData(form).entries()));
   assert(values.some(([k,v])=>k==='primary-0'),'Primary input renumbered after removal');
   assert(values.some(([k,v])=>k==='usb-0'&&v==='usb:'+'b'.repeat(64)),'USB selection survives row removal');
   assert(!values.some(([k])=>k==='usb-1'),'No stale row assignment names');
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Profile editor overflow at '+width);
   await page.screenshot({path:out+'/devices-'+width+'.png',fullPage:true});
   await page.getByRole('button',{name:'Rename profile',exact:true}).click();await name.fill('Saved from editor');
   await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL('**/profiles/save');
   assert.equal(saved.at(-1).get('name'),'Saved from editor','Saving commits an open rename editor');
   assert.equal(saved.at(-1).get('primary-0'),'true');assert.deepEqual(saved.at(-1).getAll('usb-0'),['usb:'+'b'.repeat(64)],'Saving retains a primary workstation USB settings');
   assert.equal(saved.at(-1).get('stationName'),'');assert.equal(saved.at(-1).get('stationUser'),'temporary');
  }
  await page.goto('https://stations.test/workstations');
  await page.getByRole('heading',{name:'Workstations',exact:true}).waitFor();
  await page.locator('.station-settings>summary').click();
  assert(await page.getByRole('button',{name:'Delete workstation',exact:true}).isDisabled(),'Referenced workstation cannot be deleted');
  await page.getByText('New workstation',{exact:true}).click();await page.getByRole('button',{name:'Create workstation',exact:true}).waitFor();
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Workstation management overflow');
  assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'StationIdentityUi',engine,pointer:touch?'touch':'mouse',result:'Passed',pointerPickers:['workload','workstation','user','gpu'],keyboardAndFilter:true,dialogCancelPreservesUnsaved:true,noAccidentalSubmit:true,reuseAndCreate:true,renameAndCancel:true,submittedName:true,newFieldsBelowGpu:true,engineSwitch:true,addUser:true,widths:[1440,900,390,320]}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
