const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try {
  const page=await browser.newPage();const errors=[],requests=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.route('https://stations.test/**',r=>{
   const u=new URL(r.request().url());
   if(u.pathname==='/api/station-users') { requests.push(r.request().postDataJSON());return r.fulfill({json:{name:'Alex',username:'xuruser-alex'}}); }
   if(u.pathname.endsWith('/pairings'))return r.fulfill({json:{pairings:[{id:'client1',name:'Laptop',address:'192.0.2.20'}]}});
   if(u.pathname.endsWith('/pair')) { requests.push(r.request().postDataJSON());return r.fulfill({json:{}}); }
   const asset=/\.(css|js|ttf|svg)$/.test(u.pathname);
   let body=fs.readFileSync(asset?'src/Xur.Control/wwwroot'+u.pathname:'.build/fast/control-panel/workstations-populated.html');
   if(!asset)body=body.toString().replace(/(<form\b[^>]*>)/g,'$1<input type="hidden" name="__RequestVerificationToken" value="fixture-only">');
   return r.fulfill({body,contentType:u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.ttf')?'font/ttf':u.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
  });
  const out='.build/evidence/workstation-design';fs.mkdirSync(out,{recursive:true});
  for(const width of [1440,390,320]) {
   await page.setViewportSize({width,height:1000});await page.goto('https://stations.test/workstations');
   const running=page.locator('[data-station=w1].station-card'),stopped=page.locator('[data-station=w2].station-card'),orphan=page.locator('[data-station=w4].station-card');
   assert.equal(await page.locator('.station-card').count(),4);
   assert.equal(await page.getByRole('heading',{name:'Gaming workstation',exact:true}).count(),1,'No duplicate management listing');
   assert.equal(await running.getByRole('button',{name:'Load profile'}).count(),0,'No disabled load action on a running desktop');
   assert.equal(await page.getByText('Game troubleshooting',{exact:true}).count(),1);
   assert(await running.locator('.station-settings-body').isHidden());
   assert.equal(await stopped.locator('.station-connect').count(),0,'Pairing becomes available after loading');
   assert.equal(await stopped.locator('select[name=id] option').count(),2);
   await stopped.locator('select[name=id]').selectOption('6');
   assert.deepEqual(await stopped.locator('.station-load').evaluate(f=>({action:new URL(f.action).pathname,id:new FormData(f).get('id')})),{action:'/profiles/load',id:'6'});
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Overview overflow at '+width);
   await page.screenshot({path:out+'/overview-'+width+'.png',fullPage:true});
   await running.locator('.station-connect>summary').focus();await page.keyboard.press('Enter');
   await running.locator('.refresh-pairings').click();await running.getByText('Select your client and enter its PIN.',{exact:true}).waitFor();
   await running.locator('[name=pairingId]').selectOption('client1');await running.locator('[name=pin]').fill('1234');await running.getByRole('button',{name:'Pair client',exact:true}).click();await running.getByText('Paired. Open Desktop in Moonlight.',{exact:true}).waitFor();
   await running.locator('.station-settings>summary').click();
   assert(await running.getByRole('button',{name:'Delete workstation'}).isDisabled());
   await running.getByText('Streaming resolution',{exact:true}).click();
   await running.getByRole('button',{name:'Apply resolution'}).waitFor();
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Expanded overflow at '+width);
   await page.screenshot({path:out+'/expanded-'+width+'.png',fullPage:true});
   await orphan.locator('.station-settings>summary').click();assert(await orphan.getByRole('button',{name:'Delete workstation'}).isEnabled());
   await page.locator('.station-create>summary').click();
   await page.locator('.station-create-form [name=name]').fill('My new desktop');
   await page.getByText('Add a user',{exact:true}).click();await page.locator('.station-add-user [name=name]').fill('Alex');await page.getByRole('button',{name:'Add user',exact:true}).click();
   await page.getByText('User added and selected for this workstation.',{exact:true}).waitFor();
   assert.equal(await page.locator('.station-create-form [name=name]').inputValue(),'My new desktop','Adding a user preserves the workstation draft');
   assert.equal(await page.locator('.station-create-form [name=user]').inputValue(),'xuruser-alex');
   assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Creation overflow at '+width);
  }
  assert(requests.some(r=>r.pairingId==='client1'&&r.pin==='1234'),'Pairing sends the selected client and PIN');
  assert.deepEqual(errors,[]);
  console.log(JSON.stringify({suite:'WorkstationsUi',result:'Passed',widths:[1440,390,320],pairing:true,profileSelection:true,createUserPreservesDraft:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
