(() => {
 const button=document.getElementById('camera-capabilities-refresh'), message=document.getElementById('camera-capabilities-message'), list=document.getElementById('camera-capabilities-devices'), raw=document.getElementById('camera-capabilities-raw');
 if(!button)return;
 const dialog=document.getElementById('camera-modes-dialog'), copyButton=document.getElementById('camera-capabilities-copy');
 copyButton.disabled=true;
 const element=(tag,text)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
 const value=item=>item===null||item===undefined?'—':String(item);
 function table(headings,rows){
  const container=element('div');container.className='camera-capability-table';container.tabIndex=0;container.setAttribute('role','region');container.setAttribute('aria-label','Camera driver table');const table=element('table'),thead=element('thead'),heading=element('tr');
  for(const title of headings){const cell=element('th',title);cell.scope='col';heading.append(cell);}thead.append(heading);table.append(thead);const body=element('tbody');
  for(const row of rows){const line=element('tr');for(const cell of row)line.append(element('td',value(cell)));body.append(line);}table.append(body);container.append(table);return container;
 }
 function show(report){
  raw.value=JSON.stringify(report,null,2);copyButton.disabled=false;list.replaceChildren();
  message.textContent=`Inspected ${new Date(report.observedAt).toLocaleString()}. ${report.devices.length} capture interface(s). ${report.problems.join(' ')}`;
  for(const device of report.devices){
   const section=element('article');section.className='camera-capability-device';
   section.append(element('h3',`${device.selectedRoles.length?device.selectedRoles.join(' / ')+' · ':''}${device.name}`),element('p',`${device.path} · ${device.state}`));
   const driver=device.driver;
   section.append(element('p',[driver.card,driver.name,driver.version,driver.bus].filter(Boolean).join(' · ')||'Driver identity unavailable.'));
   if(driver.currentMode){const mode=driver.currentMode;section.append(element('p',`Reported driver mode: ${value(mode.width)} × ${value(mode.height)} · ${value(mode.pixelFormat)} · ${value(mode.framesPerSecond)} fps. The next capture may request a different mode.`));}
   for(const query of device.queries){if(query.problem)section.append(element('p',`${query.name}: ${query.problem}${query.exitCode===null?'':` (exit ${query.exitCode})`}`));}
   if(device.formats.length){
    const formats=element(dialog?'div':'details');formats.className='camera-capability-formats';formats.append(element(dialog?'h4':'summary','Supported capture modes'));
    for(const format of device.formats){formats.append(element('h4',`${format.pixelFormat} · ${format.description}`),table(['Resolution','Frame rates / intervals'],format.sizes.length?format.sizes.map(size=>[
     size.width!==null&&size.height!==null?`${size.width} × ${size.height}`:size.raw,
     size.intervals.map(interval=>interval.framesPerSecond!==null?`${interval.framesPerSecond} fps (${value(interval.seconds)} s)`:interval.raw).join('; ')||'Not reported'
    ]):[['Not reported','Not reported']]));}section.append(formats);
   }else section.append(element('p','No capture modes were reported. Check the query status and raw driver output below.'));
   if(device.controls.length){
    const controls=element(dialog?'details':'div');controls.append(element(dialog?'summary':'h4','Driver controls'),table(['Driver name / type','Current','Range / step / default','Flags / menu'],device.controls.map(control=>[
     `${control.name} (${control.type}) ${control.id}${control.payloadUninterpreted?' · payload retained as raw data':''}`,
     control.current,`${value(control.minimum)} … ${value(control.maximum)} / ${value(control.step)} / ${value(control.default)}`,
     [control.flags.join(', '),control.menu.map(entry=>`${entry.index}: ${entry.label}`).join('; ')].filter(Boolean).join(' · ')
    ])));section.append(controls);
   }else section.append(element('p','No controls were parsed. Check the query status and raw data below.'));
   const details=element('details');details.append(element('summary','Raw driver output'));
   for(const query of device.queries){details.append(element('h4',`${query.argument} · ${query.state}`));const input=element('textarea');input.className='raw';input.readOnly=true;input.rows=8;input.value=query.raw||query.problem||'No output';input.setAttribute('aria-label',`${device.name} ${query.name} raw output`);details.append(input);}section.append(details);list.append(section);
  }
 }
 async function refresh(){
  if(button.disabled)return;
  button.disabled=true;copyButton.disabled=true;raw.value='';list.replaceChildren();list.setAttribute('aria-busy','true');message.textContent='Reading camera driver capabilities…';
  try{show(await window.XurRobot.api('camera-capabilities'));}catch(error){message.textContent=error.message;}finally{button.disabled=false;list.setAttribute('aria-busy','false');}
 }
 button.addEventListener('click',refresh);
 if(dialog){
  document.getElementById('camera-modes-open').addEventListener('click',()=>{dialog.showModal();refresh();});
  document.getElementById('camera-modes-close').addEventListener('click',()=>dialog.close());
  document.getElementById('camera-modes-estop').addEventListener('click',()=>{dialog.close();document.getElementById('estop').click();});
 }
 copyButton.addEventListener('click',async()=>{
  if(!raw.value){message.textContent='Inspect camera capabilities before copying a report.';return;}
  try{await navigator.clipboard.writeText(raw.value);message.textContent='Camera capability JSON copied.';}
  catch{raw.closest('details').open=true;raw.focus();raw.select();message.textContent='Select and copy the report below.';}
 });
})();
