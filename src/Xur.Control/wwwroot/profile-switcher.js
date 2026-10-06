// Shared by the menu entry and keyboard/controller shortcuts. All profile
// mutations retain the manager's authenticated preview/digest approval flow.
(()=>{
 const dialog=document.querySelector('#profile-switcher');if(!dialog)return;
 const byId=id=>document.getElementById(id),picker=byId('switcher-picker'),review=byId('switcher-review'),list=byId('switcher-profiles'),search=byId('switcher-search'),error=byId('switcher-error'),empty=byId('switcher-empty'),recovery=byId('switcher-recovery'),apply=byId('switcher-apply');
 let state,plan,selected,opener,request,applying=false,revision=0,trigger='web-menu';
 const controllers=new Map();
 function message(text){error.textContent=text;error.hidden=!text;}
 async function api(path,body,signal){
  const response=await window.xurFetch(path,{...(body!==undefined?{method:'POST',headers:{'Content-Type':'application/json',...(path==='/api/profiles/apply'?{'X-Xur-Switch-Trigger':trigger}:{})},body:JSON.stringify(body)}:{}),signal});
  if(!response.ok){let detail;try{detail=(await response.json()).error;}catch{}throw Error(detail??(response.status===401?'Your session expired. Sign in again.':'Could not load profiles. Try again.'));}
  return response.json();
 }
 function current(p){return state.active?.id===p.id&&state.active.revision===p.revision&&state.runtime.instances.length===p.workloads.length&&p.workloads.every(w=>state.runtime.instances.some(i=>i.id===w.id&&i.fingerprint===w.fingerprint&&i.state==='running'));}
 function textNode(tag,text,className){const el=document.createElement(tag);el.textContent=text;if(className)el.className=className;return el;}
 function render(){
  list.replaceChildren();const term=search.value.toLocaleLowerCase(),busy=['Applying','Cancelling','Failed'].includes(state.operation?.stage);
  const profiles=state.profiles.filter(p=>(p.name+' '+p.workloads.map(w=>w.name+' '+w.recipe.name).join(' ')).toLocaleLowerCase().includes(term)).sort((a,b)=>Number(state.active?.id===b.id)-Number(state.active?.id===a.id)||a.name.localeCompare(b.name,undefined,{numeric:true}));
  for(const p of profiles){
   const loaded=state.active?.id===p.id,button=textNode('button','','switcher-choice'+(loaded?' is-loaded':''));button.type='button';button.dataset.profileId=p.id;button.disabled=busy||current(p);
   const identity=document.createElement('span');identity.append(textNode('strong',p.name),textNode('small',p.workloads.length?p.workloads.map(w=>w.name||w.recipe.name).join(', '):'No workloads'));button.append(identity);
   if(loaded)button.append(textNode('span',current(p)?'Loaded':'Saved changes','state-label '+(current(p)?'running':'pending')));
   button.addEventListener('click',()=>choose(p));list.append(button);
  }
  if('unload all stop running workstations ai services'.includes(term)){
   const p={id:'unload-all',name:'Unload all',unload:true},button=textNode('button','','switcher-choice');button.type='button';button.dataset.profileId=p.id;button.disabled=busy||!state.runtime.instances.length;
   const identity=document.createElement('span');identity.append(textNode('strong',p.name),textNode('small','Stop all running workstations and AI services'));button.append(identity);button.addEventListener('click',()=>choose(p));list.append(button);
  }
  empty.hidden=profiles.length>0;empty.textContent=state.profiles.length?'No profiles match your search.':'No profiles yet. Create one in Profiles.';
  recovery.hidden=!busy&&state.profiles.length>0;byId('switcher-retry').hidden=!busy;
  if(busy)message(state.operation.stage==='Failed'?'A profile change needs attention. Open Manage profiles to resume or cancel it.':'A profile change is in progress. Wait for it to finish or open Manage profiles.');
 }
 async function loadDisplays(version,signal){
  const container=byId('switcher-displays');if(!container)return;
  container.replaceChildren(textNode('p','Loading displays…'));
  try{
   const status=await api('/api/displays',undefined,signal);if(version!==revision||!dialog.open)return;
   container.replaceChildren();
   if(!status.displays.length)container.append(textNode('p','No connected displays found.'));
   for(const display of status.displays){
    const row=textNode('div','','display-control'),description=textNode('div','');description.append(textNode('strong',display.name),textNode('small',display.connector+' · '+display.power));
    if(!display.canControl)description.append(textNode('small',display.unavailableReason));row.append(description);
    const actions=textNode('div','','actions');
    for(const action of ['off','on']){const button=textNode('button','CEC screen '+action,'secondary');button.type='button';button.disabled=!display.canControl;button.setAttribute('aria-label','CEC screen '+action+': '+display.name);
     button.addEventListener('click',async()=>{
      const buttons=[...container.querySelectorAll('button')];buttons.forEach(b=>b.disabled=true);
      try{const result=await api('/api/displays/power',{id:display.id,action});if(version===revision&&dialog.open)message(result.message);}
      catch(e){if(version===revision&&dialog.open)message(e.message);}
      finally{buttons.forEach(b=>b.disabled=b.dataset.unavailable==='true');}
     });button.dataset.unavailable=String(!display.canControl);actions.append(button);
    }row.append(actions);container.append(row);
   }
  }catch(e){if(e.name!=='AbortError'&&version===revision&&dialog.open)container.replaceChildren(textNode('p','Display controls unavailable. Open Display settings to retry.'));}
 }
 async function load(){
  request?.abort();request=new AbortController();const version=++revision;
  loadDisplays(version,request.signal);
  state=undefined;plan=undefined;selected=undefined;picker.hidden=false;review.hidden=true;byId('switcher-accept-label').textContent='Review';search.value='';list.replaceChildren();list.setAttribute('aria-busy','true');empty.hidden=true;recovery.hidden=true;message('');byId('switcher-current').textContent='Loading profiles…';
  try{const data=await api('/api/profiles',undefined,request.signal);if(version!==revision||!dialog.open)return;state=data;byId('switcher-current').textContent=state.active?'Loaded: '+state.active.name:state.runtime.instances.length?'Partially loaded workloads':'No profile loaded';render();search.focus();}
  catch(e){if(e.name!=='AbortError'&&version===revision&&dialog.open){message(e.message);byId('switcher-current').textContent='Profiles unavailable';recovery.hidden=false;byId('switcher-retry').hidden=false;byId('switcher-retry').focus();}}
  finally{if(version===revision)list.setAttribute('aria-busy','false');}
 }
 function open(source,origin='web-menu'){if(dialog.open)return;trigger=origin;opener=source??document.activeElement;dialog.showModal();load();}
 function close(){if(applying)return;revision++;request?.abort();dialog.close();opener?.focus();}
 function back(){if(applying)return;if(review.hidden){close();return;}revision++;request?.abort();plan=undefined;picker.hidden=false;review.hidden=true;byId('switcher-accept-label').textContent='Review';message('');list.querySelector('[data-profile-id="'+CSS.escape(selected?.id??'')+'"]')?.focus();}
 async function choose(p){
  if(applying)return;request?.abort();request=new AbortController();const version=++revision;selected=p;plan=undefined;message('');picker.hidden=true;review.hidden=false;byId('switcher-accept-label').textContent='Select';apply.disabled=true;apply.textContent=p.unload?'Unload all':'Load profile';byId('switcher-desktop-warning').hidden=true;byId('switcher-review-title').textContent=p.name;byId('switcher-changes').replaceChildren(textNode('p','Preparing review…'));byId('switcher-back').focus();
  try{
   const preview=await api(p.unload?'/api/profiles/unload/preview':'/api/profiles/'+encodeURIComponent(p.id)+'/preview',{},request.signal);if(version!==revision||!dialog.open)return;plan=preview;
   const changes=byId('switcher-changes');changes.replaceChildren();
   const definitions=new Map([...state.profiles.flatMap(p=>p.workloads),...(state.active?.workloads??[]),...plan.target.workloads].map(w=>[w.id,w]));
   for(const step of plan.steps.filter(s=>['Keep','Stop','Start'].includes(s.kind))){const row=textNode('div','','switcher-change'),w=definitions.get(step.workloadId),description=document.createElement('div');row.append(textNode('span',step.kind==='Keep'?'Keep running':step.kind,'state-label '+(step.kind==='Keep'?'running':'pending')));description.append(textNode('strong',w?.name??'Workload '+step.workloadId),textNode('small',w?.recipe.name??step.description));row.append(description);changes.append(row);}
   if(!changes.children.length)changes.append(textNode('p','No workloads need to start or stop.'));
   byId('switcher-desktop-warning').hidden=!plan.steps.some(s=>s.kind==='Stop'&&definitions.get(s.workloadId)?.recipe.kind==='Workstation');apply.disabled=false;
  }catch(e){if(e.name!=='AbortError'&&version===revision&&dialog.open){message(e.message);byId('switcher-changes').replaceChildren();}}
 }
 async function approve(){
  if(!plan||applying)return;if(Date.parse(plan.expires)<=Date.now()){message('This review expired. Go back and select the profile again.');apply.disabled=true;return;}
  applying=true;apply.disabled=true;apply.textContent=plan.unload?'Unloading…':'Loading…';byId('switcher-back').disabled=true;
  try{await api('/api/profiles/apply',{id:plan.id,digest:plan.digest});location.assign('/profiles');}
  catch(e){message(e.message+' Go back to refresh the review.');plan=undefined;}
  finally{applying=false;apply.textContent=selected?.unload?'Unload all':'Load profile';byId('switcher-back').disabled=false;}
 }
 function move(direction){const choices=[...picker.querySelectorAll('#switcher-profiles button:not(:disabled),#switcher-displays button:not(:disabled)')];if(!choices.length)return;const index=choices.indexOf(document.activeElement);choices[(index<0?(direction>0?0:choices.length-1):(index+direction+choices.length)%choices.length)].focus();}
 function control(action){if(!dialog.open)return;if(action==='back'){back();return;}if(!review.hidden){if(action==='accept'){if(document.activeElement===apply&&!apply.disabled)approve();else if(document.activeElement===byId('switcher-back'))back();}else if(action==='up'||action==='down'){(document.activeElement===apply?byId('switcher-back'):apply.disabled?byId('switcher-back'):apply).focus();}return;}if(action==='up'||action==='down')move(action==='up'?-1:1);else if(action==='accept')document.activeElement?.click();}
 document.querySelectorAll('[data-profile-switcher-open]').forEach(button=>button.addEventListener('click',()=>open(button)));
 document.querySelectorAll('[data-switcher-close]').forEach(button=>button.addEventListener('click',close));byId('switcher-back').addEventListener('click',back);apply.addEventListener('click',approve);byId('switcher-retry').addEventListener('click',load);search.addEventListener('input',()=>state&&render());dialog.addEventListener('cancel',e=>{e.preventDefault();back();});
 document.addEventListener('keydown',e=>{
  if(e.ctrlKey&&e.altKey&&!e.shiftKey&&!e.metaKey&&e.code==='KeyP'){e.preventDefault();if(!e.repeat)open(undefined,'web-keyboard');return;}
  if(!dialog.open||e.altKey||e.ctrlKey||e.metaKey)return;if(e.key==='ArrowDown'||e.key==='ArrowUp'){e.preventDefault();control(e.key==='ArrowDown'?'down':'up');}
 });
 // A queued close event from an earlier opening must not abort a newly
 // reopened picker (for example Close followed immediately by the shortcut).
 dialog.addEventListener('close',()=>{if(!dialog.open){revision++;request?.abort();}});
 // Standard Gamepad API mapping: View=8, Menu=9, A=0, B=1.
 // Track each pad separately so two people cannot accidentally form a chord.
 function poll(now){
  if(document.visibilityState==='visible'&&document.hasFocus()){
   const seen=new Set();for(const pad of navigator.getGamepads?.()??[]){if(!pad||pad.mapping!=='standard')continue;seen.add(pad.index);let s=controllers.get(pad.index);if(!s){s={held:0,latched:false,armed:false,previous:[]};controllers.set(pad.index,s);}
    const pressed=pad.buttons.map(b=>b.pressed),chord=pressed[8]&&pressed[9];
    if(chord){if(!s.held)s.held=now;if(!s.latched&&now-s.held>=1000){s.latched=true;s.armed=false;open(undefined,'web-controller');}}else{s.held=0;if(!pressed[8]&&!pressed[9])s.latched=false;}
    if(!pressed.some(Boolean))s.armed=true;
    if(dialog.open&&s.armed&&!chord){for(const [index,action]of [[12,'up'],[13,'down'],[0,'accept'],[1,'back']])if(pressed[index]&&!s.previous[index])control(action);}
    s.previous=pressed;
   }for(const index of controllers.keys())if(!seen.has(index))controllers.delete(index);
  }else controllers.clear();requestAnimationFrame(poll);
 }
 window.addEventListener('blur',()=>controllers.clear());requestAnimationFrame(poll);
 if(location.pathname==='/profiles/switch')open(undefined,'web-route');
})();
