const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),assert=require('assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true});
 try{
 const page=await browser.newPage();let mutations=0;
 await page.route('https://settings.test/**',r=>{
  const u=new URL(r.request().url());if(r.request().method()==='POST'){mutations++;return r.fulfill({body:'accepted'});}
  if(u.pathname.startsWith('/api/'))return r.fulfill({status:503,body:'unavailable'});
  const asset=/\.(css|js|ttf|svg)$/.test(u.pathname);return r.fulfill({body:fs.readFileSync(asset?'src/Xur.Control/wwwroot'+u.pathname:'.build/fast/control-panel'+u.pathname+'.html'),contentType:u.pathname.endsWith('.js')?'application/javascript':u.pathname.endsWith('.css')?'text/css':u.pathname.endsWith('.ttf')?'font/ttf':u.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
 });
 for(const width of [1440,390]){
  await page.setViewportSize({width,height:900});await page.goto('https://settings.test/settings');assert.equal(await page.locator('main>section').last().getAttribute('id'),'update-channel');assert.equal(await page.getByRole('heading',{name:'Web access',exact:true}).count(),1);assert.equal(await page.getByRole('heading',{name:'HTTPS',exact:true}).count(),0);
  assert.equal(await page.locator('#release-channel').inputValue(),'local');assert.equal(await page.locator('#local-update-server').inputValue(),'http://192.0.2.10:8088');
  const key=await page.locator('#local-update-key').inputValue();assert(key.includes('BEGIN PUBLIC KEY'));
  await page.locator('#release-channel').selectOption('stable');assert(await page.locator('#local-update-fields').isHidden());assert(await page.locator('#local-update-key').isDisabled());
  await page.locator('#release-channel').selectOption('local');assert(await page.locator('#local-update-fields').isVisible());assert.equal(await page.locator('#local-update-key').inputValue(),key);assert(await page.locator('#local-update-key').evaluate(e=>e.required));
  assert.equal(await page.locator('#hf-token').inputValue(),'');assert.equal(await page.locator('#hf-token').getAttribute('type'),'password');
  assert(await page.locator('#timezone-automatic').isChecked());assert(await page.locator('#timezone').isDisabled());
  assert(await page.locator('#timezone-refresh').isEnabled());
  await page.locator('#timezone-automatic').uncheck();assert(await page.locator('#timezone').isEnabled());assert(await page.locator('#timezone-refresh').isDisabled());
  assert.equal(mutations,0);assert.equal(await page.locator('#ntp-servers').inputValue(),'time.cloudflare.com');
  assert.equal(await page.getByRole('link',{name:'Download config backup'}).getAttribute('href'),'/settings/backup');
  await page.getByText('Contains passwords and private keys. Store securely.',{exact:true}).waitFor();
  assert.equal(await page.getByRole('link',{name:'Download config backup'}).getAttribute('download'),null,'Error redirects must render the Settings page');
  assert(!(await page.locator('main').innerText()).includes('never displayed or included in config backups'));
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Settings overflow at '+width);
  await page.screenshot({path:'.build/fast/control-panel/settings-'+width+'.png',fullPage:true});
  await page.goto('https://settings.test/storage');assert.equal(await page.getByRole('button',{name:'Trim SSD',exact:true}).count(),1);
  assert.equal(await page.locator('form[action="/storage/trim"] input[name=id]').inputValue(),'ssd');
  await page.getByText('Read only',{exact:true}).waitFor();assert.equal(mutations,0);
  assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)),'Storage overflow at '+width);
  await page.screenshot({path:'.build/fast/control-panel/storage-'+width+'.png',fullPage:true});
 }
 console.log(JSON.stringify({suite:'SettingsStorageUi',result:'Passed',automaticAndManual:true,backupDownload:true,ssdTrimEligibility:true,desktopAndMobile:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
