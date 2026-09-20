// Hardware receipts use the actual VM; four-card graph data is a browser-only layout fixture.
const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081';const jwt=fs.readFileSync(`.build/vms/${name}/session.private.cookies`,'utf8').match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try{
  const c=await browser.newContext();await c.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);const page=await c.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));
  let data;for(let n=0;n<40;n++){const r=await c.request.get(base+'/api/gpus');assert(r.ok());data=await r.json();if(data.capturedAt&&data.cards.length)break;await page.waitForTimeout(1000);}assert(data.cards.length);
  const state=await(await c.request.get(base+'/api/profiles')).json();const station=state.runtime.instances.find(i=>i.id==='management-desktop');assert(station);const card=data.cards.find(c=>c.telemetry.device.pci===station.gpus[0]);assert(card&&card.workloads.some(w=>w.id===station.id&&w.state==='running'));
  // This VM's VGA exposes no hardware power/utilization/VRAM sensors.
  assert.equal(card.telemetry.reading.powerWatts,null);assert.equal(card.telemetry.reading.memoryUsedMiB,null);
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844],['small-mobile',320,740]]){
   await page.setViewportSize({width,height});await page.goto(base+'/gpus');await page.waitForFunction(()=>document.querySelector('#gpu-updated')?.textContent.includes('every 15 seconds'));
   assert(await page.getByText('Gaming workstation',{exact:true}).isVisible());assert(await page.getByText('Not reported',{exact:true}).count()>0);assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));await page.screenshot({path:`.build/evidence/updates/gpus-${label}.png`,fullPage:true});
   await page.locator('.gpu-details>summary').first().click();assert(await page.getByText('Device processes',{exact:true}).first().isVisible());await page.locator('.gpu-details>summary').first().click();
   if(width<700){await page.locator('.mobile-nav-more summary').click();assert(await page.getByRole('link',{name:'GPUs',exact:true}).isVisible());await page.getByRole('link',{name:'GPUs',exact:true}).click();}
  }
  const firstAt=data.capturedAt;await page.waitForTimeout(16000);data=await(await c.request.get(base+'/api/gpus')).json();assert(Date.parse(data.capturedAt)>Date.parse(firstAt));assert(data.cards[0].telemetry.history.length>=2);
  // Browser fixture only: exercise multiple graphs, scales, null gaps, and range selection.
  const now=Date.now(),fixture=structuredClone(data);fixture.cards=Array.from({length:4},(_,i)=>{
   const card=structuredClone(data.cards[0]);card.telemetry.device.pci=`0000:0${i+1}:00.0`;card.telemetry.device.name='RTX 3090 · layout fixture';card.telemetry.device.vendor='NVIDIA';
   card.telemetry.history=Array.from({length:61},(_,n)=>({at:new Date(now-(60-n)*15000).toISOString(),utilization:n===30?null:30+20*Math.sin(n/8+i),memoryUsedMiB:n===30?null:8192+1024*Math.sin(n/6+i),memoryTotalMiB:24576,powerWatts:n===30?null:120+80*Math.sin(n/9+i),powerLimitWatts:350,temperatureC:52+8*Math.sin(n/10)}));
   card.telemetry.reading=card.telemetry.history.at(-1);card.workloads=[{id:'fixture-'+i,name:['Qwen LLM','Qwen LLM','Fish Speech','Gaming workstation'][i],engine:'Fixture',state:'running',pid:100+i}];return card;
  });
  const queried=[];await page.route('**/api/gpus?*',async route=>{queried.push(new URL(route.request().url()).searchParams.get('minutes'));await route.fulfill({json:fixture});});
  await page.setViewportSize({width:1440,height:1000});
  await page.evaluate(cards=>{
   const grid=document.querySelector('.gpu-grid'),template=grid.firstElementChild.cloneNode(true);grid.replaceChildren();
   for(const c of cards){const panel=template.cloneNode(true);panel.dataset.gpu=c.telemetry.device.pci;panel.querySelector('h2').textContent=c.telemetry.device.name;panel.querySelector('.gpu-card-heading>.secondary-text').textContent='NVIDIA';panel.querySelector('.gpu-identity').textContent=c.telemetry.device.pci;grid.append(panel);}
  },fixture.cards);
  await page.locator('#gpu-history-range').selectOption('60');await page.waitForFunction(()=>document.querySelector('.chart-line')?.getAttribute('d')?.includes('L'));
  assert((await page.locator('.chart-line').first().getAttribute('d')).split('M').length>=3,'Missing samples must break the line');
  assert((await page.locator('.chart-scale').first().innerText()).includes('24.0 GiB'));
  await page.locator('#gpu-history-range').selectOption('1440');await page.waitForFunction(()=>document.querySelector('.chart-start').textContent==='24 h ago');assert(queried.includes('1440'));
  await page.locator('#gpu-history-range').selectOption('15');await page.waitForFunction(()=>document.querySelector('.chart-start').textContent==='15 min ago');
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});assert(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1));await page.screenshot({path:`.build/evidence/updates/gpus-four-card-layout-fixture-${label}.png`,fullPage:true});
  }
  const anonymous=await browser.newContext();assert.equal((await anonymous.request.get(base+'/api/gpus')).status(),401);await anonymous.close();assert.deepEqual(errors,[]);
  const update=await(await c.request.get(base+'/api/application-updates')).json();const receipt={suite:'GpuPage',result:'Passed',bundle:update.current.id,realVmDrmInventory:true,realWorkstationAssignment:true,missingReadingsRemainNull:true,backgroundHistoryAccumulates:true,authenticatedOnly:true,desktopAndPhone:true,fourCardGraphs:'Browser-only layout fixture',nvidiaXmlAndSysfsParsing:'Unit fixtures; no physical NVIDIA execution claimed'};fs.writeFileSync('.build/evidence/updates/gpu-page.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.stack);process.exitCode=1});
