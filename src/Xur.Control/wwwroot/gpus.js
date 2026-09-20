(()=>{
 const range=document.querySelector('#gpu-history-range'),updated=document.querySelector('#gpu-updated'),error=document.querySelector('#gpu-error');if(!updated)return;
 let current=null,timer,request=0;
 const units={utilization:'%',memoryUsedMiB:' GiB',memoryTotalMiB:' GiB',memoryFreeMiB:' GiB',powerWatts:' W',powerLimitWatts:' W',temperatureC:' °C',fanPercent:'%',fanRpm:' RPM',graphicsClockMHz:' MHz',memoryClockMHz:' MHz'};
 function format(value,key){if(value==null)return 'Not reported';if(key==='performanceState')return value;return (key.startsWith('memory')&&key.endsWith('MiB')?(value/1024).toFixed(1):Number(value.toFixed(1)).toString())+(units[key]??'');}
 function draw(figure,telemetry){
  const key=figure.dataset.series,span=Number(range.value)*60000,now=Date.parse(telemetry.reading.at),start=now-span;
  const points=telemetry.history.filter(p=>Date.parse(p.at)>=start);const valid=points.filter(p=>p[key]!=null);figure.querySelector('.chart-start').textContent=range.value==='1440'?'24 h ago':range.value==='60'?'1 h ago':'15 min ago';
  const svg=figure.querySelector('svg'),line=svg.querySelector('.chart-line'),dot=svg.querySelector('.chart-dot'),empty=figure.querySelector('.chart-empty');
  empty.hidden=valid.length>0;empty.textContent=telemetry.reading[key]==null?'Not reported by this device':'Collecting history';svg.setAttribute('aria-label',figure.querySelector('figcaption').textContent+' history. '+(valid.length?valid.length+' samples; latest '+format(valid.at(-1)[key],key):empty.textContent));
  if(!valid.length){line.setAttribute('d','');dot.setAttribute('hidden','');return;}
  const max=Math.max(1,key==='utilization'?100:key==='memoryUsedMiB'?telemetry.reading.memoryTotalMiB??0:key==='powerWatts'?telemetry.reading.powerLimitWatts??0:0,...valid.map(p=>p[key]));
  figure.querySelector('.chart-scale').textContent='0–'+format(max,key);
  let pen=false,path='';for(const p of points){if(p[key]==null){pen=false;continue;}const x=400*(Date.parse(p.at)-start)/span,y=96-90*p[key]/max;path+=(pen?'L':'M')+x.toFixed(2)+' '+y.toFixed(2);pen=true;}
  line.setAttribute('d',path);const last=valid.at(-1);dot.removeAttribute('hidden');dot.setAttribute('cx',Math.min(397,400*(Date.parse(last.at)-start)/span));dot.setAttribute('cy',96-90*last[key]/max);
  const output=figure.querySelector('output');output.textContent=format(last[key],key);svg.onpointermove=e=>{
   const box=svg.getBoundingClientRect(),at=start+span*Math.max(0,Math.min(1,(e.clientX-box.left)/box.width));const closest=valid.reduce((a,b)=>Math.abs(Date.parse(a.at)-at)<Math.abs(Date.parse(b.at)-at)?a:b);output.textContent=new Date(closest.at).toLocaleTimeString()+' · '+format(closest[key],key);
  };svg.onpointerleave=()=>output.textContent=format(last[key],key);
 }
 function render(data){
  const links=document.querySelector('#topology-links');links.replaceChildren();
  const topology=data.topology;
  for(const link of topology?.links??[]){
   if(link.connection!=='NVLink')continue;
   const row=document.createElement('div');row.className='topology-link';
   for(const pci of [link.from,link.to]){const a=document.createElement('a');a.href='#gpu-'+pci.replaceAll(':','-').replaceAll('.','-');a.textContent=(data.cards.find(c=>c.telemetry.device.pci===pci)?.telemetry.device.displayName??'GPU')+' · '+pci;row.append(a);}
   const state=document.createElement('span');state.className='link-status';state.textContent='↔ NVLink · '+link.state;const detail=document.createElement('small');detail.textContent=(link.linkCount??'?')+' links'+(link.speedGBps!=null?' · '+link.speedGBps+' GB/s per link':'');state.append(detail);row.insertBefore(state,row.lastChild);links.append(row);
  }
  document.querySelector('#topology-note').textContent=topology?.error??(links.children.length?'NVLink connects these cards; memory remains allocated per GPU.':topology?.state==='NotApplicable'?'No NVIDIA GPUs are present.':topology?.state==='Observed'?'The driver did not report an NVLink pair. See Diagnostics for the topology output.':'NVLink topology has not been read yet.');

  const panels=[...document.querySelectorAll('[data-gpu]')];
  if(panels.length!==data.cards.length||panels.some(p=>!data.cards.some(c=>c.telemetry.device.pci===p.dataset.gpu))){location.reload();return;}
  updated.textContent=data.capturedAt?'Updated '+new Date(data.capturedAt).toLocaleTimeString()+' · every 15 seconds':'Reading GPUs…';error.hidden=!data.error;error.textContent=data.error??'';
  for(const panel of panels){const card=data.cards.find(c=>c.telemetry.device.pci===panel.dataset.gpu),t=card.telemetry;
   for(const field of panel.querySelectorAll('[data-reading]'))field.textContent=format(t.reading[field.dataset.reading],field.dataset.reading);
   panel.querySelector('[data-driver-version]').textContent=t.driverVersion??'';
   const assignments=panel.querySelector('.gpu-workloads');assignments.replaceChildren();
   if(!card.workloads.length){const span=document.createElement('span');span.className='secondary-text';span.textContent='No workloads assigned';assignments.append(span);}
   for(const w of card.workloads){const row=document.createElement('span'),name=document.createElement('strong'),state=document.createElement('span');name.textContent=w.name;state.className='state-label '+(w.state==='running'?'running':'pending');state.textContent=w.state;row.append(name,state);assignments.append(row);}
   for(const figure of panel.querySelectorAll('.gpu-history'))draw(figure,t);
   const processes=panel.querySelector('.gpu-processes');processes.replaceChildren();
   for(const p of t.processes){const row=document.createElement('div');for(const text of [p.name,'PID '+p.pid,p.memoryMiB==null?'Not reported':p.memoryMiB.toFixed(0)+' MiB']){const span=document.createElement('span');span.textContent=text;row.append(span);}processes.append(row);}
   if(!t.processes.length){const p=document.createElement('p');p.className='secondary-text';p.textContent='No device processes observed';processes.append(p);}
  }
 }
 async function refresh(){const version=++request;clearTimeout(timer);try{const url='/api/gpus?minutes='+range.value;const data=window.xurTelemetry?await window.xurTelemetry(url):await (await (window.xurFetch ?? window.fetch)(url)).json();if(version!==request)return;current=data;render(data);}catch(e){if(version===request){error.hidden=false;error.textContent=e.message;}}finally{if(version===request&&!document.hidden)timer=setTimeout(refresh,15000);}}
 range.addEventListener('change',refresh);document.addEventListener('visibilitychange',()=>{if(document.visibilityState==='visible')refresh();else{++request;clearTimeout(timer);}});refresh();
})();
