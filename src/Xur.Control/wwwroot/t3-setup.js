(()=>{
 const page=document.getElementById('t3-code-page');if(!page)return;
 const csrf=page.querySelector('[name=__RequestVerificationToken]').value;
 const error=document.getElementById('t3-setup-error'),modelStatus=document.getElementById('t3-models-status'),modelList=document.getElementById('t3-model-list');
 const origin=new URL(location.href);origin.protocol='http:';origin.port='3773';origin.pathname='/settings/providers';origin.search='';origin.hash='';
 const cards=[...page.querySelectorAll('[data-provider]')],flows=new Map(),busy=new Set();
 let running=false,modelsBusy=false,connecting=false,previous='';
 const active=s=>['Starting','Installing','Waiting','Verifying','Checking'].includes(s);
 const el=(tag,text,cls)=>{const n=document.createElement(tag);if(text!=null)n.textContent=text;if(cls)n.className=cls;return n;};
 function providerUrl(id){const url=new URL(origin);url.searchParams.set('instanceId',id);return url.href;}
 page.querySelectorAll('.t3-provider-link').forEach(a=>a.href=providerUrl(a.dataset.instance));
 async function api(path,body){
  const response=await (window.xurFetch??fetch)('/api/t3-code/'+path,body?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':csrf},body:JSON.stringify(body)}:{});
  const data=await response.json();if(!response.ok)throw Error(data.error||'Setup unavailable. Check the workload and retry.');return data;
 }
 function fail(e){error.textContent=e.message;error.hidden=false;}
 function auth(card,data){
  const area=card.querySelector('.t3-auth'),provider=card.dataset.provider;
  // Preserve a code being typed while polling an unchanged interaction.
  const signature=JSON.stringify([data.flowId,data.state,data.url,data.deviceCode,data.needsCode]);
  if(area.dataset.signature===signature)return;area.dataset.signature=signature;area.replaceChildren();area.hidden=!active(data.state)&&data.state!=='Succeeded';
  if(data.url){
   const url=new URL(data.url),allowed=provider==='codex'?['auth.openai.com']:['claude.ai','platform.claude.com','console.anthropic.com'];
   if(url.protocol!=='https:'||!allowed.includes(url.hostname)||url.username||url.password)throw Error('The provider returned an unexpected sign-in address.');
   const link=el('a','Open '+(provider==='codex'?'ChatGPT':'Claude')+' sign-in','button secondary');link.href=url.href;link.target='_blank';link.rel='noopener noreferrer';area.append(link);
  }
  if(data.deviceCode)area.append(el('p','Enter this code on the provider’s site:'),el('code',data.deviceCode));
  if(data.needsCode){
   const form=el('form'),label=el('label','One-time sign-in code'),input=el('input');input.name='code';input.autocomplete='off';input.spellcheck=false;input.maxLength=2048;input.required=true;label.append(input);
   const submit=el('button','Complete sign-in');submit.type='submit';form.append(label,submit);
   form.onsubmit=async e=>{e.preventDefault();const code=input.value.trim();input.value='';submit.disabled=true;await action(card,'respond',{flowId:data.flowId,code});};area.append(form);
  }
  if(active(data.state)&&data.state!=='Installing'&&data.state!=='Checking'){
   const cancel=el('button','Cancel sign-in','quiet');cancel.type='button';cancel.onclick=()=>action(card,'cancel',{flowId:data.flowId});area.append(cancel);
  }
  if(data.state==='Succeeded'){
   const link=el('a','Continue in T3');link.href=providerUrl(provider==='codex'?'xur_codex_subscription':provider==='claude'?'xur_claude_subscription':'opencode');link.target='_blank';link.rel='noopener noreferrer';area.append(link);
  }
 }
 function render(card,data){
  card.querySelector('.t3-provider-status').textContent=data.state+' · '+data.message;
  if(data.flowId&&active(data.state))flows.set(card.dataset.provider,data.flowId);else flows.delete(card.dataset.provider);
  card.querySelectorAll('[data-action]').forEach(b=>b.disabled=!running||active(data.state)||busy.has(card.dataset.provider)||(b.dataset.action==='login'&&data.state==='Missing'));
  auth(card,data);
 }
 async function action(card,operation,extra={}){
  const provider=card.dataset.provider;if(busy.has(provider)||!running)return;busy.add(provider);error.hidden=true;
  if(operation!=='status')card.querySelectorAll('button').forEach(b=>b.disabled=true);
  try{const data=await api('setup',{action:operation,provider,...extra});busy.delete(provider);render(card,data);}
  catch(e){busy.delete(provider);fail(e);card.querySelectorAll('button').forEach(b=>b.disabled=!running);}
 }
 cards.forEach(card=>card.querySelectorAll('[data-action]').forEach(b=>b.onclick=()=>action(card,b.dataset.action)));
 async function refresh(){
  try{
   const status=await api('status');running=status.state==='Running';document.getElementById('t3-setup-stopped').hidden=running;
   page.querySelectorAll('.t3-provider-link').forEach(a=>{a.setAttribute('aria-disabled',String(!running));a.tabIndex=running?0:-1;if(running)a.href=providerUrl(a.dataset.instance);else a.removeAttribute('href');});
   if(!running){flows.clear();cards.forEach(c=>render(c,{state:'Stopped',message:'Enable T3 Code to continue.'}));return;}
   await Promise.all(cards.map(c=>action(c,'status',flows.has(c.dataset.provider)?{flowId:flows.get(c.dataset.provider)}:{})));
  }catch(e){running=false;cards.forEach(c=>render(c,{state:'Unavailable',message:'Check the T3 workload and retry.'}));fail(e);}
 }
 async function models(){
  if(modelsBusy||connecting)return;modelsBusy=true;
  try{
   const data=await api('models');const signature=JSON.stringify([data,running]);if(signature===previous)return;previous=signature;modelList.replaceChildren();
   modelStatus.textContent=data.models.length?'Choose a model below. Availability refreshes every 15 seconds.':'No saved language models. Add one to a profile first.';
   for(const model of data.models){
    const row=el('article',null,'t3-model'),name=el('h3',model.name),state=el('p',model.state+' · '+model.engine);row.append(name,state);
    if(model.profiles.length)row.append(el('p','Profiles: '+model.profiles.join(', '),'secondary-text'));
    if(model.error)row.append(el('p',model.error));
    if(model.state==='Ready'&&model.modelIds.length){
     const modelLabel=el('label','Served model'),select=el('select');for(const id of model.modelIds){const option=el('option',id);option.value=id;select.append(option);}modelLabel.append(select);
     const harnessLabel=el('label','Harness'),harness=el('select');for(const [id,title] of [['opencode','OpenCode'],['codex','Codex']]){const option=el('option',title);option.value=id;harness.append(option);}harnessLabel.append(harness);
     const contextLabel=el('label','Context limit (tokens)'),context=el('input');context.type='number';context.min='1024';context.max='2097152';context.required=true;context.placeholder='Serving limit';context.value=model.contextLimits?.[select.value]||'';contextLabel.append(context);
     select.onchange=()=>context.value=model.contextLimits?.[select.value]||'';
     const button=el('button','Connect to T3'),result=el('p');result.setAttribute('role','status');button.type='button';button.disabled=!running;
     const fields=el('div',null,'t3-model-fields');fields.append(modelLabel,harnessLabel,contextLabel,button);row.append(fields,result);
     button.onclick=async()=>{
      if(connecting||!context.reportValidity())return;
      connecting=true;button.disabled=true;result.textContent='Checking compatibility from T3. This can take a few minutes…';
      try{const connection=await api('connect',{workloadId:model.workloadId,harness:harness.value,modelId:select.value,origin:location.origin,contextTokens:Number(context.value)});
       result.textContent='Connected. In T3, refresh provider status and select this model. ';const link=el('a','Open model in T3');link.href=providerUrl(connection.id);link.target='_blank';link.rel='noopener noreferrer';result.append(link);
      }catch(e){result.textContent=e.message;}finally{connecting=false;button.disabled=!running;}
     };
    }else{const link=el('a','Review profiles','button secondary');link.href='/profiles';row.append(link);}
    for(const connection of data.connections.filter(c=>c.workloadId===model.workloadId&&c.configured)){
     const link=el('a','Saved: '+(connection.harness==='codex'?'Codex':'OpenCode')+' · '+connection.modelId);link.href=providerUrl(connection.id);link.target='_blank';link.rel='noopener noreferrer';const p=el('p');p.append(link);row.append(p);
    }
    modelList.append(row);
   }
   const focus=new URL(location.href).searchParams.get('workload');if(focus){const index=data.models.findIndex(m=>m.workloadId===focus);if(index>=0)modelList.children[index]?.scrollIntoView({block:'nearest'});}
  }catch(e){previous='';modelList.replaceChildren();modelStatus.textContent=e.message;}finally{modelsBusy=false;}
 }
 document.getElementById('t3-setup-refresh').onclick=refresh;document.getElementById('t3-models-refresh').onclick=()=>{previous='';models();};
 refresh().then(models);setInterval(()=>{if(!document.hidden)refresh();},5000);setInterval(()=>{if(!document.hidden)models();},15000);
 window.addEventListener('pagehide',()=>{page.querySelectorAll('.t3-auth').forEach(a=>{a.replaceChildren();delete a.dataset.signature;});});
 window.addEventListener('pageshow',event=>{if(event.persisted)refresh();});
})();
