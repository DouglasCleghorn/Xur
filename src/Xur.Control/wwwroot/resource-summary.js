(()=>{
 if(!document.querySelector("#control-usage-status"))return;
 const status=document.querySelector('#control-usage-status');
 function set(key,used,total){const row=[...document.querySelectorAll('[data-usage]')].find(x=>x.dataset.usage===key);if(!row)return;const valid=Number.isFinite(used)&&Number.isFinite(total)&&total>0;row.querySelector('[data-usage-text]').textContent=valid?(used/1073741824).toFixed(1)+' / '+(total/1073741824).toFixed(1)+' GiB':'Not reported';const meter=row.querySelector('meter');meter.hidden=!valid;if(valid){meter.max=total;meter.value=Math.max(0,Math.min(total,used));}}
 function storageRows(snapshot){const root=document.querySelector('#control-storage-devices');if(!root)return;root.replaceChildren();const size=n=>n>=1099511627776?(n/1099511627776).toFixed(1)+' TiB':(n/1073741824).toFixed(1)+' GiB';const el=(tag,text,cls)=>{const e=document.createElement(tag);e.textContent=text;if(cls)e.className=cls;return e;};
  for(const disk of snapshot.disks){const row=el('article','','control-disk'),head=el('div','','control-disk-heading'),link=el('a',disk.model||disk.path);link.href='/storage';link.title=disk.model;head.append(link,el('span',size(disk.bytes)));row.append(head,el('div',disk.path,'secondary-text control-disk-path'));const files=snapshot.filesystems.filter(f=>disk.filesystems.includes(f.source)||f.mounts.some(m=>disk.mounts.includes(m)));
   if(!files.length)row.append(el('span',disk.mounts.length?'Usage unavailable':'Not mounted','secondary-text'));
   for(const f of files){const line=el('div','','control-disk-fs'),label=el('span',f.mounts[0]||f.source),meter=document.createElement('meter');label.title=f.mounts.join(', ');meter.min=0;meter.max=f.bytes;meter.value=f.used;meter.setAttribute('aria-label','Used space on '+f.source);line.append(label,meter,el('span',size(f.used)+' / '+size(f.bytes)));row.append(line);}root.append(row);
  }if(!snapshot.disks.length)root.textContent=snapshot.scanning?'Reading storage devices…':'No storage devices detected';document.querySelector('#control-storage-status').textContent=snapshot.error||('Filesystem usage '+(snapshot.capturedAt?'as of '+new Date(snapshot.capturedAt).toLocaleTimeString():'is loading')+'. Shared filesystems can appear under more than one device.');
 }
 async function refreshStorage(){try{const r=await (window.xurFetch ?? window.fetch)('/api/storage/usage');if(!r.ok)throw Error();storageRows(await r.json());}catch{document.querySelector('#control-storage-status').textContent='Storage refresh unavailable. Last readings shown.';}}
 let timer;async function refresh(){clearTimeout(timer);await refreshStorage();try{
  const [s,g]=await Promise.all([(window.xurFetch ?? window.fetch)('/api/system'),(window.xurFetch ?? window.fetch)('/api/gpus')]);if(!s.ok||!g.ok)throw Error('Usage unavailable. Last readings shown.');
  const [system,gpus]=await Promise.all([s.json(),g.json()]);set('ram',system.memoryUsedBytes,system.memoryTotalBytes);
  for(const row of document.querySelectorAll('[data-usage]'))if(!['storage','ram'].includes(row.dataset.usage))set(row.dataset.usage,null,null);
  for(const card of gpus.cards){const r=card.telemetry.reading;set(card.telemetry.device.pci,r.memoryUsedMiB==null?null:r.memoryUsedMiB*1048576,r.memoryTotalMiB==null?null:r.memoryTotalMiB*1048576);}
  status.textContent='Updated '+new Date().toLocaleTimeString();
 }catch(e){status.textContent=e.message;}finally{if(!document.hidden)timer=setTimeout(refresh,10000);}}
 document.addEventListener('visibilitychange',()=>{if(document.hidden)clearTimeout(timer);else refresh();});timer=setTimeout(refresh,10000);
})();
