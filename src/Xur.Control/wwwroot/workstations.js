(() => {
 const dialog=document.querySelector('#station-display-dialog');
 if(dialog){
  const output=dialog.querySelector('#station-display-json'),status=dialog.querySelector('[role=status]'),copy=dialog.querySelector('#station-display-copy'),download=dialog.querySelector('#station-display-download'),retry=dialog.querySelector('#station-display-retry');
  let request,source,objectUrl,json='';
  const release=()=>{request?.abort();if(objectUrl)URL.revokeObjectURL(objectUrl);objectUrl=null;download.removeAttribute('href');};
  async function readDisplay(){
   release();const current=new AbortController();request=current;
   json='';output.textContent='';output.hidden=true;copy.disabled=true;download.hidden=true;retry.hidden=true;status.textContent='Loading display details…';
   try{
    const response=await (window.xurFetch??window.fetch)(source.dataset.url,{signal:current.signal});
    if(!response.ok)throw Error('Could not load display details (HTTP '+response.status+'). Try again.');
    const data=await response.json();if(current.signal.aborted)return;
    json=JSON.stringify(data,null,2)+'\n';output.textContent=json;output.hidden=false;
    objectUrl=URL.createObjectURL(new Blob([json],{type:'application/json'}));download.href=objectUrl;
    download.download='xur-display-'+source.closest('.station-card').dataset.station.replace(/[^a-zA-Z0-9_-]/g,'_')+'.json';
    download.hidden=false;copy.disabled=false;status.textContent='';
   }catch(error){if(current.signal.aborted)return;status.textContent=error.message;retry.hidden=false;}
  }
  for(const button of document.querySelectorAll('.station-display-details'))button.addEventListener('click',()=>{
   source=button;dialog.querySelector('#station-display-name').textContent=button.dataset.name;dialog.showModal();readDisplay();
  });
  dialog.querySelector('#station-display-close').addEventListener('click',()=>dialog.close());
  dialog.addEventListener('close',release);
  retry.addEventListener('click',readDisplay);
  copy.addEventListener('click',async()=>{
   try{await navigator.clipboard.writeText(json);status.textContent='JSON copied.';}
   catch{const range=document.createRange();range.selectNodeContents(output);const selection=getSelection();selection.removeAllRanges();selection.addRange(range);output.focus();status.textContent='Clipboard access is unavailable. The JSON is selected for copying.';}
  });
 }

 for(const form of document.querySelectorAll('.station-add-user'))form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('button'),status=form.querySelector('[role=status]');button.disabled=true;try{const r=await (window.xurFetch??window.fetch)('/api/station-users',{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':form.elements.__RequestVerificationToken.value},body:JSON.stringify({name:form.elements.name.value})});if(!r.ok)throw Error((await r.json()).error||'Could not create user.');const account=await r.json(),select=document.querySelector('.station-create-form select[name=user]');select.add(new Option(account.name,account.username,true,true));status.textContent='User added and selected for this workstation.';form.reset();button.disabled=false;}catch(error){status.textContent=error.message;button.disabled=false;}};
 for(const card of document.querySelectorAll('.station-card')){
  card.querySelector('.copy-station-address')?.addEventListener('click',async e=>{const text=card.querySelector('.station-address').textContent;try{await navigator.clipboard.writeText(text);e.target.textContent='Copied';}catch{const range=document.createRange();range.selectNodeContents(card.querySelector('.station-address'));getSelection().removeAllRanges();getSelection().addRange(range);}});
  const form=card.querySelector('.station-pair');if(!form)continue;
  const status=form.querySelector('[role=status]'),select=form.elements.pairingId;
  form.querySelector('.refresh-pairings').addEventListener('click',async()=>{try{const r=await (window.xurFetch ?? window.fetch)('/api/workstations/'+form.dataset.station+'/pairings');const data=await r.json();if(!r.ok)throw Error(data.error||'Could not read pairing requests.');select.replaceChildren(new Option('Select client',''));for(const p of data.pairings)select.add(new Option(p.name+' · '+p.address,p.id));status.textContent=data.pairings.length?'Select your client and enter its PIN.':'Start pairing in Moonlight, then refresh.';}catch(e){status.textContent=e.message;}});
  form.addEventListener('submit',async e=>{e.preventDefault();const button=form.querySelector('button:not([type])');button.disabled=true;try{const r=await (window.xurFetch ?? window.fetch)('/api/workstations/'+form.dataset.station+'/pair',{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':form.elements.__RequestVerificationToken.value},body:JSON.stringify({pairingId:select.value,pin:form.elements.pin.value,name:form.elements.name.value})});if(!r.ok){const d=await r.json();throw Error(d.error||'Pairing failed.');}form.elements.pin.value='';status.textContent='Paired. Open Desktop in Moonlight.';}catch(e){status.textContent=e.message;}finally{button.disabled=false;}});
 }
})();
