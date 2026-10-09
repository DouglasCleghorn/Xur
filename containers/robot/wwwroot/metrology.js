(() => {
 const form=document.getElementById('metrology-form'), documentInput=document.getElementById('metrology-document'), message=document.getElementById('metrology-message');
 if(!form)return;
 const say=text=>{message.textContent=text;};
 const api=(body)=>window.XurRobot.api('metrology',body);
 api().then(settings=>{documentInput.value=settings?JSON.stringify(settings,null,2):'';say(settings?.cameras.length?'Supplied measured settings saved. New scans will check resolution, pose ambiguity and reprojection. Joint calibration remains unapproved.':'No measured intrinsics supplied. Scans show pixel observations only.');}).catch(error=>say(error.message));
 form.addEventListener('submit',async event=>{event.preventDefault();const button=form.querySelector('button');button.disabled=true;
  try{const settings=JSON.parse(documentInput.value), saved=await api(settings);documentInput.value=JSON.stringify(saved,null,2);say('Measured settings saved. Run Check markers to evaluate visual pose candidates; no joint calibration was approved.');}
  catch(error){say(error.message);}finally{button.disabled=false;}});
 document.getElementById('metrology-clear').addEventListener('click',async()=>{
  try{const saved=await api({version:1,cameras:[],tags:[]});documentInput.value=JSON.stringify(saved,null,2);say('New scans use pixel observations only. Existing evidence remains unchanged.');}catch(error){say(error.message);}
 });
 document.getElementById('metrology-template').addEventListener('click',async()=>{
  try{const configuration=await window.XurRobot.api('configuration');if(!configuration)throw new Error('Save hardware setup before making a camera template.');
   documentInput.value=JSON.stringify({version:1,cameras:['head','hand'].map(name=>({name,device:configuration[name+'Camera'],width:null,height:null,fx:null,fy:null,cx:null,cy:null,distortionModel:'',distortion:[],measurementSource:''})),tags:[],maximumReprojectionRmsPixels:2,minimumCandidateSeparationPixels:0.5},null,2);
   say('Template created. Replace every null value with measured data, choose measured distortion, and add tags with id, referenceEdgeMeters and measurementSource. Nothing was saved.');
  }catch(error){say(error.message);}
 });
})();
