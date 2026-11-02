const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
(async()=>{
 const elements=new Map(),requests=[];
 function element(id){if(!elements.has(id))elements.set(id,{hidden:false,disabled:true,checked:false,value:'',textContent:'',listeners:{},addEventListener(event,handler){this.listeners[event]=handler;},replaceChildren(){},querySelector(){return {value:'fixture-csrf'};}});return elements.get(id);}
 let storage={fullHostAccess:false,allDrives:true,models:true,folders:[],mcpRead:true,mcpWrite:false};
 const document={hidden:false,querySelector:()=>element('page'),getElementById:element};
 const fetch=async(url,options)=>{
  requests.push({url,options});
  let data={};
  if(url.endsWith('/status'))data={state:'Running',enabled:true,installed:true,workloadId:'fixture'};
  if(url.endsWith('/storage')){if(options?.method==='POST')storage=JSON.parse(options.body);data=storage;}
  if(url.endsWith('/mcp/connect'))data={message:'Connected'};
  return {ok:true,json:async()=>data};
 };
 const window={fetch,addEventListener(){}};
 vm.runInNewContext(fs.readFileSync('src/Xur.Control/wwwroot/t3-code.js','utf8'),{document,window,setInterval(){},Date,Error,navigator:{}});
 await new Promise(resolve=>setImmediate(resolve));
 const get=id=>element('t3-'+id);
 assert.equal(get('mcp-read').checked,true);
 assert.equal(get('mcp-write').checked,false);
 assert.equal(get('mcp-permissions').hidden,false);
 get('storage-full-host').checked=true;
 get('storage-full-host').listeners.change();
 assert.equal(get('mcp-permissions').hidden,true);
 assert.equal(get('mcp-host-note').hidden,false);
 await get('storage-form').listeners.submit({preventDefault(){}});
 assert.equal(storage.fullHostAccess,true);
 assert.equal(storage.mcpRead,true);
 assert.equal(storage.mcpWrite,false); // Preserve choices while the host override applies.
 get('storage-full-host').checked=false;
 get('storage-full-host').listeners.change();
 assert.equal(get('mcp-permissions').hidden,false);
 assert.equal(get('mcp-read').checked,true);
 assert.equal(get('mcp-write').checked,false);
 get('mcp-write').checked=true;
 await get('storage-form').listeners.submit({preventDefault(){}});
 assert.equal(storage.mcpWrite,true);
 assert.equal(storage.fullHostAccess,false);
 await get('mcp-connect').listeners.click();
 assert.equal(get('mcp-message').textContent,'Connected');
 assert.equal(requests.at(-1).options.headers.RequestVerificationToken,'fixture-csrf');
 console.log('T3 MCP permissions hide under host access, preserve choices, save and connect with CSRF.');
})().catch(error=>{console.error(error);process.exitCode=1;});
