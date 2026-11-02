(()=>{
 const panel=document.querySelector('#network-shares');if(!panel)return;
 const message=panel.querySelector('#network-share-message'),token=panel.querySelector('[name=__RequestVerificationToken]').value;
 let busy=false;
 async function api(path,body){
  const response=await (window.xurFetch??window.fetch)('/api/storage/network'+path,{method:'POST',headers:{'Content-Type':'application/json',RequestVerificationToken:token},body:JSON.stringify(body)});
  const result=await response.json().catch(()=>({}));if(!response.ok)throw Error(result.error||'The operation failed. Refresh to check its status.');return result;
 }
 async function act(path,body,reload=true){
  if(busy)return;busy=true;panel.setAttribute('aria-busy','true');message.textContent='Working…';
  try{await api(path,body);message.textContent=path==='/connections/action'&&body.action==='test'?'SMB login and filesystem access verified.':'Saved.';if(reload)location.reload();}
  catch(error){message.textContent=error.message;}
  finally{busy=false;panel.removeAttribute('aria-busy');}
 }
 function details(form){const value=Object.fromEntries(new FormData(form));for(const name of ['uid','gid'])if(name in value)value[name]=Number(value[name]);for(const name of ['readOnly','encrypt'])if(form.elements[name])value[name]=form.elements[name].checked;if(form.elements.allowedClients)value.allowedClients=form.elements.allowedClients.value.split(/[\s,]+/).filter(Boolean);return value;}
 for(const [id,path] of [['smb-connection-form','/connections'],['smb-host-form','/hosted']]){
  const form=panel.querySelector('#'+id);form.addEventListener('submit',event=>{event.preventDefault();const body=details(form);act(path,body).finally(()=>{form.elements.password.value='';});});
 }
 for(const form of panel.querySelectorAll('.smb-access-form'))form.addEventListener('submit',event=>{event.preventDefault();act('/hosted/access',{id:form.dataset.share,allowedClients:details(form).allowedClients});});
 const server=panel.querySelector('#smb-server-form');
 server.addEventListener('submit',event=>{event.preventDefault();act('/server',{address:server.elements.address.value,enabled:true});});
 panel.querySelector('#smb-stop').addEventListener('click',()=>act('/server',{address:server.elements.address.value,enabled:false}));
 panel.querySelector('#smb-runtime').addEventListener('click',()=>act('/runtime',{}));
 for(const button of panel.querySelectorAll('[data-connection]'))button.addEventListener('click',()=>act('/connections/action',{id:button.dataset.connection,action:button.dataset.action},button.dataset.action!=='test'));
 for(const button of panel.querySelectorAll('[data-remove-share]'))button.addEventListener('click',()=>act('/hosted/remove',{id:button.dataset.removeShare,action:'remove'}));
 if(panel.dataset.busy==='true')setTimeout(()=>location.reload(),5000);
})();
