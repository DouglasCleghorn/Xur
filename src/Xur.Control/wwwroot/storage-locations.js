(()=>{
 const panel=document.querySelector('#data-storage');if(!panel)return;
 const forms=[...panel.querySelectorAll('form')],status=panel.querySelector('#storage-move-status');
 const token=panel.querySelector('[name=__RequestVerificationToken]')?.value;
 const sleep=()=>new Promise(resolve=>setTimeout(resolve,2000));
 const disable=value=>{panel.dataset.moving=String(value);forms.forEach(form=>form.querySelectorAll('select,button').forEach(input=>input.disabled=value));};
 async function api(path,body){
  const response=await (window.xurFetch??window.fetch)('/api/'+path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json',RequestVerificationToken:token},body:JSON.stringify(body)});
  const value=await response.json().catch(()=>({}));if(!response.ok)throw Error(value.error||'Storage request failed. Refresh to check its status.');return value;
 }
 async function profileFinished(){
  for(;;){const op=await api('profiles/progress');if(!op||op.stage==='Complete')return;if(['Failed','Cancelled'].includes(op.stage))throw Error(op.error||'Profile change did not complete. Open Profiles to inspect it.');await sleep();}
 }
 async function moved(previousId){
  for(;;){const value=await api('storage/locations');status.textContent=value.job?.message||'Preparing storage move…';
   if(!value.busy){if(!value.job||value.job.id===previousId)throw Error('The move did not record a result. Check Diagnostics before retrying.');if(value.job.stage!=='Complete')throw Error(value.job.message);return;}
   await sleep();
  }
 }
 forms.forEach(form=>form.addEventListener('submit',async event=>{
  event.preventDefault();disable(true);
  try{
   const before=await api('storage/locations');if(before.busy)throw Error('A storage move is already running. Refresh to check its status.');
   const current=await api('profiles'),reload=current.active?.id;
   if(current.active||current.runtime?.instances?.length){
    status.textContent='Unloading the profile before moving storage…';
    const plan=await api('profiles/unload/preview',{});await api('profiles/apply',{id:plan.id,digest:plan.digest});await profileFinished();
   }
   status.textContent='Preparing storage move…';
   await api('storage/locations',{group:form.dataset.group,destination:form.elements.destination.value});
   await moved(before.job?.id);
   if(reload){status.textContent='Storage verified. Reloading the previous profile…';await api('profiles/'+encodeURIComponent(reload)+'/load',{});await profileFinished();}
   location.reload();
  }catch(error){status.textContent=error.message+' Original files were retained. Check Storage and Profiles before retrying.';disable(false);}
 }));
 // A copy continues on the appliance when the browser closes. A reopened page
 // reports it without assuming permission to reload a different profile.
 if(forms.some(form=>form.querySelector('button').disabled)){
  disable(true);
  (async()=>{try{for(;;){const value=await api('storage/locations');status.textContent=value.job?.message||'Preparing storage move…';if(!value.busy){location.reload();return;}await sleep();}}catch(error){status.textContent=error.message;}})();
 }
})();
