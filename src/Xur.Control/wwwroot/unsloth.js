(()=>{
 const page=document.querySelector('#unsloth-page');if(!page)return;
 const find=id=>document.getElementById('unsloth-'+id),button=find('password');
 async function api(path,post=false){
  const response=await (window.xurFetch??window.fetch)('/api/unsloth/'+path,post?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':page.querySelector('[name=__RequestVerificationToken]').value},body:'{}'}:{});
  if(!response.ok){const error=await response.json();throw Error(error.error||'Unsloth is unavailable.');}return response.json();
 }
 function clear(){find('secret').textContent='';find('credential').hidden=true;}
 function fail(e){find('error').textContent=e.message;find('error').hidden=false;}
 async function status(){
  try{const data=await api('status');find('status').textContent=data.message;find('running').hidden=!data.running;find('open').hidden=!data.url;if(data.url)find('open').href=data.url;else find('open').removeAttribute('href');if(!data.running)clear();}
  catch(e){find('running').hidden=true;find('status').textContent='Status unavailable';clear();fail(e);}
 }
 button.addEventListener('click',async()=>{
  button.disabled=true;clear();find('error').hidden=true;
  try{const data=await api('password',true);find('secret').textContent=data.password;find('credential').hidden=false;}
  catch(e){fail(e);}finally{button.disabled=false;}
 });
 document.addEventListener('visibilitychange',()=>{if(document.hidden)clear();else status();});
 status();setInterval(()=>{if(!document.hidden)status();},15000);
})();
