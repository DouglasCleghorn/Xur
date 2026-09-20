// Per-page memory only. Never persist authenticated responses in the browser cache.
(()=>{
 const cache=new Map();let bytes=0;
 window.xurFetch=async(input,options={})=>{
  const url=new URL(typeof input==='string'?input:input.url,location.href);
  const method=(options.method??(input instanceof Request?input.method:'GET')).toUpperCase();
  const headers=new Headers(input instanceof Request?input.headers:undefined);new Headers(options.headers).forEach((v,k)=>headers.set(k,v));
  const own=url.origin===location.origin,token=document.querySelector('meta[name=xur-csrf]')?.content;
  if(own&&token)headers.set('RequestVerificationToken',token);
  const eligible=own&&token&&method==='GET'&&url.pathname.startsWith('/api/')&&!/^\/api\/(auth\/|bootstrap|api-keys)/.test(url.pathname)&&!/(\/download|\/export)$/.test(url.pathname);
  const previous=eligible?cache.get(url.href):null;
  if(previous)headers.set('If-None-Match',previous.etag);
  const response=await fetch(input,{...options,headers,credentials:own?'same-origin':options.credentials,cache:own?'no-store':options.cache});
  if(response.status===304&&previous)return new Response(previous.body,{status:200,headers:previous.headers});
  if(eligible&&response.ok&&response.headers.has('ETag')){
   const body=await response.clone().arrayBuffer();
   if(body.byteLength<=4*1024*1024){
    if(previous){bytes-=previous.body.byteLength;cache.delete(url.href);}
    while(cache.size&&(bytes+body.byteLength>8*1024*1024||cache.size>=32)){const key=cache.keys().next().value;bytes-=cache.get(key).body.byteLength;cache.delete(key);}
    const savedHeaders=new Headers(response.headers);savedHeaders.delete('Content-Encoding');savedHeaders.delete('Content-Length');
    cache.set(url.href,{body,headers:savedHeaders,etag:response.headers.get('ETag')});bytes+=body.byteLength;
   }
  }
  return response;
 };
 const histories=new Map();
 window.xurTelemetry=async(path)=>{
  const url=new URL(path,location.href),previous=histories.get(path);
  if(previous?.capturedAt)url.searchParams.set('since',previous.capturedAt);
  const response=await window.xurFetch(url.href);if(!response.ok)throw Error('Could not refresh monitoring.');
  const data=await response.json(),gpu=Array.isArray(data.cards),items=gpu?data.cards:data.adapters;
  const start=Date.parse(data.capturedAt)-Number(url.searchParams.get('minutes')??15)*60000;
  if(response.headers.get('X-Xur-History-Delta')==='true'&&previous){
   for(const item of items){const current=gpu?item.telemetry:item;const old=gpu?previous.cards.find(c=>c.telemetry.device.pci===current.device.pci)?.telemetry:previous.adapters.find(a=>a.name===current.name);
    const points=new Map((old?.history??[]).map(p=>[p.at,p]));for(const point of current.history)points.set(point.at,point);
    current.history=[...points.values()].filter(p=>Date.parse(p.at)>=start).sort((a,b)=>Date.parse(a.at)-Date.parse(b.at));
   }
  }
  if(histories.size>=6&&!histories.has(path))histories.delete(histories.keys().next().value);
  histories.set(path,data);return data;
 };
})();
