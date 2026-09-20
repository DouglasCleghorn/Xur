const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081',raw=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8'),jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const c=await browser.newContext({viewport:{width:1440,height:1000}});await c.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await c.newPage(),get=async path=>(await c.request.get(base+path)).json();
  async function post(path,body={}){const r=await c.request.post(base+path,{data:body,headers:{Authorization:'Bearer '+jwt},timeout:30000});if(!r.ok())throw Error(path+': '+await r.text());const text=await r.text();return text?JSON.parse(text):null;}
  async function prepared(id){for(let i=0;i<600;i++){const job=(await get('/api/container-jobs')).find(j=>j.id===id);if(job?.stage==='Ready')return (await get('/api/recipes')).find(r=>r.id===job.recipeId);if(['Failed','Interrupted'].includes(job?.stage))throw Error(job.log);await new Promise(r=>setTimeout(r,1000));}throw Error('Preparation timeout');}
  async function complete(id){for(let i=0;i<120;i++){const s=await get('/api/profiles');if(s.operation?.stage==='Failed')throw Error(s.operation.error);if(s.operation?.stage==='Complete'&&s.active?.id===id)return s;await new Promise(r=>setTimeout(r,1000));}throw Error('Load timeout');}
  const original=(await get('/api/profiles')).active;
  await page.goto(base+'/containers');await page.getByLabel('Image',{exact:true}).fill('mirror.gcr.io/library/python:3.12-alpine');await page.locator('[name=name]').fill('Container HTTP test');
  await page.getByRole('button',{name:'Add volume',exact:true}).click();await page.locator('.volume-name').fill('Persistent test data');
  await page.getByText('Advanced',{exact:true}).click();await page.locator('[name=command]').fill(JSON.stringify(['python','-m','http.server','8080','--directory','/data']));
  const previousJobs=new Set((await get('/api/container-jobs')).map(j=>j.id));await page.getByRole('button',{name:'Prepare container',exact:true}).click();
  let job;for(let i=0;i<20;i++){job=(await get('/api/container-jobs')).find(j=>!previousJobs.has(j.id));if(job)break;await page.waitForTimeout(500);}assert(job);
  const first=await prepared(job.id);assert.match(first.image,/^sha256:[0-9a-f]{64}$/);assert.equal(first.container.mounts.length,1);const volume=first.container.mounts[0].volume;
  const preparedJob=await post('/api/container-jobs',{name:'Dockerfile HTTP test',dockerfile:'FROM mirror.gcr.io/library/python:3.12-alpine\nCOPY server.py /server.py\nCMD ["python", "/server.py"]\n',files:[{name:'server.py',content:'from http.server import BaseHTTPRequestHandler,HTTPServer\nfrom pathlib import Path\np=Path("/data/saved.txt")\nif not p.exists(): p.write_text("persistent container data")\nclass H(BaseHTTPRequestHandler):\n def do_GET(self):\n  self.send_response(200); self.end_headers(); self.wfile.write(p.read_bytes())\nHTTPServer(("0.0.0.0",8080),H).serve_forever()\n'}],port:8080,volumes:[{volume,name:'Persistent test data',destination:'/data'}]});
  const second=await prepared(preparedJob.id);assert.notEqual(first.image,second.image);assert.equal(second.container.mounts[0].volume,volume);
  async function create(recipe){const p=await post('/api/profiles/create');const w={id:'container-'+p.id,name:recipe.name,recipe,gpus:[],route:'container-'+p.id};return await post('/api/profiles',{...p,workloads:[...(original?.workloads||[]),w]});}
  const profileA=await create(second),profileB=await create(first);await post(`/api/profiles/${profileA.id}/load`);const running=await complete(profileA.id);const kept=new Map(running.runtime.instances.filter(i=>i.id!=='container-'+profileA.id).map(i=>[i.id,i.pid]));
  assert.equal(await (await c.request.get(base+'/inference/container-'+profileA.id+'/')).text(),'persistent container data');
  await post(`/api/profiles/${profileB.id}/load`);const switched=await complete(profileB.id);for(const [id,pid]of kept)assert.equal(switched.runtime.instances.find(i=>i.id===id).pid,pid);
  assert.equal(await (await c.request.get(base+'/inference/container-'+profileB.id+'/saved.txt')).text(),'persistent container data');
  await post('/api/storage/usage/refresh');let storage;for(let i=0;i<120;i++){storage=await get('/api/storage/usage');if(!storage.scanning&&storage.volumes?.some(v=>v.id===volume))break;await page.waitForTimeout(1000);}assert(storage.volumes.find(v=>v.id===volume).bytes>0);
  await page.goto(base+'/storage');assert((await page.locator('body').innerText()).includes('Persistent test data'));
  for(const [label,width,height]of[['desktop',1440,1000],['mobile',390,844]]){await page.setViewportSize({width,height});for(const path of['containers','storage']){await page.goto(base+'/'+path);await page.screenshot({path:`.build/evidence/updates/${path}-custom-${label}.png`,fullPage:true});assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)));}}
  if(original){await post(`/api/profiles/${original.id}/load`);await complete(original.id);}else{const p=await post('/api/profiles/unload/preview');await post('/api/profiles/apply',{id:p.id,digest:p.digest});for(let i=0;i<120;i++){if((await get('/api/profiles')).runtime.instances.length===0)break;await page.waitForTimeout(1000);}}
  await post(`/api/profiles/${profileA.id}/delete`,{revision:profileA.revision});await post(`/api/profiles/${profileB.id}/delete`,{revision:profileB.revision});assert((await get('/api/container-volumes')).some(v=>v.id===volume));
  const anonymous=await browser.newContext();assert.equal((await anonymous.request.post(base+'/api/container-jobs',{data:{image:'mirror.gcr.io/library/python:3.12-alpine'}})).status(),401);await anonymous.close();
  const update=await get('/api/application-updates');const receipt={suite:'CustomContainers',result:'Passed',bundle:update.current.id,realPodmanPull:true,realDockerfileBuild:true,httpRequests:true,sharedVolumeDataSurvivesSwitch:true,unaffectedWorkloadPidsKept:true,profileDeletionRetainsVolumes:true,storageBreakdown:true,desktopAndPhone:true};fs.writeFileSync('.build/evidence/updates/custom-containers.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
