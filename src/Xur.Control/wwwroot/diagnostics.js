(()=>{
 const report=document.querySelector('#diagnostic-report'),copy=document.querySelector('#diagnostic-copy'),refresh=document.querySelector('#diagnostic-refresh'),summary=document.querySelector('#diagnostic-summary'),devices=document.querySelector('#diagnostic-devices'),status=document.querySelector('#diagnostic-copy-status');
 if(!report)return;
 const download=document.querySelector('#diagnostic-download');
 async function load(){
  refresh.disabled=true;copy.disabled=true;download.disabled=true;status.textContent='';summary.textContent='Collecting display information…';
  try{
   const response=await (window.xurFetch ?? window.fetch)('/api/diagnostics/display');if(!response.ok)throw Error('Could not collect the report ('+response.status+').');
   const data=await response.json();report.value=JSON.stringify(data,null,2);summary.textContent=data.summary;devices.replaceChildren();
   for(const entry of data.inventory){
    const item=document.createElement('article'),title=document.createElement('h3'),reason=document.createElement('p');
    title.textContent=entry.gpu.name;reason.textContent=entry.workstationEligible?'Available for workstation':entry.excludedBecause.join('; ');item.append(title,reason);devices.append(item);
   }
   copy.disabled=false;download.disabled=false;
  }catch(e){summary.textContent=e.message;report.value='';devices.replaceChildren();}
  finally{refresh.disabled=false;}
 }
 copy.addEventListener('click',async()=>{
  try{
   if(navigator.clipboard&&window.isSecureContext)await navigator.clipboard.writeText(report.value);
   else{report.focus();report.select();if(!document.execCommand('copy'))throw Error();}
   status.textContent='Report copied.';
  }catch{report.focus();report.select();status.textContent='Press Ctrl+C or use Copy to copy the selected report.';}
 });
 download.addEventListener('click',()=>{
  if(!report.value)return;
  const url=URL.createObjectURL(new Blob([report.value+'\n'],{type:'application/json'}));
  const a=document.createElement('a');a.href=url;a.download='xur-diagnostics-'+new Date().toISOString().replace(/[:.]/g,'-')+'.json';
  document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
 });
 refresh.addEventListener('click',load);load();
})();
