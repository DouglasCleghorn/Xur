const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
 const context=await browser.newContext();const page=await context.newPage();const errors=[],mutations=[],sizes=[];let listingCalls=0,usageScenario=false,mountScans=0;
 page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{window.copied=[];Object.defineProperty(navigator,'clipboard',{value:{writeText:async value=>window.copied.push(value)}});});
 const initial=[{name:'.local',kind:'folder',modified:1700000000},{name:'steam-123.log',kind:'file',bytes:128,modified:1700000000},{name:'archive.zip',kind:'file',bytes:256,modified:1700000000},{name:'<script>alert(1)</script>',kind:'link',modified:1700000000}];let items=structuredClone(initial);
 await context.route('http://files.test/**',async r=>{
  const u=new URL(r.request().url());
  if(usageScenario&&u.pathname==='/api/storage/mounts')return r.fulfill({json:[['ssd','/var'],['etc','/etc'],['broken','/unreadable']].map(([id,path])=>({id,path,source:'/dev/nvme0n1p2',uuid:'shared-fixture',bytes:2000000000000,available:600000000000,readOnly:false}))});
  if(usageScenario&&u.pathname==='/api/storage/files/size'){
   sizes.push(u.href);const id=u.searchParams.get('id'),path=u.searchParams.get('path');
   if(id==='broken'||path==='folder-18')return r.fulfill({status:503,json:{error:'Permission denied.'}});
   const scanning=id==='ssd'&&!path&&mountScans++===0;
   const bytes=id==='etc'?16000000:!path?900000000:path==='folder-19'?512000000:1000000;
   const children=Object.fromEntries(Array.from({length:20},(_,i)=>['folder-'+String(i).padStart(2,'0'),{bytes:i===18?null:i===19?512000000:1000000,partial:i===17,scanning:false,error:i===18?'Permission denied.':null}]));
   return r.fulfill({json:{bytes:scanning?null:bytes,partial:id==='etc'||path==='folder-17',scanning,capturedAt:'2026-10-31T18:00:00Z',children}});
  }
  if(usageScenario&&u.pathname==='/api/storage/files/list')return r.fulfill({json:{ok:true,entries:u.searchParams.get('path')?[{name:'payload',kind:'file',bytes:512000000,allocated:512000000}]:[...Array.from({length:20},(_,i)=>({name:'folder-'+String(i).padStart(2,'0'),kind:'folder',bytes:null})),{name:'sparse',kind:'file',bytes:128000000,allocated:0}],truncated:false}});
  if(u.pathname==='/api/storage/mounts')return r.fulfill({json:[{id:'ssd',path:'/var',source:'/dev/nvme0n1p2',type:'btrfs',bytes:1000000000,used:400000000,available:600000000,readOnly:false}]});
  if(u.pathname==='/api/files/roots')return r.fulfill({json:[{username:'station',name:'Gaming workstation',home:'/var/home/station'}]});
  if(u.pathname.endsWith('/size')){sizes.push(u.href);return r.fulfill({json:{bytes:290000000,partial:false,scanning:false,capturedAt:'2026-09-20T18:00:00Z',children:{models:{bytes:290000000,partial:false,scanning:false}}}});}
  if(u.pathname==='/api/storage/files/list')return r.fulfill({json:{ok:true,entries:[{name:'models',kind:'folder',bytes:null,modified:1700000000},{name:'<script>bad</script>',kind:'link',bytes:null,modified:1700000000}],truncated:false}});
  if(u.pathname==='/api/files/list'){listingCalls++;const data=u.searchParams.get('path')?[{name:'steam-123.log',kind:'file',bytes:128,modified:1700000000}]:structuredClone(items);return r.fulfill({json:{ok:true,entries:data,truncated:false}});}
  if(r.request().method()==='POST'){
   const body=r.request().postDataJSON();mutations.push({path:u.pathname,target:u.searchParams.get('path'),body});
   if(u.pathname.endsWith('/move')){items.find(i=>i.name===u.searchParams.get('path')).name=body.destination;}
   if(u.pathname.endsWith('/delete'))items=items.filter(i=>i.name!==u.searchParams.get('path'));
   return r.fulfill({json:{ok:true}});
  }
  const asset=/\.(css|js|ttf|svg)$/.test(u.pathname),file=u.pathname==='/vendor/ag-grid/ag-grid-community.min.js'?'src/Xur.Control/.build/libman/ag-grid/dist/ag-grid-community.min.js':asset?'src/Xur.Control/wwwroot'+u.pathname:'.build/fast/control-panel/files.html';return r.fulfill({body:fs.readFileSync(file),contentType:u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.ttf')?'font/ttf':u.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
 });
 async function newTab(link,expected,click={button:'middle'}){
  const original=page.url(),href=await link.getAttribute('href'),destination=new URL(href,original);
  for(const [key,value] of Object.entries(expected))assert.equal(destination.searchParams.get(key),value,'Link destination '+key);
  const [opened]=await Promise.all([context.waitForEvent('page',{timeout:10000}),link.click(click)]);
  try{
   opened.on('pageerror',e=>errors.push(e.message));await opened.waitForLoadState('domcontentloaded');assert.equal(opened.url(),destination.href,'New tab destination');
   const panel=expected.tab==='storage'?'#usage-explorer':'#file-explorer';await opened.locator(panel).waitFor();await opened.locator(panel+' .ag-root').waitFor();
   assert.equal(page.url(),original,'Opening a new tab leaves the original location unchanged');
  }finally{await opened.close();}
 }
 for(const width of [1440,390]){
  items=structuredClone(initial);await page.setViewportSize({width,height:950});await page.goto('http://files.test/files');
  await page.locator('#usage-list .ag-root').waitFor();assert(await page.locator('#file-explorer').isHidden());
  await newTab(page.locator('#usage-list').getByRole('link',{name:'/var',exact:true}),{tab:'storage',mount:'ssd',folder:''});
  await page.locator('#usage-list').getByRole('link',{name:'/var',exact:true}).click();
  await page.locator('#usage-list').getByRole('link',{name:'models',exact:true}).waitFor();
  assert.equal(await page.locator('#usage-list script').count(),0);
  await newTab(page.locator('#usage-list').getByRole('link',{name:'models',exact:true}),{tab:'storage',mount:'ssd',folder:'models'});
  await newTab(page.getByRole('tab',{name:'Workstation files',exact:true}),{tab:'workstation'});
  await newTab(page.locator('#usage-path').getByRole('link',{name:'↑ Up',exact:true}),{tab:'storage'});
  await page.locator('#usage-list').getByRole('button',{name:'Actions for models',exact:true}).click();
  await newTab(page.getByRole('menuitem',{name:'Open folder',exact:true}),{tab:'storage',mount:'ssd',folder:'models'});
  await page.keyboard.press('Escape');
  await page.getByRole('button',{name:'Copy directory path',exact:true}).click();assert.equal(await page.evaluate(()=>window.copied.at(-1)),'/var');
  await page.locator('#usage-list').getByRole('button',{name:'Actions for models',exact:true}).click();
  assert((await page.getByRole('menuitem',{name:'Download ZIP',exact:true}).getAttribute('href')).includes('/api/storage/files/download'));
  await page.getByRole('menuitem',{name:'Refresh folder size',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#usage-list').textContent.includes('276.6 MiB'));
  assert(sizes.some(u=>u.includes('refresh=true')));
  await page.locator('#usage-list').getByRole('link',{name:'models',exact:true}).focus();await page.keyboard.press('Enter');await page.waitForURL('**folder=models');await newTab(page.locator('#usage-path').getByRole('link',{name:'/var',exact:true}),{tab:'storage',mount:'ssd',folder:''});await page.goBack();await page.waitForURL('**folder=');
  await page.getByRole('tab',{name:'Workstation files',exact:true}).click();
  await page.locator('#file-status').getByText('4 of 4 items',{exact:true}).waitFor();
  await page.locator('#file-filter').fill('steam');await page.locator('#file-status').getByText('1 of 4 items',{exact:true}).waitFor();
  await page.locator('#file-clear').click();await page.locator('#file-status').getByText('4 of 4 items',{exact:true}).waitFor();
  // Numeric sort really compares numeric values, rather than formatted size text.
  await page.locator('#file-list .ag-header-cell[col-id="bytes"] .ag-header-cell-label').click();
  await page.locator('#file-list [role=row] [col-id=name]').filter({hasText:'steam-123.log'}).waitFor();
  const fileOrder=await page.locator('#file-list [role=row]').evaluateAll(rs=>rs.filter(r=>/steam-123.log|archive.zip/.test(r.textContent)).sort((a,b)=>Number(a.getAttribute('row-index'))-Number(b.getAttribute('row-index'))).map(r=>r.querySelector('[col-id=name]').textContent));assert.deepEqual(fileOrder,['steam-123.log','archive.zip']);
  await page.locator('#file-list .ag-header-cell[col-id="name"] .ag-header-cell-filter-button').click();
  await page.locator('.ag-filter-body input').first().fill('steam');await page.locator('#file-status').getByText('1 of 4 items',{exact:true}).waitFor();
  await page.keyboard.press('Escape');await page.locator('#file-clear').click();await page.locator('#file-status').getByText('4 of 4 items',{exact:true}).waitFor();
  await page.getByRole('button',{name:'Actions for archive.zip',exact:true}).click();
  assert((await page.getByRole('menuitem',{name:'Download ZIP (compressed)',exact:true}).getAttribute('href')).includes('format=compressed'));
  assert((await page.getByRole('menuitem',{name:'Download ZIP (uncompressed)',exact:true}).getAttribute('href')).includes('format=stored'));
  await page.keyboard.press('Escape');
  await page.getByRole('button',{name:'Actions for steam-123.log',exact:true}).click();await page.getByRole('menuitem',{name:'Rename / move…',exact:true}).click();
  await page.getByLabel('Destination path',{exact:true}).fill('renamed.log');await page.getByRole('button',{name:'Move',exact:true}).click();
  await page.getByRole('button',{name:'Actions for renamed.log',exact:true}).waitFor();
  assert(mutations.at(-1).body.destination==='renamed.log');
  await page.getByRole('button',{name:'Actions for renamed.log',exact:true}).click();await page.getByRole('menuitem',{name:'Delete…',exact:true}).click();
  const before=mutations.length;await page.getByRole('button',{name:'Cancel',exact:true}).click();assert.equal(mutations.length,before);
  await page.getByRole('button',{name:'Actions for renamed.log',exact:true}).click();await page.getByRole('menuitem',{name:'Delete…',exact:true}).click();await page.getByRole('button',{name:'Delete',exact:true}).click();
  await page.locator('#file-status').getByText('3 of 3 items',{exact:true}).waitFor();assert.equal(mutations.at(-1).body.confirm,true);
  await newTab(page.locator('#file-list').getByRole('link',{name:'.local',exact:true}),{tab:'workstation',user:'station',path:'.local'},{modifiers:['Control']});
  await page.getByRole('link',{name:'.local',exact:true}).click();await page.locator('#file-status').getByText('1 of 1 items',{exact:true}).waitFor();
  await page.getByRole('button',{name:'Copy directory path',exact:true}).click();assert.equal(await page.evaluate(()=>window.copied.at(-1)),'/var/home/station/.local');
  await page.goBack();await page.locator('#file-status').getByText('3 of 3 items',{exact:true}).waitFor();
  const beforeRefresh=listingCalls;await page.locator('#file-refresh').click();await page.waitForFunction(()=>document.querySelector('#file-status').textContent==='3 of 3 items');assert(listingCalls>beforeRefresh);
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Files overflow at '+width);
  await page.getByRole('button',{name:'Actions for .local',exact:true}).click();
  assert(await page.getByRole('menuitem',{name:'Download ZIP',exact:true}).isVisible());
  const bounds=await page.locator('.file-menu').boundingBox();assert(bounds.x>=0&&bounds.x+bounds.width<=width+1);
  fs.mkdirSync('.build/evidence/files-grid',{recursive:true});await page.screenshot({path:'.build/evidence/files-grid/files-'+width+'.png',fullPage:true});
 }
 usageScenario=true;
 const orderedNames=()=>page.locator('#usage-list .ag-row').evaluateAll(rs=>rs.filter(r=>r.querySelector('[col-id=name]')).sort((a,b)=>Number(a.getAttribute('row-index'))-Number(b.getAttribute('row-index'))).map(r=>r.querySelector('[col-id=name]').textContent));
 for(const width of [1440,390]){
  await page.setViewportSize({width,height:950});await page.goto('http://files.test/files');
  await page.waitForFunction(()=>document.querySelector('#usage-list').textContent.includes('858.3 MiB'));
  assert.equal((await orderedNames())[0],'/var','Mounts sort by measured directory usage, not identical filesystem capacity');
  assert.match(await page.locator('#usage-list').textContent(),/≥ 15.3 MiB/);
  assert.match(await page.locator('#usage-list').textContent(),/Unavailable/);
  assert.match(await page.locator('#usage-list').getByRole('link',{name:'/var',exact:true}).getAttribute('title'),/Shares filesystem with/);
  const refreshBefore=sizes.length;await page.locator('#usage-refresh').click();
  await page.waitForFunction(()=>document.querySelector('#usage-list').textContent.includes('858.3 MiB'));
  assert.equal(sizes.slice(refreshBefore).filter(s=>s.includes('refresh=true')).length,3,'Refresh rescans each mount');
  const siblingBefore=sizes.length;
  await page.locator('#usage-list').getByRole('link',{name:'/var',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#usage-list .ag-row[row-index="0"] [col-id=name]')?.textContent==='folder-19');
  assert.match(await page.locator('#usage-total').textContent(),/Allocated in this folder: 858.3 MiB/);
  assert(sizes.some(s=>new URL(s).searchParams.get('children')==='true'),'Sibling sizes arrive in one parent response, including offscreen rows');
  assert(!sizes.slice(siblingBefore).some(s=>new URL(s).searchParams.get('path')==='folder-19'),'No individual scan is started for each sibling');
  if(width>600){const largest=page.locator('#usage-list .ag-row[row-index="0"]');assert.equal(await largest.locator('.file-usage-bar>span').evaluate(e=>e.style.width),'100%');}
  const before=sizes.length;await page.locator('#usage-refresh').click();
  await page.waitForFunction(()=>document.querySelector('#usage-list .ag-row[row-index="0"] [col-id=name]')?.textContent==='folder-19');
  assert(sizes.slice(before).some(s=>new URL(s).searchParams.get('path')===''&&s.includes('refresh=true')),'Folder Refresh starts a new parent scan');
  const name=page.locator('#usage-list .ag-row[row-index="0"] [col-id=name]');await name.focus();await name.press('ArrowRight');
  await page.waitForURL('**folder=folder-19');await page.locator('#usage-list').getByText('payload',{exact:true}).waitFor();
  await page.waitForFunction(()=>document.activeElement?.matches('#usage-list .ag-cell[col-id=name]'));
  await page.keyboard.press('ArrowLeft');await page.waitForURL('**folder=');
  await page.waitForFunction(()=>document.querySelector('#usage-list .ag-row[row-index="0"] [col-id=name]')?.textContent==='folder-19');
  // Sparse file usage stays zero; a failed measurement sorts below it rather than looking empty.
  await page.locator('#usage-filter').fill('sparse');
  await page.waitForFunction(()=>document.querySelector('#usage-list .ag-row [col-id=bytes]')?.textContent==='0 B');
  await page.locator('#usage-clear').click();
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Storage usage overflows at '+width);
  await page.screenshot({path:'.build/evidence/files-grid/storage-'+width+'.png',fullPage:true});
 }
 assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'FilesUi',result:'Passed',desktopAndMobile:true,agGrid:true,searchAndSort:true,copyPaths:true,menus:true,confirmedDeleteAndMove:true,freshListings:true,folderSizeRefresh:true,allocatedMountUsage:true,offscreenSizeSort:true,sparseFileUsage:true,keyboardDrilldown:true,partialAndFailedScans:true,middleClickNavigation:true,controlClickNavigation:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
