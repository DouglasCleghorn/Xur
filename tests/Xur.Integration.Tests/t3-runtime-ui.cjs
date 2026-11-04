// Exercise the real configuration client without a browser or external assets.
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const elements=new Map(),intervals=[];
function element(id){
 if(!elements.has(id))elements.set(id,{hidden:false,disabled:true,checked:false,value:'',textContent:'',listeners:{},
  addEventListener(type,callback){this.listeners[type]=callback;},replaceChildren(){this.innerHTML='';},focus(){},select(){}});
 return elements.get(id);
}
const page={querySelector(){return {value:'fixture-csrf'};}};
let runtime={mode:'Container',stage:'Ready'},state='Running';const calls=[];
const context={document:{hidden:false,querySelector(){return page;},getElementById:element},
 window:{addEventListener(){}},navigator:{clipboard:{async writeText(){}}},Date,Error,JSON,console,
 setInterval(callback,period){intervals.push({callback,period});}};
context.window.fetch=async(path,options={})=>{
 calls.push({path,...options});let data;
 const route=path.split('/').pop();
 if(route==='runtime'){
  if(options.method==='POST')runtime={mode:'Container',target:JSON.parse(options.body).mode,stage:'Preparing'};
  data={...runtime};
 }else if(route==='storage')data={allDrives:true,models:true,folders:[],fullHostAccess:false};
 else if(route==='status')data={state,enabled:true,installed:true,workloadId:'same-t3'};
 else if(route==='pair')data={url:'http://same-host:3773/pair#token=fixture',expiresAt:new Date(Date.now()+300000).toISOString(),qr:'<svg></svg>'};
 else throw Error('Unexpected API: '+path);
 return {ok:true,async json(){return data;}};
};
const find=id=>element('t3-'+id);
async function settle(){for(let i=0;i<4;i++)await new Promise(resolve=>setImmediate(resolve));}
async function poll(){intervals.find(i=>i.period===5000).callback();await settle();}
(async()=>{
 vm.runInNewContext(fs.readFileSync('src/Xur.Control/wwwroot/t3-code.js','utf8'),context);
 await settle();
 assert.equal(find('runtime-mode').value,'Container');
 await find('pair').listeners.click();const link=find('link').value;
 assert.ok(link.includes('same-host:3773'));
 find('runtime-mode').value='Native';find('runtime-mode').listeners.change();
 await find('runtime-form').listeners.submit({preventDefault(){}});
 const request=calls.find(c=>c.path.endsWith('/runtime')&&c.method==='POST');
 assert.deepEqual(JSON.parse(request.body),{mode:'Native'});
 assert.equal(request.headers.RequestVerificationToken,'fixture-csrf');
 assert.equal(find('runtime-mode').disabled,true);
 state='Stopped';runtime={mode:'Native',target:'Native',stage:'Switching'};
 await poll();
 assert.equal(find('link').value,link,'A temporary handoff must not discard an outstanding pairing link');
 assert.equal(find('storage-save').disabled,true);
 runtime={mode:'Native',stage:'Ready'};state='Running';await poll();
 assert.equal(find('runtime-mode').value,'Native');
 assert.equal(find('runtime-mode').disabled,false);
 assert.equal(find('link').value,link);
 runtime={mode:'Native',stage:'Failed',error:'Previous runtime restored.'};await poll();
 assert.equal(find('runtime-error').hidden,false);
 assert.equal(find('runtime-error').textContent,'Previous runtime restored.');
 console.log('T3 runtime UI: mode selection, authenticated request, progress, pairing retention and recovery passed.');
})().catch(error=>{console.error(error);process.exitCode=1;});
