(()=>{
 for(const family of document.querySelectorAll('.network-family')){
  const method=family.querySelector('select');
  function update(){for(const field of family.querySelectorAll('[data-static]'))field.disabled=method.value!=='manual';for(const field of family.querySelectorAll('[data-dns]'))field.disabled=method.value==='disabled';}
  method.addEventListener('change',update);update();
 }
 const change=document.querySelector('#network-change');
 if(change&&['Applying','Confirm'].includes(change.dataset.stage)){
  async function poll(){try{const response=await (window.xurFetch??window.fetch)('/api/network/settings');if(response.ok){const state=await response.json();if(state.pending?.id!==change.dataset.id||state.pending?.stage!==change.dataset.stage){location.reload();return;}}}catch{}setTimeout(poll,2000);}
  setTimeout(poll,2000);
 }
})();
