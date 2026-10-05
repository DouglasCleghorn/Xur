(()=>{
 const root=document.getElementById('wifi-settings');if(!root)return;
 const pending=root.dataset.pending==='true',adapters=[];let connecting=false;
 async function api(path,body,token){
  let response;
  try{response=await (window.xurFetch??window.fetch)(path,{method:'POST',headers:{'Content-Type':'application/json',...(token?{RequestVerificationToken:token}:{})},body:JSON.stringify(body)});}
  catch{throw Error('The request was interrupted. Refresh adapters to check the connection before trying again.');}
  const data=await response.json().catch(()=>null);
  if(!response.ok)throw Error(data?.error||'Could not change Wi-Fi settings. Refresh and try again.');
  if(!data)throw Error('Could not read the Wi-Fi response. Refresh adapters to check the connection.');
  return data;
 }
 for(const card of root.querySelectorAll('.wifi-adapter')){
  const scan=card.querySelector('.wifi-scan'),form=card.querySelector('.wifi-connect'),select=card.querySelector('.wifi-network'),password=card.querySelector('.wifi-password'),passwordField=card.querySelector('.wifi-password-field'),submit=card.querySelector('.wifi-submit'),message=card.querySelector('.wifi-message'),security=card.querySelector('.wifi-security'),addresses=card.querySelector('.wifi-addresses');
  const identity={interface:card.dataset.interface,macAddress:card.dataset.mac};
  const token=form.querySelector('[name="__RequestVerificationToken"]')?.value??document.querySelector('meta[name=xur-csrf]')?.content;
  let busy=false,networks=[];
  const selected=()=>select.value===''?undefined:networks[Number(select.value)];
  function update(){
   const network=selected(),blocked=busy||connecting||pending;
   scan.disabled=blocked||card.dataset.available!=='true';
   select.disabled=blocked;
   passwordField.hidden=!network?.needsPassword;
   password.disabled=blocked||!network?.needsPassword;
   password.required=!!network?.needsPassword;
   password.minLength=network?.keyManagement==='sae'?1:8;
   password.maxLength=network?.keyManagement==='sae'?63:64;
   submit.disabled=blocked||!network?.supported;
   security.textContent=network?(network.supported?`${network.security==='--'?'Open':network.security} · Signal ${network.signal}% · ${network.bssid}${network.needsPassword?'':' · No password required'}`:'This network requires separate configuration. Choose an open, WPA2-Personal or WPA3-Personal network.') :'';
  }
  function say(text,error=false){message.setAttribute('role',error?'alert':'status');message.textContent=text;}
  adapters.push({update});update();
  select.addEventListener('change',()=>{password.value='';say('');update();});
  scan.addEventListener('click',async()=>{
   if(scan.disabled)return;
   busy=true;password.value='';networks=[];form.hidden=true;addresses.replaceChildren();update();say('Scanning for Wi-Fi networks…');
   try{
    networks=await api('/api/network/wifi/scan',identity,token);
    select.replaceChildren();
    for(const [index,network] of networks.entries()){
     const option=document.createElement('option');option.value=String(index);option.textContent=`${network.ssid} · ${network.signal}% · ${network.security==='--'?'Open':network.security}${network.supported?'':' · Unsupported'}`;option.disabled=!network.supported;select.append(option);
    }
    select.value=String(networks.findIndex(network=>network.supported));
    form.hidden=networks.length===0;
    say(networks.length?networks.some(network=>network.supported)?'Choose a network to connect.':'No supported networks found. WEP and enterprise networks require separate configuration.':'No visible networks found. Move closer to the access point and scan again.');
    scan.textContent='Scan again';
   }catch(error){say(error.message,true);}
   finally{busy=false;update();}
  });
  form.addEventListener('submit',async event=>{
   event.preventDefault();const network=selected();if(submit.disabled||!network?.supported)return;
   const request={...identity,ssid:network.ssid,bssid:network.bssid,keyManagement:network.keyManagement,password:network.needsPassword?password.value:''};
   password.value='';connecting=true;for(const adapter of adapters)adapter.update();say(`Connecting to ${network.ssid}…`);addresses.replaceChildren();
   try{
    const change=await api('/api/network/wifi/connect',request,token);
    if(change.stage!=='Kept')throw Error('The Wi-Fi connection has not been saved. Refresh adapters to check its status.');
    card.querySelector('.wifi-state').textContent='Connected';form.hidden=true;
    say(`Connected to ${network.ssid}. Saved for automatic reconnection.`);
    for(const address of change.addresses??[]){
     const ip=address.split('/')[0],link=document.createElement('a'),line=document.createElement('p');
     link.href=`https://${ip.includes(':')?'['+ip+']':ip}:${root.dataset.port}/settings/network`;link.textContent=link.href;line.append(link);addresses.append(line);
    }
   }catch(error){say(error.message,true);}
   finally{request.password='';connecting=false;for(const adapter of adapters)adapter.update();}
  });
 }
})();
