(()=>{
 const dialog=document.querySelector('#home-reboot-dialog');if(!dialog)return;
 document.querySelector('#home-reboot').addEventListener('click',()=>dialog.showModal());
 document.querySelector('#home-reboot-cancel').addEventListener('click',()=>dialog.close());
})();
