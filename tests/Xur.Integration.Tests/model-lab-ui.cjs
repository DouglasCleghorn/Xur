const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/fast/model-lab');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--control-panel-render',out],{stdio:'pipe'});
 let html=fs.readFileSync(path.join(out,'model-lab.html'),'utf8').replace(/(<form id="lab-token"[^>]*>)/, '$1<input name="__RequestVerificationToken" value="fixture-token">');
 const id='123456789012345678901234567890abce',now=new Date().toISOString();
 const gpu={pci:'0000:01:00.0',name:'NVIDIA GeForce RTX 3090',vendor:'NVIDIA',memoryMiB:24576};
 const target={workload:{id:'1',name:'Qwen test',route:'qwen',gpus:[gpu.pci],recipe:{name:'Qwen benchmark model',engine:'vLLM',kind:'Model',hub:{repository:'owner/Qwen-long-model-name',revision:'a'.repeat(40)},image:'registry.example/vllm@sha256:'+'b'.repeat(64),command:['--max-num-seqs','1']}},instance:{instanceId:'fixture-instance'}};
 const workloads=[{name:'Qwen test',kind:'Model',engine:'vLLM',state:'running',gpus:[gpu.pci]},{name:'Gaming workstation',kind:'Workstation',engine:'Plasma',state:'running',gpus:['0000:02:00.0']}];
 let run={id,state:'Completed',started:now,settings:{requests:2,concurrency:1,maxTokens:128,temperature:0,prompt:'A benchmark prompt'},target,context:{profile:'AI and gaming',bundle:'c'.repeat(64),workloads},samples:[{at:now,gpus:[{device:gpu,reading:{at:now,memoryUsedMiB:18000,memoryTotalMiB:24576}}],workloads}],responses:[{number:1,started:now,warmup:false,durationMs:1000,firstTokenMs:50,inputTokens:10,outputTokens:40,text:'<img src=x onerror=alert(1)> Test response',reasoning:'',finishReason:'length'}]};
 const summary=()=>({id,state:run.state,started:now,model:target.workload.recipe.hub.repository,completed:1,requested:2,meanFirstTokenMs:50,outputTokensPerSecond:40,peakVramMiB:{[gpu.pci]:18000}});
 let mutations=[],slow=false;
 const browser=await chromium.launch({headless:true});
 try{const page=await browser.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.route('http://lab.test/**',async r=>{
  const u=new URL(r.request().url()),method=r.request().method();const json=v=>r.fulfill({body:JSON.stringify(v),contentType:'application/json'});
  if(method==='POST'){
   assert.equal(r.request().headers().requestverificationtoken,'fixture-token');mutations.push({path:u.pathname,body:r.request().postDataJSON()});
   if(u.pathname==='/api/benchmarks'){run.state='Running';return json(run);}
   if(u.pathname.endsWith('/cancel')){run.state='Cancelled';return json({});}
   if(u.pathname==='/api/model-lab/chat'){
    if(slow){await new Promise(resolve=>setTimeout(resolve,1500));}
    const body=[{type:'delta',text:'<script>not executable</script> Hello 世界',reasoning:'A thought'},{type:'result',result:{text:'<script>not executable</script> Hello 世界',reasoning:'A thought',firstTokenMs:20,durationMs:250,outputTokens:9,finishReason:'stop'}}].map(x=>JSON.stringify(x)).join('\n')+'\n';
    return r.fulfill({body,contentType:'application/x-ndjson'}).catch(()=>{});
   }
  }
  if(u.pathname==='/api/model-lab/targets')return json([target]);
  if(u.pathname==='/api/benchmarks')return json([summary()]);
  if(u.pathname==='/api/benchmarks/'+id)return json({run,summary:summary()});
  if(u.pathname.endsWith('/export'))return json({run});
  if(u.pathname.endsWith('.js')||u.pathname.endsWith('.css'))return r.fulfill({body:fs.readFileSync('src/Xur.Control/wwwroot'+u.pathname),contentType:u.pathname.endsWith('.js')?'application/javascript':'text/css'});
  if(u.pathname.endsWith('.ttf'))return r.fulfill({body:fs.readFileSync('src/Xur.Control/wwwroot'+u.pathname)});
  return r.fulfill({body:html,contentType:'text/html'});
 });
 for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
  await page.setViewportSize({width,height});await page.goto('http://lab.test/model-lab');
  await page.locator('#bench-detail:not([hidden])').waitFor();await page.getByText('40.0 tokens/s',{exact:true}).waitFor();
  assert.equal(await page.locator('#bench-context').getByText(/Gaming workstation/).count(),1);
  assert.equal(await page.locator('#bench-export').getAttribute('href'),'/api/benchmarks/'+id+'/export');
  assert.equal(await page.locator('#bench-responses img').count(),0);
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),label+' benchmark overflow');
  await page.screenshot({path:path.join(out,label+'-benchmark.png'),fullPage:true});
  await page.locator('#bench-requests').fill('2');await page.locator('#bench-tokens').fill('128');await page.locator('#bench-start').click();await page.locator('#bench-cancel:not([hidden])').waitFor();
  await page.locator('#bench-cancel').click();await page.getByText('Cancelled · 1/2 requests',{exact:true}).waitFor();
  assert.equal(mutations.find(x=>x.path==='/api/benchmarks').body.requests,2);
  await page.locator('#lab-chat-tab').click();await page.locator('#chat-prompt').fill('Hello');await page.locator('#chat-send').click();await page.getByText(/9 output tokens/).waitFor();
  assert.equal(await page.locator('#chat-messages script').count(),0);
  assert((await page.locator('#chat-messages').textContent()).includes('Hello 世界'));
  await page.locator('#chat-prompt').fill('Follow-up');await page.locator('#chat-send').click();await page.getByText(/9 output tokens/).waitFor();
  const chatRequests=mutations.filter(x=>x.path.endsWith('/chat'));assert.equal(chatRequests.at(-1).body.messages.length,3);
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),label+' chat overflow');
  await page.screenshot({path:path.join(out,label+'-chat.png'),fullPage:true});
  slow=true;await page.locator('#chat-prompt').fill('Slow request');await page.locator('#chat-send').click();await page.locator('#chat-stop').click();await page.getByText('Stopped. The partial response is not included in the next message.').waitFor();slow=false;
  await page.locator('#chat-clear').click();assert.equal(await page.locator('#chat-messages article').count(),0);
 }
 assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'ModelLabUi',result:'Passed',desktopAndMobile:true,savedContextAndExport:true,cancellation:true,multiTurnChat:true,stopChat:true,untrustedOutputIsText:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.stack||e);process.exitCode=1;});
