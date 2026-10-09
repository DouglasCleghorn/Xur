(() => {
 const form=document.getElementById('backup-settings'), list=document.getElementById('backup-recordings'), message=document.getElementById('backup-message');
 if(!form||!list)return;
 const api=(path,body)=>window.XurRobot.api('backups/'+path,body);
 let refreshing=false;
 const say=text=>{message.textContent=text;};
 async function refresh(){
  if(refreshing)return;refreshing=true;
  try{
   const recordings=await api('recordings');list.replaceChildren();
   if(!recordings.length){const empty=document.createElement('p');empty.textContent='Backup history appears when recording starts. Originals stay on the robot.';list.append(empty);}
   for(const recording of recordings){
    const item=document.createElement('article'), heading=document.createElement('h3'), state=document.createElement('p');
    heading.textContent=recording.dataset+' · '+new Date(recording.createdAt).toLocaleString();
    state.textContent=(recording.state==='recording'?'Recording in progress. ':recording.outcome==='interrupted'?'Interrupted recording; episode completeness is unverified. ':'Completed recording. ')+
     'Backup: '+recording.state+(recording.verifiedAt&&recording.state==='verified'?' · verified '+new Date(recording.verifiedAt).toLocaleString():'');
    item.append(heading,state);
    if(recording.destination){const destination=document.createElement('p');destination.textContent='Destination: '+recording.destination;item.append(destination);}
    if(recording.error){const error=document.createElement('p');error.className='notice error';error.textContent=recording.error;item.append(error);}
    if(recording.nextAttemptAt&&recording.state==='failed'){const next=document.createElement('p');next.textContent='Automatic retry: '+new Date(recording.nextAttemptAt).toLocaleString();item.append(next);}
    if(recording.snapshotId&&recording.state!=='uploading'){
     const retry=document.createElement('button');retry.type='button';retry.className='secondary';retry.textContent=recording.state==='verified'?'Verify copy again':'Retry backup';
     retry.addEventListener('click',async()=>{retry.disabled=true;try{await api('retry',{recordingId:recording.recordingId});say('Backup queued. Original recordings and snapshots remain local.');await refresh();}catch(error){say(error.message);}finally{retry.disabled=false;}});item.append(retry);
    }
    if(recording.snapshotId){const details=document.createElement('details'), summary=document.createElement('summary'), checksum=document.createElement('code');summary.textContent='Snapshot checksum';checksum.textContent=recording.snapshotId;details.append(summary,checksum);item.append(details);}
    list.append(item);
   }
  }finally{refreshing=false;}
 }
 form.addEventListener('submit',async event=>{event.preventDefault();const submit=form.querySelector('button');submit.disabled=true;
  try{const saved=await api('settings',{url:form.elements.url.value.trim(),token:form.elements.token.value});form.elements.token.value='';form.elements.url.value=saved.url;
   say(saved.tokenStored?'Receiver saved. Pending snapshots will back up automatically.':'Remote backups disabled. Existing originals, snapshots and remote copies are retained.');await refresh();
  }catch(error){say(error.message);}finally{submit.disabled=false;}});
 api('settings').then(settings=>{form.elements.url.value=settings.url;form.elements.token.placeholder=settings.tokenStored?'Saved privately; leave blank to keep it':'Private receiver token';
  say(settings.tokenStored?'Remote receiver configured. Verification status is shown for each recording.':'Remote backup is unconfigured. Recordings still retain local originals and immutable snapshots.');return refresh();}).catch(error=>say(error.message));
 setInterval(()=>{if(!document.hidden)refresh().catch(error=>say(error.message));},5000);
})();
