(() => {
 const byId=id=>document.getElementById(id), setup=byId('robot-setup');
 if(!setup)return;
 let configuration=null,skills=[],refreshing=false,lastDetection=null,lastJobs=null;
 const message=text=>{const node=byId('robot-message');node.textContent=text;node.hidden=!text;};
 const api=(path,body)=>window.XurRobot.api(path,body);
 async function refresh(){
  if(refreshing)return;refreshing=true;
  try{
  const status=await api('status');skills=status.skills;
  byId('robot-status').textContent=status.workloadId?`${status.mode} · ${status.job?.detail??'No active job'}`:'Load the Robotics profile to begin.';
  const problems=byId('robot-problems');problems.replaceChildren();for(const text of status.problems){const p=document.createElement('p');p.textContent=text;problems.append(p);}
  const container=byId('robot-skills'),select=byId('robot-sort').elements.skill,chosen=select.value;container.replaceChildren();select.replaceChildren(new Option('Select reviewed skill',''));
  for(const skill of skills){
   const p=document.createElement('p');p.append(`${skill.name} · ${skill.kind} · ${skill.arm} arm `);
   const verified=skill.verified&&skill.calibrationHash===status.calibrationHash;
   if(skill.kind==='emote'&&verified){const button=document.createElement('button');button.type='button';button.textContent='Trigger emote';button.addEventListener('click',()=>run('emotes',{skill:skill.id}));p.append(button);}
   if(skill.kind==='sort'&&verified)select.add(new Option(`${skill.name} → ${skill.bin}`,skill.id));
   if(!verified)p.append('Review required');container.append(p);
  }
  select.value=chosen;
  const detection=await api('detection');
  if(detection&&JSON.stringify(detection)!==lastDetection){
   setup.elements.leftPort.value=detection.leftPort;setup.elements.rightPort.value=detection.rightPort;
   lastDetection=JSON.stringify(detection);
  }
  const jobs=await api('jobs'),jobsSnapshot=JSON.stringify(jobs);
  if(jobsSnapshot!==lastJobs){const list=byId('robot-jobs');list.replaceChildren();
  for(const job of jobs.slice(0,10)){
   const p=document.createElement('p');p.textContent=`${job.kind} · ${job.state} · ${job.detail}`;list.append(p);
   if(['inspect-table','inspect-markers','sort','evaluate'].includes(job.kind)&&job.state!=='running'){
    const captures=await api(`jobs/${encodeURIComponent(job.id)}/captures`);
    for(const name of captures){const link=document.createElement('a');link.href=`/robot/api/jobs/${encodeURIComponent(job.id)}/captures/${encodeURIComponent(name)}`;link.textContent=name;link.target='_blank';link.rel='noopener';p.append(' · ',link);}
   }
   if(job.kind==='inspect-markers'&&job.state==='completed'){
    const report=await api(`jobs/${encodeURIComponent(job.id)}/markers`),details=document.createElement('details'),summary=document.createElement('summary');
    summary.textContent='Marker visibility';details.append(summary);
    for(const camera of report.cameras){
     const line=document.createElement('p');
     line.textContent=`${camera.name}: ${camera.markers.length?camera.markers.map(m=>`tag ${String(m.id).padStart(2,'0')} in ${m.detectedFrames}/${camera.frames.length} frames`).join(', '):'no unambiguous tags detected'}`;
     details.append(line);
     const duplicates=[...new Set(camera.frames.flatMap(f=>f.ambiguousDuplicateIds))];
     if(duplicates.length){const warning=document.createElement('p');warning.textContent=`Multiple copies of tag ${duplicates.join(', ')} appeared in a frame; those observations are excluded.`;details.append(warning);}
    }
    const scope=document.createElement('p');scope.textContent='Shared tags: '+(report.sharedIds.join(', ')||'none')+'. Visibility has only been checked at this pose. Camera calibration and joint limits remain unverified.';details.append(scope);list.append(details);
   }
  }
  lastJobs=jobsSnapshot;
  }
  }finally{refreshing=false;}
 }
 async function run(path,body){try{const data=await api(path,body);message(data.detail??'Robot request accepted.');await refresh();}catch(error){message(error.message);}}
 async function loadSetup(){
  const devices=await api('devices');configuration=await api('configuration');
  for(const [name,items] of [['leftPort',devices.ports],['rightPort',devices.ports],['controllerDevice',devices.controllers],['headCamera',devices.cameras],['handCamera',devices.cameras]]){
   const select=setup.elements[name];select.replaceChildren(new Option('Select device',''));
   for(const item of items)select.add(new Option(item.name,item.path));
   if(configuration?.[name]&&!items.some(d=>d.path===configuration[name]))select.add(new Option((name.endsWith('Camera')?'Saved alias (confirm interface): ':'Disconnected: ')+configuration[name],configuration[name]));
   select.value=configuration?.[name]??'';
  }
  lastDetection=null;
  for(const name of ['maxLoadRaw','maxCurrentRaw','maxFollowingErrorDegrees'])setup.elements[name].value=configuration?.motorLimits?.[name]??'';
  if(configuration){setup.elements.robotId.value=configuration.robotId;setup.elements.motionEnabled.checked=configuration.motionEnabled;}
 }
 setup.addEventListener('submit',async event=>{event.preventDefault();try{
  configuration=await api('configuration');const body={skills:configuration?.skills??[]};
  for(const name of ['robotId','leftPort','rightPort','controllerDevice','headCamera','handCamera'])body[name]=setup.elements[name].value;
  const limits=['maxLoadRaw','maxCurrentRaw','maxFollowingErrorDegrees'];
  body.motorLimits=limits.some(name=>setup.elements[name].value!=='')?Object.fromEntries(limits.map(name=>[name,Number(setup.elements[name].value)])):null;
  body.motionEnabled=setup.elements.motionEnabled.checked;await api('configure',body);message('Setup saved. Check installed tools, then inspect hardware before enabling motion.');await loadSetup();await refresh();
 }catch(error){message(error.message);}});
 byId('robot-probe').addEventListener('click',()=>run('probe',{}));
 byId('robot-detect').addEventListener('click',()=>run('detect-buses',{}));
 byId('robot-inspect').addEventListener('click',()=>run('tasks',{kind:'inspect-table'}));
 byId('robot-markers').addEventListener('click',()=>run('tasks',{kind:'inspect-markers'}));
 byId('robot-controller').addEventListener('click',()=>run('controller',{seconds:Number(byId('robot-arm').elements.seconds.value)}));
 byId('robot-arm').addEventListener('submit',event=>{event.preventDefault();run('arm',{seconds:Number(event.target.elements.seconds.value)});});
 for(const [id,path,fields,numbers] of [['robot-record','record',['dataset','arm','task','seconds'],['seconds']],['robot-train','train',['dataset','policy','steps'],['steps']]]){
  byId(id).addEventListener('submit',event=>{event.preventDefault();const body={};for(const name of fields)body[name]=numbers.includes(name)?Number(event.target.elements[name].value):event.target.elements[name].value;run(path,body);});
 }
 function skillRequest(){const form=byId('robot-skill').elements,skill={policyType:'act'};for(const name of ['id','name','kind','arm','task','assetPath','bin','episode','seconds'])skill[name]=['episode','seconds'].includes(name)?Number(form[name].value):form[name].value;return {skill};}
 byId('robot-skill').addEventListener('submit',event=>{event.preventDefault();run('skills/review',skillRequest());});
 byId('robot-evaluate').addEventListener('click',()=>run('skills/evaluate',skillRequest()));
 byId('robot-sort').addEventListener('submit',event=>{event.preventDefault();const form=event.target.elements,skill=skills.find(s=>s.id===form.skill.value);if(!skill)return;run('tasks',{kind:'sort',instructions:form.instructions.value,items:[{object:form.object.value,bin:skill.bin,skill:skill.id}]});});
 loadSetup().then(refresh).catch(error=>message(error.message));
 setInterval(()=>{if(!document.hidden)refresh().catch(error=>message(error.message));},3000);
})();
