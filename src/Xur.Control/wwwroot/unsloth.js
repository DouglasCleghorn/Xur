(()=>{
 const page=document.querySelector('#unsloth-page');if(!page)return;
 const find=id=>document.getElementById('unsloth-'+id),button=find('submit');
 async function api(path,body){
  const response=await (window.xurFetch??window.fetch)('/api/unsloth/'+path,body?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':page.querySelector('[name=__RequestVerificationToken]').value},body:JSON.stringify(body)}:{});
  if(!response.ok){const error=await response.json();throw Error(error.error||'Unsloth is unavailable.');}return response.json();
 }
 function fail(e){find('error').textContent=e.message;find('error').hidden=false;}
 async function status(){
  try{const data=await api('status');find('status').textContent=data.message;find('running').hidden=!data.running;find('connect').hidden=!data.needsPassword;find('open').hidden=!data.ready||!data.url;if(data.ready&&data.url)find('open').href=data.url;else find('open').removeAttribute('href');}
  catch(e){find('running').hidden=true;find('connect').hidden=true;find('status').textContent='Status unavailable';fail(e);}
 }
 find('connect').addEventListener('submit',async e=>{
  e.preventDefault();button.disabled=true;find('error').hidden=true;
  const password=find('password').value;find('password').value='';
  try{const data=await api('connect',{password});if(!data.ready)throw Error(data.message);await status();}
  catch(e){fail(e);}finally{button.disabled=false;}
 });
 document.addEventListener('visibilitychange',()=>{if(document.hidden)find('password').value='';else status();});
 status();setInterval(()=>{if(!document.hidden)status();},15000);
})();
