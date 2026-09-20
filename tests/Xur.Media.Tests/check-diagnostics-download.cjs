const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict'),{execFileSync}=require('node:child_process');
(async()=>{
 const name=process.argv[2],out=process.argv[3];assert(/^[a-z0-9-]+$/.test(name));
 const token=execFileSync('python3',['-c',"import sys,pathlib;sys.path.insert(0,'tests/Xur.Media.Tests');from test_auth import api_session;print(api_session(pathlib.Path('.build/vms')/sys.argv[1]))",name],{encoding:'utf8'}).trim();
 const base='https://xur-vm.test:18443';
 const browser=await chromium.launch({headless:true,args:['--host-resolver-rules=MAP xur-vm.test 127.0.0.1','--no-proxy-server']});
 try{
  const context=await browser.newContext({acceptDownloads:true,ignoreHTTPSErrors:true,permissions:['clipboard-read','clipboard-write']});
  const anonymous=await context.request.get('https://127.0.0.1:18443/api/diagnostics/display');assert.equal(anonymous.status(),401);
  await context.addCookies([{name:'xur.session',value:token,url:base,httpOnly:true,secure:true,sameSite:'Strict'}]);
  const page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base+'/diagnostics');await page.waitForFunction(()=>document.querySelector('#diagnostic-download')?.disabled===false,{},{timeout:120000});
  const report=JSON.parse(await page.locator('#diagnostic-report').inputValue());
  assert.equal(report.agentBundle,report.controlBundle);
  for(const [name,args] of Object.entries({nvidiaGpuMapping:['--query-gpu=index,pci.bus_id','--format=csv,noheader,nounits'],nvidiaTopology:['topo','-m'],nvidiaNvlinkStatus:['nvlink','--status']})){
   assert.equal(report.commands[name].command,'nvidia-smi');assert.deepEqual(report.commands[name].arguments,args);assert.equal(typeof report.commands[name].exitCode,'number');
  }
  const pending=page.waitForEvent('download');await page.getByRole('button',{name:'Download report',exact:true}).click();const download=await pending;
  assert.match(download.suggestedFilename(),/^xur-diagnostics-.*\.json$/);
  const text=fs.readFileSync(await download.path(),'utf8');assert.deepEqual(JSON.parse(text),report);assert(!text.includes(token));
  await page.getByRole('button',{name:'Copy report',exact:true}).click();await page.waitForFunction(()=>document.querySelector('#diagnostic-copy-status')?.textContent==='Report copied.');assert.equal(await page.locator('#diagnostic-copy-status').innerText(),'Report copied.');
  fs.mkdirSync(out,{recursive:true});
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)));
   await page.screenshot({path:path.join(out,`diagnostics-${label}.png`),fullPage:true});
  }
  assert.deepEqual(errors,[]);
  const receipt={result:'Passed',bundleId:report.controlBundle,anonymousDenied:true,exactDriverCommands:true,downloadMatchesReport:true,copyWorks:true,desktopAndMobile:true};
  fs.writeFileSync(path.join(out,'diagnostics-download.json'),JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
