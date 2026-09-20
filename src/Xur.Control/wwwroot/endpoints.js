(() => {
 const list=document.getElementById('endpoints-list');if(!list)return;
 const status=document.getElementById('endpoints-status'),refresh=document.getElementById('endpoints-refresh');
 const el=(tag,text,cls)=>{const node=document.createElement(tag);if(text!=null)node.textContent=text;if(cls)node.className=cls;return node;};
 let busy=false,previous=null;
 function address(card,label,value,link=false){
  const row=el('div',null,'endpoint-address'),field=el('label',label),input=el('input');input.readOnly=true;input.value=value;input.spellcheck=false;input.onclick=()=>input.select();field.append(input);
  const copy=el('button','Copy','secondary');copy.type='button';copy.setAttribute('aria-label','Copy '+label);
  copy.onclick=async()=>{try{await navigator.clipboard.writeText(value);status.textContent=label+' copied.';}catch{input.focus();input.select();status.textContent='Select and copy the highlighted URL.';}};
  row.append(field,copy);if(link){const a=el('a','Open','button quiet');a.href=value;a.setAttribute('aria-label','Open '+label);row.append(a);}card.append(row);
 }
 async function load(){
  if(busy)return;busy=true;refresh.disabled=true;
  try{
   const response=await window.xurFetch('/api/model-lab/targets',{signal:AbortSignal.timeout(30000)});
   if(!response.ok)throw Error('Could not refresh endpoints. Try again.');
   const targets=await response.json();if(!Array.isArray(targets))throw Error('Could not read endpoints. Try again.');
   const signature=JSON.stringify(targets.map(t=>[t.workload,t.instance.instanceId]));
   if(signature!==previous){
    list.replaceChildren();
    for(const {workload:w} of targets){
     const card=el('article',null,'panel endpoint-card');card.append(el('h3',w.name),el('p',(w.recipe.hub?.repository||w.recipe.name)+' · '+w.recipe.engine,'secondary-text'),el('p','GPUs: '+w.gpus.join(', '),'secondary-text'));
     const base=location.origin+'/inference/'+encodeURIComponent(w.route)+'/v1';
     address(card,'Base URL',base);address(card,'Chat completions',base+'/chat/completions');address(card,'Model list',base+'/models',true);
     list.append(card);
    }
    previous=signature;
   }
   status.textContent=targets.length?targets.length+' ready '+(targets.length===1?'model':'models')+' · Refreshes every 15 seconds.':'No ready LLM endpoints. Load a llama.cpp or vLLM workload in Profiles; its endpoint appears when it is ready.';
  }catch(e){list.replaceChildren();previous=null;status.textContent=e.name==='TimeoutError'?'Endpoint refresh timed out. Try again.':e.message;}
  finally{busy=false;refresh.disabled=false;}
 }
 refresh.onclick=load;document.addEventListener('visibilitychange',()=>{if(!document.hidden)load();});
 setInterval(()=>{if(!document.hidden)load();},15000);load();
})();
