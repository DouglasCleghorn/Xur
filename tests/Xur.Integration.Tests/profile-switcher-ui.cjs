const assert=require('node:assert/strict');
const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('node:child_process'),fs=require('node:fs'),path=require('node:path');
(async()=>{
 const out=path.resolve('.build/evidence/profile-switcher-ci');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--artifacts-path','.build/dotnet','--','--control-panel-render',out],{stdio:'pipe'});
 const workload=(id,name,kind)=>({id,name,fingerprint:id+'-v1',recipe:{name:kind==='Model'?'Qwen assistant':'Gaming workstation',kind}});
 const assistant=workload('assistant','Assistant','Model'),gaming=workload('gaming','Gaming workstation','Workstation'),studio=workload('studio','Studio workstation','Workstation');
 const profiles=[{id:'1',name:'Gaming desk',revision:1,workloads:[gaming,assistant]},{id:'2',name:'Studio desk',revision:1,workloads:[studio,assistant]},{id:'3',name:'<img src=x onerror=alert(1)>',revision:1,workloads:[]}];
 const state={profiles,active:profiles[0],runtime:{instances:profiles[0].workloads.map(w=>({id:w.id,fingerprint:w.fingerprint,state:'running'})),gpus:[]},operation:null};
 const browser=await chromium.launch({headless:true});
 try{
  for(const [width,height]of [[1280,800],[390,844]]){
   const page=await browser.newPage({viewport:{width,height}});let mode='normal',requests=[];
   await page.route('http://127.0.0.1:18866/**',route=>{
    const request=route.request(),url=new URL(request.url()),name=url.pathname;
    const json=(data,status=200)=>route.fulfill({json:data,status});
    if(name==='/test/mode'){mode=url.searchParams.get('value');requests=[];return json({mode});}
    if(name==='/test/state')return json({requests,mode});
    if(name==='/api/displays')return json({displays:[
     {id:'display-console',name:'Example TV',connector:'card0-HDMI-A-1',power:'Unknown',canControl:true},
     {id:'display-station',name:'Desk TV',connector:'card1-HDMI-A-1',power:'Unknown',canControl:true},
     {id:'display-monitor',name:'Office monitor',connector:'card2-DP-1',power:'Unknown',canControl:false,unavailableReason:'No CEC adapter assigned.'}
    ],adapters:[]});
    if(name==='/api/profiles')return mode==='offline'?json({error:'Manager unavailable. Try again.'},503):json({...state,...(mode==='empty'?{profiles:[],active:null}:mode==='partial'?{runtime:{...state.runtime,instances:state.runtime.instances.slice(0,1)}}:mode==='changed'?{profiles:[{...profiles[0],revision:2},...profiles.slice(1)]}:mode==='busy'?{operation:{stage:'Applying'}}:{})});
    if(request.method()==='POST'){
     const body=request.postDataJSON(),csrf=request.headers()['requestverificationtoken'],trigger=request.headers()['x-xur-switch-trigger'];requests.push({path:name,body,csrf,trigger});if(csrf!=='fixture-only')return json({error:'CSRF required'},400);
     if(name==='/api/displays/power')return mode==='cec-failure'?json({error:'The TV did not acknowledge the CEC power command.'},409):json({message:'CEC command acknowledged. Workloads keep running.'});
     if(name.endsWith('/preview')){
      if(request.headers().accept!=='text/html')throw Error('Switcher must request the shared review');
      const form=name==='/api/profiles/unload/preview'?'unload':'load';
      const fragment=fs.readFileSync(path.join(out,'review-'+form+'.html'),'utf8');
      return route.fulfill({contentType:'text/html',body:mode==='expired'?fragment.replace(/data-expires="[^"]+"/,'data-expires="2000-01-01T00:00:00Z"'):fragment});
     }
     return json({error:'The running workloads changed. Review again.'},409);
    }
    if(name.startsWith('/api/'))return json({},503);
    const asset=/\.(css|js|ttf|svg)$/.test(name),file=asset?'src/Xur.Control/wwwroot'+name:path.join(out,name==='/profiles'?'profiles.html':name==='/profiles/view'?'profile-view.html':'home.html');
    return route.fulfill({body:fs.readFileSync(file),contentType:name.endsWith('.css')?'text/css':name.endsWith('.js')?'application/javascript':name.endsWith('.ttf')?'font/ttf':name.endsWith('.svg')?'image/svg+xml':'text/html'});
   });
   await page.goto('http://127.0.0.1:18866/');await page.addScriptTag({path:'tests/Xur.Integration.Tests/profile-switcher-checks.js'});
   console.log(JSON.stringify({...await page.evaluate(()=>window.runProfileSwitcherChecks()),width,height}));
   await page.addScriptTag({path:'tests/Xur.Integration.Tests/display-power-checks.js'});console.log(JSON.stringify(await page.evaluate(()=>window.runDisplayPowerChecks())));await page.goto('http://127.0.0.1:18866/profiles');
   await page.locator('[data-profile="1"] .profile-name').click();
   assert.equal(new URL(page.url()).pathname,'/profiles/view');
   assert(await page.getByRole('link',{name:'Edit profile',exact:true}).isVisible());
   assert.equal(await page.locator('main form:has-text("Review changes")').getAttribute('action'),'/profiles/preview');
   assert(await page.locator('main .profile-workload').innerText().then(text=>text.includes('Gaming')&&text.includes('Stopped')));
   assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
   await page.close();
  }
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1});
