(()=>{
 const script=document.querySelector('script[src="/profile-progress.js"]');if(!script)return;
 async function poll(){try{if(!document.hidden){const r=await (window.xurFetch??window.fetch)('/api/profiles/progress');if(r.ok){const op=await r.json();if(!op||op.id!==script.dataset.operation||op.stage!==script.dataset.stage||String(op.completed)!==script.dataset.completed){location.reload();return;}}}}catch{}setTimeout(poll,3000);}setTimeout(poll,3000);
})();
