(()=>{
 const page=document.querySelector('#t3-code-page');if(!page)return;
 const find=id=>document.getElementById('t3-'+id),error=find('error'),message=find('message'),pair=find('pair');
 const csrf=page.querySelector('[name=__RequestVerificationToken]').value;
 let expires=0,running=false,pairing=false,changing=false,enabled=false,serviceError=false,statusSequence=0;
 const toggle=find('enabled'),retry=find('retry');
 async function api(path,body){
  const response=await (window.xurFetch??window.fetch)('/api/t3-code/'+path,body?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':csrf},body:JSON.stringify(body)}:{});
  const data=await response.json();if(!response.ok)throw Error(data.error||'T3 Code is unavailable. Retry after checking the workload.');return data;
 }
 function clearPair(){expires=0;find('pairing').hidden=true;find('link').value='';find('qr').replaceChildren();}
 function fail(e){serviceError=false;error.textContent=e.message;error.hidden=false;}
 async function status(){
  const sequence=++statusSequence;
  try{
   const data=await api('status');if(sequence!==statusSequence)return;running=data.state==='Running';find('status').textContent=data.state;
   enabled=data.enabled;
   if(!changing){toggle.checked=enabled;toggle.disabled=!data.installed||!data.workloadId;}
   retry.hidden=data.state!=='Failed';
   find('stopped').textContent=data.state==='Failed'?'T3 Code needs attention.':enabled?'T3 Code is starting.':'T3 Code is disabled.';
   if(data.error){error.textContent=data.error;error.hidden=false;serviceError=true;}else if(serviceError){error.hidden=true;serviceError=false;}
   find('running').hidden=!running;find('stopped').hidden=running;pair.disabled=!running||pairing;
   if(!running)clearPair();
  }catch(e){toggle.disabled=true;running=false;pair.disabled=true;find('status').textContent='Status unavailable';find('running').hidden=true;clearPair();fail(e);}
 }
 async function setEnabled(value){
  changing=true;statusSequence++;toggle.disabled=true;retry.disabled=true;error.hidden=true;
  try{await api('enabled',{enabled:value});enabled=value;}
  catch(e){fail(e);}
  finally{changing=false;toggle.checked=enabled;retry.disabled=false;await status();}
 }
 toggle.addEventListener('change',()=>setEnabled(toggle.checked));
 retry.addEventListener('click',()=>setEnabled(enabled));
 const storageFullHost=find('storage-full-host'),storageAll=find('storage-all'),storageModels=find('storage-models'),storageFolders=find('storage-folders'),storageSave=find('storage-save'),storageError=find('storage-error'),storageMessage=find('storage-message');
 const mcpRead=find('mcp-read'),mcpWrite=find('mcp-write');
 let storageLoaded=false,storageLoading=false;
 function storageFields(){find('storage-folders-label').hidden=storageAll.checked;find('mcp-permissions').hidden=storageFullHost.checked;find('mcp-host-note').hidden=!storageFullHost.checked;}
 storageFullHost.addEventListener('change',storageFields);
 function renderStorage(data){mcpRead.checked=data.mcpRead===true;mcpWrite.checked=data.mcpWrite===true;storageFullHost.checked=data.fullHostAccess===true;storageAll.checked=data.allDrives;storageModels.checked=data.models;storageFolders.value=(data.folders??[]).join('\n');storageFields();}
 storageAll.addEventListener('change',storageFields);
 async function loadStorage(){
  if(storageLoading)return;storageLoading=true;
  try{renderStorage(await api('storage'));for(const input of [storageFullHost,storageAll,storageModels,storageFolders,storageSave,mcpRead,mcpWrite])input.disabled=false;storageError.hidden=true;storageLoaded=true;}
  catch(e){storageError.textContent=e.message;storageError.hidden=false;}
  finally{storageLoading=false;}
 }
 find('storage-form').addEventListener('submit',async event=>{
  event.preventDefault();storageError.hidden=true;storageMessage.textContent='';
  for(const input of [storageFullHost,storageAll,storageModels,storageFolders,storageSave,mcpRead,mcpWrite])input.disabled=true;
  try{
   const data=await api('storage',{fullHostAccess:storageFullHost.checked,mcpRead:mcpRead.checked,mcpWrite:mcpWrite.checked,allDrives:storageAll.checked,models:storageModels.checked,folders:storageFolders.value.split('\n').map(s=>s.trim()).filter(Boolean)});
   renderStorage(data);clearPair();storageMessage.textContent='Access settings saved.';await status();
  }catch(e){storageError.textContent=e.message;storageError.hidden=false;}
  finally{for(const input of [storageFullHost,storageAll,storageModels,storageFolders,storageSave,mcpRead,mcpWrite])input.disabled=false;}
 });
 find('storage').addEventListener('toggle',()=>{if(find('storage').open&&!storageLoaded)loadStorage();});
 loadStorage();
 pair.addEventListener('click',async()=>{
  pairing=true;pair.disabled=true;error.hidden=true;message.textContent='';clearPair();
  try{
   const data=await api('pair',{});if(!running)return;
   expires=Date.parse(data.expiresAt);find('link').value=data.url;
   // The QR is generated locally by Xur's QRCoder library, never a remote service.
   find('qr').innerHTML=data.qr;find('expiry').textContent='One use · Expires at '+new Date(expires).toLocaleTimeString();find('pairing').hidden=false;
  }catch(e){fail(e);}finally{pairing=false;pair.disabled=!running;}
 });
 find('copy').addEventListener('click',async()=>{
  try{await navigator.clipboard.writeText(find('link').value);message.textContent='Pairing link copied.';}
  catch{find('link').focus();find('link').select();message.textContent='Copy the selected pairing link.';}
 });
 find('mcp-connect').addEventListener('click',async()=>{
  const button=find('mcp-connect'),result=find('mcp-message');button.disabled=true;result.textContent='Connecting Xur tools…';
  try{const data=await api('mcp/connect',{});result.textContent=data.message;}
  catch(e){result.textContent=e.message;}finally{button.disabled=false;}
 });
 status();setInterval(()=>{if(!document.hidden)status();},5000);
 setInterval(()=>{if(expires && Date.now()>=expires){clearPair();message.textContent='Pairing link expired. Create a new one to connect another device.';}},1000);
 window.addEventListener('pagehide',clearPair);
})();
