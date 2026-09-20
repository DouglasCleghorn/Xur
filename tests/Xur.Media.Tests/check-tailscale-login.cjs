const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const vm=process.argv[2],base='http://127.0.0.1:18081';
 const raw=fs.readFileSync(`.build/vms/${vm}/session.private.cookies`,'utf8');
 const session=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const browser=await chromium.launch({headless:true});
 try {
  const context=await browser.newContext();
  await context.addCookies([{name:'xur.session',value:session,url:base,httpOnly:true,sameSite:'Strict'}]);
  const page=await context.newPage();await page.goto(base+'/tailscale');
  if(await page.getByText('Show QR on the machine').count())throw Error('Browser still offers console QR action');
  await page.getByRole('button',{name:'Sign in to Tailscale',exact:true}).click();
  const link=page.getByRole('link',{name:'Authorize this machine',exact:true});
  await link.waitFor({timeout:60000});
  const url=await link.getAttribute('href');
  if(!url.startsWith('https://login.tailscale.com/a/'))throw Error('Real client authorization link missing');
  if((await (await context.request.get(base+'/api/logs')).text()).includes(url))throw Error('Claim URL retained in installation logs');
  console.log(JSON.stringify({suite:'TailscaleBrowserLogin',realClientAuthorizationLink:true,noConsoleQrAction:true,claimExcludedFromLogs:true}));
 } finally {await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1});
