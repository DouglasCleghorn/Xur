(()=>{
 for(const script of document.querySelectorAll('script[src="/page-poll.js"]')){
  async function poll(){try{if(!document.hidden){const response=await (window.xurFetch??window.fetch)(script.dataset.path);if(response.ok&&(await response.json()).version!==script.dataset.version&&!document.activeElement?.matches('input,textarea,select')){location.reload();return;}}}catch{}setTimeout(poll,5000);}setTimeout(poll,5000);
 }
})();
