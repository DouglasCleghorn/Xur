(()=>{
 const button=document.getElementById('t3-install');if(!button)return;
 const error=document.getElementById('t3-install-error'),csrf=document.querySelector('#t3-code-page [name=__RequestVerificationToken]').value;
 const key='xur-t3-install';let jobId=sessionStorage.getItem(key),checking=false;
 function busy(value){button.disabled=value;button.textContent=value?'Installing…':'Install';button.setAttribute('aria-busy',String(value));}
 function fail(message){error.textContent=message;error.hidden=false;}
 function clear(){jobId=null;sessionStorage.removeItem(key);busy(false);}
 async function api(path,body){
  const response=await (window.xurFetch??window.fetch)(path,body?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':csrf},body:JSON.stringify(body)}:{});
  const data=await response.json();if(!response.ok)throw Error(data.error||'Could not install T3 Code. Try again.');return data;
 }
 async function check(){
  if(!jobId||checking)return;checking=true;
  try{
   const jobs=await api('/api/container-jobs'),job=jobs.find(j=>j.id===jobId);
   if(job?.stage==='Ready'){clear();location.reload();return;}
   if(!job||['Failed','Interrupted'].includes(job.stage)){clear();fail('Installation did not finish. Try again.');return;}
   error.hidden=true;
  }catch{fail('Could not check installation. Retrying…');}
  finally{checking=false;}
 }
 button.addEventListener('click',async()=>{
  busy(true);error.hidden=true;
  try{const job=await api('/api/t3-code/prepare',{});if(job.stage==='Ready'){clear();location.reload();return;}jobId=job.id;sessionStorage.setItem(key,jobId);await check();}
  catch(e){busy(false);fail(e.message);}
 });
 if(jobId){busy(true);check();}
 setInterval(()=>{if(!document.hidden)check();},3000);
})();
