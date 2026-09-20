(()=>{
 const script=document.querySelector('script[src="/storage-usage.js"]');let previous;
 async function poll(){try{if(!document.hidden&&!document.querySelector('details[open]')){const r=await (window.xurFetch??window.fetch)('/api/storage/usage');if(r.ok){const data=await r.json(),state=JSON.stringify(data);if(previous!==undefined&&previous!==state||script?.dataset.scanning==='true'&&!data.scanning){location.reload();return;}previous=state;}}}catch{}setTimeout(poll,script?.dataset.scanning==='true'?4000:60000);}poll();
})();
