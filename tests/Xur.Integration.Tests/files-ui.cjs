const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
 const page=await browser.newPage();const errors=[],mutations=[],sizes=[];let listingCalls=0;
 page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{window.copied=[];Object.defineProperty(navigator,'clipboard',{value:{writeText:async value=>window.copied.push(value)}});});
 const initial=[{name:'.local',kind:'folder',modified:1700000000},{name:'steam-123.log',kind:'file',bytes:128,modified:1700000000},{name:'archive.zip',kind:'file',bytes:256,modified:1700000000},{name:'<script>alert(1)</script>',kind:'link',modified:1700000000}];let items=structuredClone(initial);
 await page.route('http://files.test/**',async r=>{
  const u=new URL(r.request().url());
  if(u.pathname==='/api/storage/mounts')return r.fulfill({json:[{id:'ssd',path:'/var',source:'/dev/nvme0n1p2',type:'ext4',bytes:1000000000,used:400000000,available:600000000,readOnly:false}]});
  if(u.pathname==='/api/files/roots')return r.fulfill({json:[{username:'station',name:'Gaming workstation',home:'/var/home/station'}]});
  if(u.pathname.endsWith('/size')){sizes.push(u.href);return r.fulfill({json:{bytes:290000000,partial:false,scanning:false,capturedAt:'2026-09-20T18:00:00Z'}});}
  if(u.pathname==='/api/storage/files/list')return r.fulfill({json:{ok:true,entries:[{name:'models',kind:'folder',bytes:null,modified:1700000000},{name:'<script>bad</script>',kind:'link',bytes:null,modified:1700000000}],truncated:false}});
  if(u.pathname==='/api/files/list'){listingCalls++;const data=u.searchParams.get('path')?[{name:'steam-123.log',kind:'file',bytes:128,modified:1700000000}]:structuredClone(items);return r.fulfill({json:{ok:true,entries:data,truncated:false}});}
  if(r.request().method()==='POST'){
   const body=r.request().postDataJSON();mutations.push({path:u.pathname,target:u.searchParams.get('path'),body});
   if(u.pathname.endsWith('/move')){items.find(i=>i.name===u.searchParams.get('path')).name=body.destination;}
   if(u.pathname.endsWith('/delete'))items=items.filter(i=>i.name!==u.searchParams.get('path'));
   return r.fulfill({json:{ok:true}});
  }
  const asset=/\.(css|js|ttf|svg)$/.test(u.pathname);return r.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+u.pathname:'.build/fast/control-panel/files.html'),contentType:u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.ttf')?'font/ttf':u.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
 });
 for(const width of [1440,390]){
  items=structuredClone(initial);await page.setViewportSize({width,height:950});await page.goto('http://files.test/files');
  await page.locator('#usage-list .ag-root').waitFor();assert(await page.locator('#file-explorer').isHidden());
  await page.locator('#usage-list').getByRole('button',{name:'/var',exact:true}).click();
  await page.locator('#usage-list').getByRole('button',{name:'models',exact:true}).waitFor();
  assert.equal(await page.locator('#usage-list script').count(),0);
  await page.getByRole('button',{name:'Copy directory path',exact:true}).click();assert.equal(await page.evaluate(()=>window.copied.at(-1)),'/var');
  await page.locator('#usage-list').getByRole('button',{name:'Actions for models',exact:true}).click();
  assert((await page.getByRole('menuitem',{name:'Download ZIP',exact:true}).getAttribute('href')).includes('/api/storage/files/download'));
  await page.getByRole('menuitem',{name:'Refresh folder size',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#usage-list').textContent.includes('276.6 MiB'));
  assert(sizes.some(u=>u.includes('refresh=true')));
  await page.locator('#usage-list').getByRole('button',{name:'models',exact:true}).click();await page.waitForURL('**folder=models');await page.goBack();await page.waitForURL('**folder=');
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
  await page.getByRole('button',{name:'.local',exact:true}).click();await page.locator('#file-status').getByText('1 of 1 items',{exact:true}).waitFor();
  await page.getByRole('button',{name:'Copy directory path',exact:true}).click();assert.equal(await page.evaluate(()=>window.copied.at(-1)),'/var/home/station/.local');
  await page.goBack();await page.locator('#file-status').getByText('3 of 3 items',{exact:true}).waitFor();
  const beforeRefresh=listingCalls;await page.locator('#file-refresh').click();await page.waitForFunction(()=>document.querySelector('#file-status').textContent==='3 of 3 items');assert(listingCalls>beforeRefresh);
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Files overflow at '+width);
  await page.getByRole('button',{name:'Actions for .local',exact:true}).click();
  assert(await page.getByRole('menuitem',{name:'Download ZIP',exact:true}).isVisible());
  const bounds=await page.locator('.file-menu').boundingBox();assert(bounds.x>=0&&bounds.x+bounds.width<=width+1);
  fs.mkdirSync('.build/evidence/files-grid',{recursive:true});await page.screenshot({path:'.build/evidence/files-grid/files-'+width+'.png',fullPage:true});
 }
 assert.deepEqual(errors,[]);console.log(JSON.stringify({suite:'FilesUi',result:'Passed',desktopAndMobile:true,agGrid:true,searchAndSort:true,copyPaths:true,menus:true,confirmedDeleteAndMove:true,freshListings:true,folderSizeRefresh:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
