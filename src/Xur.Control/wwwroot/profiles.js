(() => {
 const form=document.querySelector('#profile-form'),rows=document.querySelector('#workload-rows');
 if(!form||!rows)return;
 const template=rows.firstElementChild.cloneNode(true);
 let topology=null;
 (window.xurFetch ?? window.fetch)('/api/gpus').then(r=>r.ok?r.json():null).then(data=>{topology=data?.topology;for(const row of rows.children)hint(row);}).catch(()=>{});
 function hint(row){
  let p=row.querySelector('.nvlink-hint');if(!p){p=document.createElement('p');p.className='nvlink-hint';row.append(p);}
  const selected=[...row.querySelectorAll('.gpu-choices select')].map(s=>s.value).filter(Boolean);
  const pairs=(topology?.links??[]).filter(l=>l.connection==='NVLink');
  const pair=pairs.find(l=>selected.includes(l.from)&&selected.includes(l.to));
  p.textContent=selected.length!==2?'':pair?'NVLink pair · '+pair.state:topology?.error?'NVLink topology could not be fully read. See GPU Diagnostics.':topology?.state==='Observed'?'The driver did not report NVLink between these GPUs.':'';
  p.hidden=!p.textContent;
  for(const select of row.querySelectorAll('.gpu-choices select'))for(const option of select.options){if(!option.dataset.baseLabel)option.dataset.baseLabel=option.textContent;const link=pairs.find(l=>l.from===option.value||l.to===option.value);option.textContent=option.dataset.baseLabel+(link?' · NVLink ↔ '+(link.from===option.value?link.to:link.from)+(link.state==='Active'?'':' · '+link.state):'');}
 }
 rows.addEventListener('change',event=>{if(event.target.closest('.gpu-choices'))hint(event.target.closest('.workload-editor'));});
 let counter=0;
 const csrf=form.querySelector('[name=__RequestVerificationToken]').value;
 async function api(path,body){
  const response=await (window.xurFetch ?? window.fetch)('/api/catalog/'+path,body?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':csrf},body:JSON.stringify(body)}:{});
  const data=await response.json();if(!response.ok)throw Error(data.error||'The catalog is unavailable');return data;
 }
 const error=(row,message)=>{const p=row.querySelector('.catalog-error');p.textContent=message;p.hidden=!message;};
 function enhance(select){
  if(select.dataset.enhanced)return;
  select.dataset.enhanced='true';select.hidden=true;
  const box=document.createElement('div');box.className='search-select';
  const input=document.createElement('input');input.type='search';input.autocomplete='off';
  input.placeholder=select.dataset.placeholder;input.setAttribute('role','combobox');
  input.setAttribute('aria-autocomplete','list');input.setAttribute('aria-expanded','false');
  input.setAttribute('aria-label',select.parentElement.firstChild.textContent.trim());
  const list=document.createElement('div');list.className='search-options';list.id='choices-'+(++counter);
  list.setAttribute('role','listbox');list.hidden=true;input.setAttribute('aria-controls',list.id);
  box.append(input,list);select.after(box);
  let choices=[],active=-1,request=0,timer;
  const restore=()=>{input.value=select.value ? select.selectedOptions[0]?.textContent??'' : '';};
  const close=()=>{++request;clearTimeout(timer);list.hidden=true;input.setAttribute('aria-expanded','false');input.removeAttribute('aria-activedescendant');restore();};
  const choose=option=>{select.value=option.value;close();select.dispatchEvent(new Event('change',{bubbles:true}));};
  function highlight(index){
   active=index;[...list.children].forEach((item,i)=>item.setAttribute('aria-selected',String(i===index)));
   if(index>=0){input.setAttribute('aria-activedescendant',list.children[index].id);list.children[index].scrollIntoView({block:'nearest'});}
   else input.removeAttribute('aria-activedescendant');
  }
  function render(filter=''){
   if(filter.startsWith('https://huggingface.co/'))filter=filter.slice('https://huggingface.co/'.length).split('/').slice(0,2).join('/');
   list.replaceChildren();choices=[...select.options].filter(o=>!o.disabled&&!o.hidden&&o.textContent.toLowerCase().includes(filter.toLowerCase()));
   choices.forEach((option,i)=>{
    const item=document.createElement('div');item.id=list.id+'-'+i;item.setAttribute('role','option');item.textContent=option.textContent;
    item.addEventListener('pointerdown',event=>event.preventDefault());item.addEventListener('click',()=>choose(option));list.append(item);
   });
   if(!choices.length){const empty=document.createElement('p');empty.textContent='No matches';list.append(empty);}
   list.hidden=false;input.setAttribute('aria-expanded','true');highlight(-1);
  }
  function open(filter=''){
   const version=++request;clearTimeout(timer);render(filter);if(select.name!=='recipe')return;
   const row=select.closest('.workload-editor'),engine=row.querySelector('.catalog-engine').value;
   if(engine==='Workstation'||engine==='Podman')return;
   timer=setTimeout(async()=>{
    try{
     const models=await api('search?engine='+encodeURIComponent(engine)+'&q='+encodeURIComponent(filter));
     if(version!==request||document.activeElement!==input||row.querySelector('.catalog-engine').value!==engine)return;
     select.querySelectorAll('[data-remote]').forEach(o=>{if(!o.selected)o.remove();});
     for(const model of models){if([...select.options].some(o=>o.value==='hub:'+model.id))continue;const o=new Option(model.name,'hub:'+model.id);o.dataset.remote='true';o.dataset.engine=engine;select.add(o);}
     render(filter);error(row,'');
    }catch(e){if(version===request)error(row,e.message);}
   },200);
  }
  input.addEventListener('focus',()=>{open();input.select();});
  input.addEventListener('click',()=>{if(list.hidden)open();});
  input.addEventListener('input',()=>open(input.value));
  input.addEventListener('keydown',event=>{
   if(event.key==='Escape'){event.preventDefault();close();}
   if(event.key==='ArrowDown'||event.key==='ArrowUp'){
    event.preventDefault();if(list.hidden)open();if(choices.length)highlight(active<0?(event.key==='ArrowDown'?0:choices.length-1):(active+(event.key==='ArrowDown'?1:-1)+choices.length)%choices.length);
   }
   if(event.key==='Enter'&&!list.hidden){event.preventDefault();if(active>=0)choose(choices[active]);else if(choices.length===1)choose(choices[0]);}
  });
  input.addEventListener('blur',close);
  select.addEventListener('choices-changed',()=>{close();input.placeholder=select.dataset.placeholder;input.setAttribute('aria-label',select.closest('label').querySelector('.recipe-label')?.textContent??input.getAttribute('aria-label'));});
  select.addEventListener('change',restore);select.addEventListener('catalog-resolved',restore);restore();
 }
 const userDialog=document.querySelector('#add-station-user');let userRow=null;
 userDialog.addEventListener('close',()=>{
  // Native dialogs restore focus to the user picker. Keep the committed
  // selection visible without reopening its options over Save profile.
  userRow?.querySelector('.station-user-choice [role=combobox]')?.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
 });
 document.querySelector('#cancel-station-user').addEventListener('click',()=>userDialog.close());
 document.querySelector('#add-station-user-form').addEventListener('submit',async event=>{
  event.preventDefault();const submit=event.target.querySelector('button:not([type=button])'),message=userDialog.querySelector('[role=alert]');submit.disabled=true;message.hidden=true;
  try{
   const response=await (window.xurFetch ?? window.fetch)('/api/station-users',{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':csrf},body:JSON.stringify({name:event.target.elements.name.value})});
   const account=await response.json();if(!response.ok)throw Error(account.error||'Could not add the user.');
   for(const select of [...rows.querySelectorAll('[name=stationUser]'),template.querySelector('[name=stationUser]')])select.add(new Option(account.name,account.username));
   if(userRow?.isConnected){const select=userRow.querySelector('[name=stationUser]');select.value=account.username;select.dataset.previous=account.username;select.dispatchEvent(new CustomEvent('choices-changed'));}userDialog.close();
  }catch(e){message.textContent=e.message;message.hidden=false;}finally{submit.disabled=false;}
 });
 const renumber=()=>[...rows.children].forEach((row,i)=>row.querySelectorAll('.gpu-choices select').forEach(s=>s.name='gpus-'+i));
 function gpus(row){
  const recipe=row.querySelector('[name=recipe]').selectedOptions[0],group=row.querySelector('.gpu-choices');
  const count=Number(recipe?.dataset.gpus??0);
  while(group.children.length>count)group.lastElementChild.remove();
  while(group.children.length<count){
   const label=document.querySelector('#gpu-template').content.firstElementChild.cloneNode(true);
   label.firstChild.textContent='GPU '+(group.children.length+1);group.append(label);
  }
  group.querySelectorAll('select').forEach(select=>{
   for(const option of select.options)option.hidden=!!option.value&&(recipe.dataset.kind==='Workstation'?option.dataset.display!=='true':option.dataset.compute!=='true'||option.dataset.vendor!==recipe.dataset.vendor||Number(option.dataset.memory)<Number(recipe.dataset.memory));
   if(select.selectedOptions[0]?.hidden){select.value='';select.dispatchEvent(new Event('change'));}
   if(!select.value){const assigned=[...rows.querySelectorAll('.gpu-choices select')].filter(s=>s!==select).map(s=>s.value);const available=[...select.options].find(o=>o.value&&!o.hidden&&!assigned.includes(o.value));if(available){select.value=available.value;select.dispatchEvent(new Event('change'));}}
   enhance(select);
  });
  row.querySelector('.gpu-empty').hidden=!(recipe?.dataset.kind==='Workstation' && count>0 && ![...group.querySelectorAll('option')].some(o=>o.value&&!o.hidden&&!o.disabled));
  renumber();hint(row);
 }
 function filterRecipes(row){
  const engine=row.querySelector('.catalog-engine').value,select=row.querySelector('[name=recipe]'),station=engine==='Workstation';
  row.querySelector('.recipe-label').textContent=station?'Workstation':engine==='Podman'?'Container':'Model';
  row.querySelector('.container-library-link').hidden=engine!=='Podman';
  row.querySelector('.recipe-choice').hidden=station;
  row.querySelector('.station-user-choice').hidden=!station||!!row.querySelector('[name=stationId]').value;row.querySelector('.station-identity-choice').hidden=!station;
  select.dataset.placeholder=station?'Search workstations…':engine==='Podman'?'Search containers…':'Search models…';
  for(const option of select.options){
   option.hidden=!!option.value&&(station?option.dataset.kind!=='Workstation':option.dataset.engine!==engine||option.dataset.kind==='Workstation');
   if(!option.value)option.textContent=station?'Select workstation':engine==='Podman'?'Select container':'Select model';
  }
  if(select.selectedOptions[0]?.hidden)select.value='';
  if(station)select.value=[...select.options].find(o=>o.value==='gaming-workstation'&&!o.hidden)?.value??'';
  select.dispatchEvent(new CustomEvent('choices-changed'));
 }
 function initialize(row){row.querySelector('[name=stationUser]').dataset.previous=row.querySelector('[name=stationUser]').value;filterRecipes(row);row.querySelectorAll('select').forEach(enhance);gpus(row);}
 rows.querySelectorAll('.workload-editor').forEach(initialize);
 async function modelOptions(row,model){
  const select=row.querySelector('[name=recipe]'),engine=row.querySelector('.catalog-engine').value,version=String(++counter);row.dataset.request=version;
  const group=row.querySelector('.model-options');group.replaceChildren();row.querySelector('.gpu-choices').replaceChildren();error(row,'Loading model options…');
  try{
   const options=await api('options?model='+encodeURIComponent(model)+'&engine='+encodeURIComponent(engine));if(row.dataset.request!==version)return;
   const variantLabel=document.createElement('label');variantLabel.append('Model format');const variant=document.createElement('select');variant.dataset.placeholder='Search formats…';
   for(const v of options.variants)variant.add(new Option(v.name+' · '+(v.bytes/1024**3).toFixed(1)+' GiB',v.id));variantLabel.append(variant);
   const deviceLabel=document.createElement('label');deviceLabel.append('Run on');const device=document.createElement('select');device.dataset.placeholder='Search devices…';
   for(const [value,label] of [['Auto','Automatic'],['CPU','CPU'],['NVIDIA','NVIDIA GPUs'],['AMD','AMD GPUs'],['Intel','Intel GPUs']])device.add(new Option(label,value));deviceLabel.append(device);group.append(variantLabel,deviceLabel);enhance(variant);enhance(device);
   let resolveVersion=0;
   async function resolve(){
    const revision=++resolveVersion;row.dataset.resolving='true';error(row,'Preparing model…');
    if(![...select.options].some(o=>o.value==='hub:'+model))select.add(new Option(model,'hub:'+model));select.value='hub:'+model;
    try{
     const recipe=await api('resolve',{model,revision:options.revision,variant:variant.value,engine,device:device.value});if(revision!==resolveVersion||row.dataset.request!==version)return;
     let option=[...select.options].find(o=>o.value===recipe.id);if(!option){option=new Option(recipe.name,recipe.id);select.add(option);}
     Object.assign(option.dataset,{engine,kind:recipe.kind,gpus:recipe.gpuCount,vendor:recipe.vendor,memory:recipe.memoryMiB});select.value=recipe.id;
     // Restore the displayed selection without triggering another metadata fetch.
     select.dispatchEvent(new CustomEvent('catalog-resolved'));gpus(row);error(row,'');
    }catch(e){if(revision===resolveVersion&&row.dataset.request===version)error(row,e.message);}
    finally{if(revision===resolveVersion&&row.dataset.request===version)delete row.dataset.resolving;}
   }
   variant.addEventListener('change',resolve);device.addEventListener('change',resolve);await resolve();
  }catch(e){if(row.dataset.request===version)error(row,e.message);}
 }
 form.addEventListener('change',event=>{
  const row=event.target.closest('.workload-editor');if(!row)return;
  if(event.target.name==='stationId'){
   row.querySelector('.station-user-choice').hidden=!!event.target.value;
   const option=event.target.selectedOptions[0];row.querySelector('[name=stationName]').value=option.dataset.name||'';
   const user=row.querySelector('[name=stationUser]');const value=option.dataset.user||'temporary';
   if(value==='legacy'&&![...user.options].some(o=>o.value===value))user.add(new Option('Existing workstation user','legacy'));
   user.value=value;user.dispatchEvent(new CustomEvent('choices-changed'));
  }
  if(event.target.name==='stationUser'){
   if(event.target.value==='add-user'){
    userRow=row;event.target.value=event.target.dataset.previous||'temporary';event.target.dispatchEvent(new CustomEvent('choices-changed'));
    userDialog.querySelector('[name=name]').value='';userDialog.querySelector('[role=alert]').hidden=true;userDialog.showModal();userDialog.querySelector('[name=name]').focus();
   }else event.target.dataset.previous=event.target.value;
  }
  if(event.target.matches('.catalog-engine')){
   row.dataset.request=String(++counter);delete row.dataset.resolving;
   const select=row.querySelector('[name=recipe]');select.value='';
   select.querySelectorAll('[data-remote]').forEach(option=>option.remove());
   row.querySelector('.model-options').replaceChildren();error(row,'');filterRecipes(row);gpus(row);
  }
  if(event.target.name==='recipe'){
   if(event.target.value.startsWith('hub:'))modelOptions(row,event.target.value.slice(4));
   else{row.dataset.request=String(++counter);delete row.dataset.resolving;row.querySelector('.model-options').replaceChildren();error(row,'');gpus(row);}
  }
 });
 form.addEventListener('submit',event=>{
  if([...rows.children].some(row=>row.dataset.resolving||row.querySelector('[name=recipe]').value.startsWith('hub:'))){event.preventDefault();const row=[...rows.children].find(row=>row.dataset.resolving||row.querySelector('[name=recipe]').value.startsWith('hub:'));error(row,'Choose a model format that can run on this machine before saving.');}
 });
 form.addEventListener('click',event=>{if(event.target.matches('.remove-workload')){event.target.closest('.workload-editor').remove();renumber();}});
 document.querySelector('#add-workload').addEventListener('click',()=>{
  const row=template.cloneNode(true);row.querySelector('[name=workloadId]').value='';row.querySelector('[name=stationId]').value='';row.querySelector('[name=stationName]').value='';row.querySelector('[name=recipe]').value='';row.querySelector('.catalog-engine').value='Workstation';row.querySelector('[name=stationUser] option[value=legacy]')?.remove();row.querySelector('[name=stationUser]').value='temporary';
  row.querySelector('.gpu-choices').replaceChildren();row.querySelector('.model-options').replaceChildren();rows.append(row);initialize(row);renumber();
  row.querySelector('.catalog-engine').nextElementSibling.querySelector('[role=combobox]').focus();
 });
})();
