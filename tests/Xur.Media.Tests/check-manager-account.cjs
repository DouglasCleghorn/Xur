const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');const crypto=require('node:crypto');const {execFileSync}=require('node:child_process');
(async()=>{
 const name=process.argv[2],base='http://127.0.0.1:18081',directory=`.build/vms/${name}`,cookiePath=directory+'/session.private.cookies';
 const raw=fs.readFileSync(cookiePath,'utf8');const jwt=raw.match(/Set-Cookie3: xur\.session=([^;]+)/)[1].replaceAll('"','');
 const credentialsPath=directory+'/account.private.json';
 const credentials=fs.existsSync(credentialsPath)?JSON.parse(fs.readFileSync(credentialsPath)): {username:'owner',password:crypto.randomBytes(24).toString('base64url')};
 fs.writeFileSync(credentialsPath,JSON.stringify(credentials),{mode:0o600});
 const browser=await chromium.launch({headless:true});
 try{
  const context=await browser.newContext();await context.addCookies([{name:'xur.session',value:jwt,url:base,httpOnly:true,sameSite:'Strict'}]);const page=await context.newPage();await page.goto(base+'/');
  if(page.url().includes('/setup-account')){
   if((await context.request.get(base+'/api/profiles')).status()!==403)throw Error('Setup session has management access');
   for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/updates/account-setup-${label}.png`,fullPage:true});if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Setup overflow');}
   await page.getByLabel('Username',{exact:true}).fill(credentials.username);await page.getByLabel('Password',{exact:true}).fill(credentials.password);await page.getByRole('button',{name:'Create account',exact:true}).click();await page.waitForURL(base+'/');
  }else if(page.url().includes('/login')){
   await page.getByLabel('Username',{exact:true}).fill(credentials.username);await page.getByLabel('Password',{exact:true}).fill(credentials.password);await page.getByRole('button',{name:'Sign in',exact:true}).click();await page.waitForURL(base+'/');
  }
  if((await context.request.get(base+'/api/profiles')).status()!==200)throw Error('Manager session unavailable');
  await page.goto(base+'/settings');await page.getByRole('button',{name:'Sign out',exact:true}).click();await page.waitForURL(base+'/login');
  if(await page.locator('#code').count())throw Error('Code remains on returning login');
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/updates/password-login-${label}.png`,fullPage:true});}
  await page.getByLabel('Username',{exact:true}).fill(credentials.username);await page.getByLabel('Password',{exact:true}).fill('wrong-password');await page.getByRole('button',{name:'Sign in',exact:true}).click();await page.getByRole('alert').waitFor();
  await page.getByLabel('Username',{exact:true}).fill(credentials.username);await page.getByLabel('Password',{exact:true}).fill(credentials.password);await page.getByRole('button',{name:'Sign in',exact:true}).click();await page.waitForURL(base+'/');
  const session=(await context.cookies()).find(c=>c.name==='xur.session').value;
  fs.writeFileSync(cookiePath,raw.replace(/(Set-Cookie3: xur\.session=)[^;]+/,`$1${session}`),{mode:0o600});
  const status=await (await context.request.get(base+'/api/application-updates')).json();
  const receipt={suite:'ManagerAccountBrowser',result:'Passed',bundle:status.current.id,tokenRedirectsToAccount:true,passwordSignIn:true,signOut:true,desktopAndPhone:true};fs.writeFileSync('.build/evidence/updates/manager-account.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1});
