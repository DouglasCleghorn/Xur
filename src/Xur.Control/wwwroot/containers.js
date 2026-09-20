(()=>{
 const form=document.querySelector('#container-form');if(!form)return;
 const csrf=form.querySelector('[name=__RequestVerificationToken]').value,volumeRows=document.querySelector('#container-volumes'),jobs=document.querySelector('#container-jobs'),error=document.querySelector('#container-error'),submit=document.querySelector('#prepare-container');
 let volumes=[],observedReady=new Set(),loaded=false;
 const e=(tag,text)=>{const node=document.createElement(tag);if(text)node.textContent=text;return node;};
 async function api(path,body){const r=await (window.xurFetch ?? window.fetch)('/api/'+path,body?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':csrf},body:JSON.stringify(body)}:{});const value=await r.json();if(!r.ok)throw Error(value.error||'Request failed');return value;}
 form.elements.source.addEventListener('change',()=>{document.querySelector('#container-image').hidden=form.elements.source.value!=='image';document.querySelector('#container-build').hidden=form.elements.source.value!=='build';});
 form.elements.vendor.addEventListener('change',()=>document.querySelector('#container-gpu-count').hidden=form.elements.vendor.value==='CPU');
 api('container-volumes').then(v=>volumes=v).catch(()=>{});
 document.querySelector('#add-container-volume').addEventListener('click',()=>{
  const row=e('div');row.className='container-volume-row';
  const choose=e('select');choose.className='volume-choice';choose.add(new Option('New volume',''));volumes.forEach(v=>choose.add(new Option(v.name,v.id)));
  const name=e('input');name.className='volume-name';name.required=true;name.maxLength=80;name.value='Container data';
  const destination=e('input');destination.className='volume-destination';destination.required=true;destination.value='/data';
  for(const [title,input] of [['Volume',choose],['Name',name],['Mount in container',destination]]){const label=e('label',title);label.append(input);row.append(label);}
  choose.addEventListener('change',()=>{name.disabled=!!choose.value;if(choose.value)name.value=volumes.find(v=>v.id===choose.value).name;});
  const remove=e('button','Remove');remove.type='button';remove.className='secondary';remove.onclick=()=>row.remove();row.append(remove);volumeRows.append(row);
 });
 form.addEventListener('submit',async event=>{
  event.preventDefault();submit.disabled=true;error.hidden=true;
  try{
   const command=form.elements.command.value.trim()?JSON.parse(form.elements.command.value):[];
   if(!Array.isArray(command)||command.some(v=>typeof v!=='string'))throw Error('Command must be a JSON array of strings.');
   const environment={};for(const line of form.elements.environment.value.split('\n').filter(l=>l.trim())){const at=line.indexOf('=');if(at<1)throw Error('Environment needs NAME=value on each line.');environment[line.slice(0,at).trim()]=line.slice(at+1);}
   const files=[];for(const file of form.elements.files.files)files.push({name:file.name,content:await file.text()});
   const body={name:form.elements.name.value, image:form.elements.source.value==='image'?form.elements.image.value:null,dockerfile:form.elements.source.value==='build'?form.elements.dockerfile.value:null,files:form.elements.source.value==='build'?files:[],vendor:form.elements.vendor.value,gpuCount:form.elements.vendor.value==='CPU'?0:Number(form.elements.gpuCount.value),port:Number(form.elements.port.value),healthPath:form.elements.healthPath.value,command,environment,volumes:[...volumeRows.children].map(row=>({volume:row.querySelector('.volume-choice').value||null,name:row.querySelector('.volume-name').value,destination:row.querySelector('.volume-destination').value}))};
   await api('container-jobs',body);jobs.parentElement.open=true;await poll();jobs.scrollIntoView({block:'nearest'});
  }catch(e){error.textContent=e.message;error.hidden=false;submit.disabled=false;}
 });
 async function poll(){
  try{
   const list=await api('container-jobs');submit.disabled=list.some(j=>['Preparing','Building','Pulling'].includes(j.stage));
   if(loaded && list.some(j=>j.stage==='Ready'&&!observedReady.has(j.id))){location.reload();return;}
   observedReady=new Set(list.filter(j=>j.stage==='Ready').map(j=>j.id));loaded=true;jobs.replaceChildren();
   for(const job of list){const article=e('article');article.className='panel container-job';article.append(e('h3',job.stage));const details=e('details');details.open=!['Ready'].includes(job.stage);details.append(e('summary','Logs'),e('pre',job.log));article.append(details);if(job.stage==='Ready'){const link=e('a','Select in a profile');link.href='/profiles';article.append(link);}jobs.append(article);}
  }catch(e){error.textContent=e.message;error.hidden=false;}
 }
 poll();setInterval(()=>{if(!document.hidden)poll();},5000);
})();
