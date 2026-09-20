const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
 const page=await browser.newPage();
 await page.route('http://files.test/**',r=>{
  const u=new URL(r.request().url());
  if(u.pathname==='/api/storage/mounts')return r.fulfill({json:[{id:'ssd',path:'/var',source:'/dev/nvme0n1p2',type:'ext4',bytes:1000000000,used:400000000,available:600000000,readOnly:false}]});
  if(u.pathname==='/api/storage/explore')return r.fulfill({json:{scanning:false,data:{bytes:300000000,visited:100,partial:false,errors:0,entries:[{name:'models',kind:'folder',bytes:290000000,partial:false},{name:'<script>bad</script>',kind:'link',bytes:0,partial:false}]}}});
  if(u.pathname==='/api/files/roots')return r.fulfill({json:[{username:'station',name:'Gaming workstation'}]});
  if(u.pathname==='/api/files/list')return r.fulfill({json:{ok:true,entries:u.searchParams.get('path')?[{name:'steam-123.log',kind:'file',bytes:128,modified:1700000000}]:[{name:'.local',kind:'folder',modified:1700000000},{name:'steam-123.log',kind:'file',bytes:128,modified:1700000000},{name:'<script>alert(1)</script>',kind:'link',modified:1700000000}],truncated:false}});
  const asset=/\.(css|js|ttf|svg)$/.test(u.pathname);return r.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+u.pathname:'.build/fast/control-panel/files.html'),contentType:u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.ttf')?'font/ttf':u.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
 });
 for(const width of [1440,390]){
  await page.setViewportSize({width,height:900});await page.goto('http://files.test/files');
  await page.getByRole('tab',{name:'Storage files',exact:true}).waitFor();
  assert(await page.locator('#file-explorer').isHidden());
  await page.locator('#usage-list').getByRole('button',{name:'/var',exact:true}).click();
  await page.locator('#usage-list').getByRole('button',{name:'models/',exact:true}).waitFor();
  assert.equal(await page.locator('#usage-list script').count(),0);

  await page.locator('#usage-list').getByRole('button',{name:'models/',exact:true}).click();
  await page.waitForURL('**folder=models');
  await page.goBack();await page.waitForURL('**folder=');
  await page.getByRole('tab',{name:'Workstation files',exact:true}).click();
  assert(await page.locator('#usage-explorer').isHidden());
  await page.getByText('3 items',{exact:true}).waitFor();
  assert.equal(await page.locator('#file-list script').count(),0);
  assert((await page.getByRole('link',{name:'Download',exact:true}).getAttribute('href')).includes('path=steam-123.log'));
  await page.getByRole('button',{name:'.local',exact:true}).click();await page.getByText('1 items',{exact:true}).waitFor();
  assert((await page.getByRole('link',{name:'Download',exact:true}).getAttribute('href')).includes('path=.local%2Fsteam-123.log'));
  await page.goBack();await page.getByText('3 items',{exact:true}).waitFor();
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Files overflow at '+width);
  await page.screenshot({path:'.build/fast/control-panel/files-'+width+'.png',fullPage:true});
 }
 console.log(JSON.stringify({suite:'FilesUi',result:'Passed',desktopAndMobile:true,navigationAndDownload:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
