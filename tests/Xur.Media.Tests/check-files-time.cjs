const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('fs'),path=require('path'),assert=require('assert/strict'),{execFileSync}=require('child_process');
(async()=>{
 const name=process.argv[2],out=process.argv[3];assert(/^[a-z0-9-]+$/.test(name));
 const guest=args=>{const r=JSON.parse(execFileSync('python3',['tests/Xur.Media.Tests/guest.py',name,...args],{encoding:'utf8'}));assert.equal(r.code,0,r.error);return r.output;};
 const token=execFileSync('python3',['-c',"import sys,pathlib;sys.path.insert(0,'tests/Xur.Media.Tests');from test_auth import api_session;print(api_session(pathlib.Path('.build/vms')/sys.argv[1]))",name],{encoding:'utf8'}).trim();
 const base='https://127.0.0.1:18443',browser=await chromium.launch({headless:true});let fixture;
 try{
  const context=await browser.newContext({acceptDownloads:true,ignoreHTTPSErrors:true});
  await context.addCookies([{name:'xur.session',value:token,url:base,httpOnly:true,secure:true,sameSite:'Strict'}]);
  const roots=await (await context.request.get(base+'/api/files/roots')).json();assert(roots.length,'VM needs a workstation account');const user=roots[0];
  fixture=user.home+'/.xur-download-test.log';
  const payload='Proton test log\nUnicode: café\n';
  guest(['runuser','-u',user.username,'--','python3','-c','import pathlib,sys;pathlib.Path(sys.argv[1]).write_text(sys.argv[2])',fixture,payload]);
  const page=await context.newPage();await page.goto(base+'/files?user='+encodeURIComponent(user.username));
  await page.getByText('.xur-download-test.log',{exact:true}).waitFor();
  const row=page.getByRole('listitem').filter({hasText:'.xur-download-test.log'});const pending=page.waitForEvent('download');await row.getByRole('link',{name:'Download'}).click();const download=await pending;
  assert.equal(fs.readFileSync(await download.path(),'utf8'),payload);assert.match(download.suggestedFilename(),/^\.?xur-download-test\.log$/);
  const denied=await context.request.get(base+'/api/files/download?user='+encodeURIComponent(user.username)+'&path=..%2F..%2Fetc%2Fshadow');assert.equal(denied.status(),400);
  const before=await (await context.request.get(base+'/api/ntp')).json();assert(Array.isArray(before.servers));
  try{
   await page.goto(base+'/settings');await page.getByLabel('Preferred NTP servers').fill('time.cloudflare.com');await page.getByRole('button',{name:'Save NTP settings',exact:true}).click();await page.getByText('NTP settings saved. Synchronization may take a few minutes.',{exact:true}).waitFor();
   assert.deepEqual((await (await context.request.get(base+'/api/ntp')).json()).servers,['time.cloudflare.com']);
   guest(['chronyd','-p','-f','/etc/chrony.conf']);
   guest(['systemctl','restart','xur-agent.service']);
   await page.waitForTimeout(1500);
   assert.deepEqual((await (await context.request.get(base+'/api/ntp')).json()).servers,['time.cloudflare.com']);
  }finally{
   await page.goto(base+'/settings');await page.getByLabel('Preferred NTP servers').fill(before.servers.join('\n'));await page.getByRole('checkbox',{name:'Enable automatic time synchronization'}).setChecked(before.enabled);await page.getByRole('button',{name:'Save NTP settings',exact:true}).click();await page.getByText('NTP settings saved. Synchronization may take a few minutes.',{exact:true}).waitFor();
  }
  for(const width of [1440,390]){await page.setViewportSize({width,height:900});assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)));await page.screenshot({path:path.join(out,'time-settings-'+width+'.png'),fullPage:true});}
  const result={result:'Passed',realUserDownload:true,traversalDenied:true,ntpSavedAndRestored:true,chronyConfigurationValid:true,persistedAcrossAgentRestart:true,settingsDesktopAndMobile:true};fs.writeFileSync(path.join(out,'files-time.json'),JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify(result));
 }finally{if(fixture)guest(['rm','-f',fixture]);await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
