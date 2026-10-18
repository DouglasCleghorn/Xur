(()=>{
 const page=document.querySelector('#t3-code-page');if(!page)return;
 const find=id=>document.getElementById('t3-'+id),error=find('error'),message=find('message'),pair=find('pair'),prepare=find('prepare');
 const csrf=page.querySelector('[name=__RequestVerificationToken]').value;
 let expires=0,running=false,pairing=false;
 const origin=new URL(location.href);origin.protocol='http:';origin.port='3773';origin.pathname='/';origin.search='';origin.hash='';find('open').href=origin.href;
 async function api(path,body){
  const response=await (window.xurFetch??window.fetch)('/api/t3-code/'+path,body?{method:'POST',headers:{'Content-Type':'application/json','RequestVerificationToken':csrf},body:JSON.stringify(body)}:{});
  const data=await response.json();if(!response.ok)throw Error(data.error||'T3 Code is unavailable. Retry after checking the workload.');return data;
 }
 function clearPair(){expires=0;find('pairing').hidden=true;find('link').value='';find('pair-open').removeAttribute('href');find('qr').replaceChildren();}
 function fail(e){error.textContent=e.message;error.hidden=false;}
 async function status(){
  try{
   const data=await api('status');running=data.state==='Running';find('status').textContent=data.state;
   find('running').hidden=!running;find('stopped').hidden=running;pair.disabled=!running||pairing;
   if(!running)clearPair();
  }catch(e){running=false;pair.disabled=true;find('status').textContent='Status unavailable';find('running').hidden=true;clearPair();fail(e);}
 }
 pair.addEventListener('click',async()=>{
  pairing=true;pair.disabled=true;error.hidden=true;message.textContent='';clearPair();
  try{
   const data=await api('pair',{});if(!running)return;
   expires=Date.parse(data.expiresAt);find('link').value=data.url;find('pair-open').href=data.url;
   // The QR is generated locally by Xur's QRCoder library, never a remote service.
   find('qr').innerHTML=data.qr;find('expiry').textContent='One use · Expires at '+new Date(expires).toLocaleTimeString();find('pairing').hidden=false;
  }catch(e){fail(e);}finally{pairing=false;pair.disabled=!running;}
 });
 find('copy').addEventListener('click',async()=>{
  try{await navigator.clipboard.writeText(find('link').value);message.textContent='Pairing link copied.';}
  catch{find('link').focus();find('link').select();message.textContent='Copy the selected pairing link.';}
 });
 prepare.addEventListener('click',async()=>{
  prepare.disabled=true;error.hidden=true;
  try{await api('prepare',{volume:find('volume').value||null});location.href='/containers';}
  catch(e){fail(e);prepare.disabled=false;}
 });
 status();setInterval(()=>{if(!document.hidden)status();},5000);
 setInterval(()=>{if(expires && Date.now()>=expires){clearPair();message.textContent='Pairing link expired. Create a new one to connect another device.';}},1000);
 window.addEventListener('pagehide',clearPair);
})();
