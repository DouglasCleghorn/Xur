(()=>{
 const dialog=document.querySelector('#delete-model-dialog');
 document.querySelector('#model-filter')?.addEventListener('input',e=>{const q=e.target.value.toLowerCase();for(const row of document.querySelectorAll('[data-model]'))row.hidden=!row.dataset.model.toLowerCase().includes(q);});
 document.addEventListener('click',event=>{
  const button=event.target.closest('[data-delete-model]');if(!button||button.disabled||!dialog)return;
  dialog.querySelector('[name=path]').value=button.dataset.deleteModel;
  dialog.querySelector('[name=deletePath]').value=button.dataset.deletePath;
  dialog.querySelector('h2').textContent='Delete '+button.dataset.deleteName+'?';
  dialog.querySelector('#delete-model-path').textContent=button.dataset.deletePath;
  dialog.querySelector('#delete-model-repository').hidden=button.dataset.deletePath===button.dataset.deleteModel;
  dialog.showModal();
 });
 document.querySelector('#cancel-model-delete')?.addEventListener('click',()=>dialog.close());
 dialog?.querySelector('form').addEventListener('submit',()=>{
  for(const button of dialog.querySelectorAll('button'))button.disabled=true;
  dialog.querySelector('button.danger').textContent='Deleting…';
  dialog.addEventListener('cancel',event=>event.preventDefault(),{once:true});
 });
})();
