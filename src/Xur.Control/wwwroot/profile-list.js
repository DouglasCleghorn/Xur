(()=>{
 const search=document.querySelector('#profile-search'),rows=[...document.querySelectorAll('[data-profile]')],dialog=document.querySelector('#delete-profile-dialog');
 search?.addEventListener('input',()=>{
  const term=search.value.trim().toLocaleLowerCase();for(const row of rows)row.hidden=!row.dataset.search.toLocaleLowerCase().includes(term);
  document.querySelector('#profile-no-matches').hidden=!rows.length||rows.some(row=>!row.hidden);
 });
 document.addEventListener('click',event=>{
  const button=event.target.closest('[data-delete-id]');if(!button)return;
  dialog.querySelector('[name=id]').value=button.dataset.deleteId;dialog.querySelector('[name=revision]').value=button.dataset.deleteRevision;
  dialog.querySelector('h2').textContent='Delete '+button.dataset.deleteName+'?';dialog.showModal();
 });
 document.querySelector('#cancel-profile-delete')?.addEventListener('click',()=>dialog.close());
})();
