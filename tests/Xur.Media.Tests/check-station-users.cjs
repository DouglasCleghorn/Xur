const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict'),{execFileSync}=require('node:child_process');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';
 const jwt=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8').match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const c=await browser.newContext({viewport:{width:1440,height:1000}});await c.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);const page=await c.newPage();
  const state=async()=>await(await c.request.get(base+'/api/profiles')).json();
  async function post(path,data={}){const r=await c.request.post(base+path,{headers:{Authorization:'Bearer '+jwt},data});assert(r.ok(),path+': '+await r.text());return r;}
  async function complete(){for(let n=0;n<180;n++){const s=await state();assert.notEqual(s.operation?.stage,'Failed',s.operation?.error);if(s.operation?.stage==='Complete'){await page.goto(base+'/profiles');return s;}await page.waitForTimeout(1000);}throw Error('Station operation timed out');}
  async function load(id){await page.goto(base+'/profiles');await page.locator(`[data-profile="${id}"]`).getByRole('button',{name:'Load profile',exact:true}).click();await page.waitForURL(base+'/profiles');return complete();}
  async function unload(){const p=await(await post('/api/profiles/unload/preview')).json();await post('/api/profiles/apply',{id:p.id,digest:p.digest});return complete();}
  function guest(script,args){return JSON.parse(execFileSync('python3',['-c',`import sys,json\nsys.path.insert(0,'tests/Xur.Media.Tests')\nfrom guest import execute\nr=execute(sys.argv[1],['/usr/bin/python3','-c','ARGS='+sys.argv[3]+'\\n'+sys.argv[2]])\nassert r['code']==0,r['error']\nprint(r['output'])`,name,script,JSON.stringify(args)],{encoding:'utf8'}));}
  function userEvidence(user,mode){return guest(`import pathlib,pwd,subprocess,json\nu,mode=ARGS\np=pwd.getpwnam(u)\nhome=pathlib.Path(p.pw_dir)\nmarker=home/'.xur-profile-user-test'\nif mode=='write':\n subprocess.run(['runuser','-u',u,'--','/bin/sh','-c','printf persistent > "$1"','xur',str(marker)],check=True)\nif mode in ('write','running'):\n assert subprocess.run(['pgrep','-u',u,'-x','kwin_wayland'],stdout=subprocess.DEVNULL).returncode==0\nif mode=='fresh':assert not marker.exists()\nelse:assert marker.read_text()=='persistent'\nprint(json.dumps({'home':str(home),'uid':p.pw_uid,'markerPresent':marker.exists()}))`,[user,mode]);}
  let s=await state();const model=s.active.workloads.find(w=>w.recipe.kind==='Model'),station=s.active.workloads.find(w=>w.recipe.kind==='Workstation');assert(model&&station);const modelPid=s.runtime.instances.find(i=>i.id===model.id).pid;
  const first=s.active.id;
  async function addUser(profileId,label){
   await page.goto(base+'/profiles/edit?id='+profileId);const user=page.getByRole('combobox',{name:'User',exact:true});await user.click();assert(await page.getByRole('option',{name:'Temporary user',exact:true}).isVisible());await page.getByRole('option',{name:'Add user…',exact:true}).click();
   const dialog=page.getByRole('dialog');await dialog.getByLabel('Name',{exact:true}).fill(label);await dialog.getByRole('button',{name:'Add user',exact:true}).click();await dialog.waitFor({state:'hidden'});assert.equal(await user.inputValue(),label);
   await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');const account=(await state()).profiles.find(p=>p.id===profileId).workloads.find(w=>w.recipe.kind==='Workstation').user;assert(account?.uid>=1000&&!account.temporary);return account;
  }
  const accounts=await(await c.request.get(base+'/api/station-users')).json();const uniqueName=base=>{let value=base,n=1;while(accounts.some(a=>a.name===value))value=base+' '+(++n);return value;};const alexName=uniqueName('Alex'),samName=uniqueName('Sam');const alex=await addUser(first,alexName);await load(first);assert.equal((await state()).runtime.instances.find(i=>i.id===model.id).pid,modelPid);const homeA=userEvidence(alex.username,'write').home;
  const copied=await(await post('/api/profiles/create',{copy:first})).json();const sam=await addUser(copied.id,samName);await load(copied.id);const homeB=userEvidence(sam.username,'write').home;assert.notEqual(homeA,homeB);userEvidence(alex.username,'kept');
  await load(first);userEvidence(alex.username,'running');assert.equal((await state()).runtime.instances.find(i=>i.id===model.id).pid,modelPid);
  // A stale/reassigned UID cannot be saved or stop the currently running user.
  s=await state();const invalid={...s.active,workloads:s.active.workloads.map(w=>w.recipe.kind==='Workstation'?{...w,user:{...w.user,uid:w.user.uid+99}}:w)};
  assert.equal((await c.request.post(base+'/api/profiles',{headers:{Authorization:'Bearer '+jwt},data:invalid})).status(),409);
  await page.goto(base+'/profiles/edit?id='+first);const user=page.getByRole('combobox',{name:'User',exact:true});await user.click();await page.getByRole('option',{name:'Temporary user',exact:true}).click();await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');await load(first);
  const temporary='xurtmp'+require('node:crypto').createHash('sha256').update(JSON.stringify(station.id)).digest('hex').slice(0,12);const tempHome=userEvidence(temporary,'write').home;userEvidence(alex.username,'kept');await unload();
  guest(`import pathlib,pwd,json\nu,home=ARGS\ntry:pwd.getpwnam(u);raise AssertionError('Temporary user survived unload')\nexcept KeyError:pass\nassert not pathlib.Path(home).exists()\nprint('{}')`,[temporary,tempHome]);
  await load(first);userEvidence(temporary,'fresh');
  await page.goto(base+'/profiles/edit?id='+first);await user.click();await page.getByRole('option',{name:alexName,exact:true}).click();await page.getByRole('button',{name:'Save profile',exact:true}).click();await page.waitForURL(base+'/profiles');await load(first);userEvidence(alex.username,'running');
  // Deleting a saved profile never deletes its selected persistent user or home.
  const toDelete=(await state()).profiles.find(p=>p.id===copied.id);await post(`/api/profiles/${copied.id}/delete`,{revision:toDelete.revision});userEvidence(sam.username,'kept');
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.goto(base+'/profiles/edit?id='+first);await user.click();assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));await page.screenshot({path:`.build/evidence/updates/workstation-users-${label}.png`,fullPage:true});await page.keyboard.press('Escape');
  }
  const anonymous=await browser.newContext();assert.equal((await anonymous.request.get(base+'/api/station-users')).status(),401);assert.equal((await anonymous.request.post(base+'/api/station-users',{data:{name:'No'}})).status(),401);await anonymous.close();
  assert.equal((await c.request.post(base+'/api/station-users',{data:{name:'No'}})).status(),400);
  const update=await(await c.request.get(base+'/api/application-updates')).json();const receipt={suite:'StationUsers',result:'Passed',bundle:update.current.id,addUserInEditor:true,existingUserSelection:true,namedHomesPersistAcrossSwitch:true,separateAccountsAndHomes:true,modelPidSurvivesUserSwitch:true,temporaryAccountAndHomeRemovedOnUnload:true,temporaryReloadStartsFresh:true,deletingProfileRetainsUserFiles:true,staleUidRejected:true,desktopAndMobile:true,anonymousDenied:true,csrfEnforced:true};fs.writeFileSync('.build/evidence/updates/station-users.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.stack);process.exitCode=1});
