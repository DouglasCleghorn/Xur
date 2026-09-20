(()=>{
 const $=id=>document.getElementById(id),form=$('api-key-create');if(!form)return;
 const token=form.querySelector('[name="__RequestVerificationToken"]').value;
 const message=text=>{$('api-key-message').textContent=text;};
 async function api(path,body){const r=await (window.xurFetch ?? window.fetch)(path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json',RequestVerificationToken:token},body:JSON.stringify(body)});const data=await r.json().catch(()=>null);if(!r.ok)throw Error(data?.error||'Request failed. Refresh and try again.');return data;}
 const date=value=>value?new Date(value).toLocaleString():'Never';
 async function refresh(){const keys=await api('/api/api-keys');const list=$('api-key-list');list.replaceChildren();if(!keys.length){list.textContent='No API keys yet.';return;}
  for(const k of keys){const row=document.createElement('article');row.className='panel';const title=document.createElement('h3');title.textContent=k.name;const detail=document.createElement('p');detail.textContent=`${k.scope} · ${k.revokedAt?'Revoked':k.expiresAt&&new Date(k.expiresAt)<=new Date()?'Expired':'Active'} · Expires ${date(k.expiresAt)}`;const usage=document.createElement('p');usage.className='secondary-text';usage.textContent=`Created ${date(k.createdAt)} · Last used ${date(k.lastUsedAt)} · ${k.requests} authorized requests${k.lastPath?' · '+k.lastMethod+' '+k.lastPath:''}`;row.append(title,detail,usage);
   if(!k.revokedAt){const revoke=document.createElement('button');revoke.className='secondary';revoke.textContent='Revoke';revoke.addEventListener('click',async()=>{if(!confirm(`Revoke “${k.name}”? Requests using this key will stop working.`))return;revoke.disabled=true;try{await api('/api/api-keys/'+encodeURIComponent(k.id)+'/revoke',{});await refresh();message('Key revoked.');}catch(e){message(e.message);revoke.disabled=false;}});row.append(revoke);}list.append(row);}
 }
 form.elements.scope.addEventListener('change',()=>{$('api-key-scope').textContent={diagnostics:'Read system status, profiles, GPU metrics, logs and graphics reports. No file downloads or configuration changes.',testing:'Diagnostics plus inference, test chat and benchmark start/cancel. Uses resources on loaded models.',automation:'Manage profiles, streaming, settings, updates and reboot; includes file downloads. Cannot manage API keys.'}[form.elements.scope.value];});
 form.addEventListener('submit',async e=>{e.preventDefault();const button=form.querySelector('button');button.disabled=true;try{const result=await api('/api/api-keys',{name:form.elements.name.value,scope:form.elements.scope.value,days:Number(form.elements.days.value)});$('api-key-secret').value=result.token;$('api-key-created').hidden=false;message('Key created. Save it before leaving this page.');await refresh();}catch(e){message(e.message);}finally{button.disabled=false;}});
 $('api-key-copy').addEventListener('click',async()=>{try{await navigator.clipboard.writeText($('api-key-secret').value);message('Key copied.');}catch{$('api-key-secret').focus();$('api-key-secret').select();message('Select and copy the key manually.');}});
 $('api-key-dismiss').addEventListener('click',()=>{$('api-key-secret').value='';$('api-key-created').hidden=true;message('Key hidden. It cannot be shown again.');});
 $('api-key-refresh').addEventListener('click',()=>refresh().catch(e=>message(e.message)));
 $('api-key-example').textContent=`curl '${location.origin}/api/workstations' \\\n  -H "Authorization: Bearer $XUR_API_KEY"`;
 refresh().catch(e=>message(e.message));
})();
