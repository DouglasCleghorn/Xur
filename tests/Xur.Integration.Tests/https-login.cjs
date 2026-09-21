const {chromium}=require('../../.build/browser/node_modules/playwright');
const {spawn}=require('child_process');
const http=require('http'),fs=require('fs'),os=require('os'),path=require('path'),crypto=require('crypto');
const assert=require('assert/strict');
(async()=>{
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'xur-https-'));
 let process,browser,agent;const tls='https://127.0.0.1:19063',plain='http://127.0.0.1:18700';
 const local=(socket,url,headers={})=>new Promise((resolve,reject)=>{http.get({socketPath:path.join(root,socket),path:url,headers},r=>{let body='';r.on('data',x=>body+=x);r.on('end',()=>resolve({status:r.statusCode,body,headers:r.headers}));}).on('error',reject);});
 const start=async(request)=>{
  process=spawn(path.resolve('.build/context/publish/control/Xur.Control'),[],{env:{...global.process.env,XUR_MODE:'Installer',XUR_RUN:root,XUR_PORT:'18700',XUR_CONSOLE:'stdio'},stdio:['pipe','ignore','ignore'],detached:true});
  for(let n=0;n<100;n++){try{if((await request.get(tls+'/health')).ok())return;}catch{}await new Promise(r=>setTimeout(r,100));}
  throw Error('HTTPS host did not start');
 };
 const stop=async()=>{if(process&&process.exitCode===null){const ended=new Promise(r=>process.once('exit',r));global.process.kill(-process.pid,'SIGTERM');await ended;}process=null;};
 try{
  browser=await chromium.launch({headless:true});const context=await browser.newContext({ignoreHTTPSErrors:true});const request=context.request;
  const logBody='Synthetic workload log line without credentials.\n'.repeat(6000);
  agent=http.createServer((req,res)=>{if(req.url==='/logs'){res.setHeader('Content-Type','text/plain');res.end(logBody);}else{res.statusCode=404;res.end('{}');}});
  await new Promise(resolve=>agent.listen(path.join(root,'agent.sock'),resolve));
  await start(request);
  const redirect=await request.get(plain+'/login',{maxRedirects:0});assert.equal(redirect.status(),308);assert.equal(redirect.headers().location,tls+'/login');
  const rejected=await request.post(plain+'/api/auth/login',{data:{username:'owner',password:'not-a-real-password'},maxRedirects:0});assert.equal(rejected.status(),426);
  const serve=await local('serve.sock','/login');assert.equal(serve.status,200);assert(serve.headers['set-cookie'].some(x=>x.includes('secure')));
  assert.equal((await local('serve.sock','/local/login')).status,404);
  const proxyHeaders={Origin:'https://xur.example.ts.net','X-Forwarded-Host':'xur.example.ts.net','Sec-Fetch-Site':'same-origin'};
  assert.equal((await local('serve.sock','/health',proxyHeaders)).status,200,'Private Serve socket restores the original host');
  assert.equal((await local('serve.sock','/health',{...proxyHeaders,Origin:'https://evil.example'})).status,403);
  assert.equal((await local('serve.sock','/health',{...proxyHeaders,'X-Forwarded-Host':'xur.example.ts.net,evil.example'})).status,400);
  assert.equal((await request.get(tls+'/health',{headers:proxyHeaders})).status(),403,'Public listener must ignore forwarded host');
  const code=(await local('control.sock','/local/login')).body.match(/Access code: ([0-9A-Z-]+)/)[1];
  const manifest=await request.get(tls+'/manifest.webmanifest');assert(manifest.ok());const app=await manifest.json();assert.equal(app.display,'standalone');assert.equal(app.icons.length,2);
  for(const icon of app.icons)assert((await request.get(tls+icon.src)).ok());
  for(const iconPath of ['/icons/xur-icon.svg','/icons/xur-icon-32.png','/icons/xur-icon-180.png'])assert((await request.get(tls+iconPath)).ok(),'Brand assets must load before login');
  const qrPage=await context.newPage();await qrPage.goto(tls+'/login#code='+encodeURIComponent(code));assert.equal(await qrPage.locator('#code').inputValue(),code);assert.equal(new URL(qrPage.url()).hash,'');assert.equal(await qrPage.locator('link[rel=manifest]').count(),1);await qrPage.close();
  const phone=await browser.newContext({ignoreHTTPSErrors:true,viewport:{width:390,height:844},isMobile:true,hasTouch:true,userAgent:'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1'});
  const mobile=await phone.newPage();await mobile.goto(tls+'/login');await mobile.getByRole('button',{name:'How to add',exact:true}).click();await mobile.getByText('Tap Share → Add to Home Screen → Add.').waitFor();await mobile.getByRole('button',{name:'Dismiss home screen suggestion'}).click();await mobile.reload();assert.equal(await mobile.locator('.install-suggestion').count(),0);await phone.close();
  const exchange=await request.post(tls+'/api/bootstrap',{data:{token:code}});const session=(await exchange.json()).accessToken;
  const password=crypto.randomBytes(24).toString('base64url');
  assert((await request.post(tls+'/api/auth/setup',{headers:{Authorization:'Bearer '+session},data:{username:'owner@example.test',password}})).ok());
  const page=await context.newPage();await page.goto(tls+'/login#code=ABC-DEF');assert.equal(await page.locator('#code').count(),0);assert.equal(new URL(page.url()).hash,'');await page.locator('[name=username]').fill('owner@example.test');await page.locator('[name=password]').fill(password);
  // Submit a form rendered by the previous manager process, using its cookie.
  await stop();await start(request);
  const posted=page.waitForResponse(r=>r.url()===tls+'/auth/login');
  await page.getByRole('button',{name:'Sign in',exact:true}).click();
  const loginResponse=await posted;assert.equal(loginResponse.status(),302,'Login form must redirect after authentication');
  await page.waitForURL(tls+'/');
  const cookies=await context.cookies();assert(cookies.find(c=>c.name==='xur.session')?.secure);assert(cookies.find(c=>c.name==='xur.csrf.https')?.secure);
  const htmlHeaders={Accept:'text/html'};
  for(const action of ['/profiles/load','/profiles/cancel','/storage/trim','/settings/ntp']) {
   const error=await request.post(tls+action,{headers:htmlHeaders,form:{},maxRedirects:0});assert.equal(error.status(),400);assert(error.headers()['content-type'].includes('text/html'));assert((await error.text()).includes('This form expired'));
  }
  const assertRecovery=async(response,status,title,href,label)=>{
   assert.equal(response.status(),status);assert(response.headers()['content-type'].includes('text/html'));
   const html=await response.text();
   const content=await page.evaluate(html=>{
    const document=new DOMParser().parseFromString(html,'text/html');
    return {heading:document.querySelector('h1')?.textContent,links:[...document.querySelectorAll('.actions a')].map(a=>({href:a.getAttribute('href'),label:a.textContent}))};
   },html);
   assert.equal(content.heading,title);assert.deepEqual(content.links,[{href,label}]);
  };
  const missing=await request.get(tls+'/no-such-page',{headers:htmlHeaders});
  await assertRecovery(missing,404,'Page not found','/','Return home');
  const blockedForm=await request.post(tls+'/profiles/load',{headers:{...htmlHeaders,Origin:'https://evil.example'},form:{}});
  await assertRecovery(blockedForm,403,'Request blocked','/profiles','Return to Profiles');
  const csrf=await page.locator('meta[name=xur-csrf]').getAttribute('content');
  const headers={RequestVerificationToken:csrf,'Accept-Encoding':'gzip'};
  const first=await request.get(tls+'/api/status',{headers});assert.equal(first.status(),200);assert.equal(first.headers()['content-encoding'],'gzip');assert(first.headers().etag);assert((await first.json()).bootId);
  const same=await request.get(tls+'/api/status',{headers:{...headers,'If-None-Match':first.headers().etag}});assert.equal(same.status(),304);assert.equal((await same.body()).length,0);
  const plainPoll=await request.get(tls+'/api/status',{headers:{'Accept-Encoding':'gzip'}});assert.equal(plainPoll.headers()['content-encoding'],undefined);
  assert.equal((await request.get(tls+'/api/status',{headers:{...headers,RequestVerificationToken:'invalid'}})).status(),400);
  const logs=await request.get(tls+'/api/logs',{headers});assert.equal(logs.headers()['content-encoding'],'gzip');assert.equal(await logs.text(),logBody);
  const unchangedLogs=await request.get(tls+'/api/logs',{headers:{...headers,'If-None-Match':logs.headers().etag}});assert.equal(unchangedLogs.status(),304);
  assert.equal(fs.statSync(path.join(root,'response-spool')).mode&0o777,0o700);assert.deepEqual(fs.readdirSync(path.join(root,'response-spool')),[]);
  const cross=await request.post(tls+'/auth/logout',{headers:{...headers,Origin:'https://evil.example'},maxRedirects:0});assert.equal(cross.status(),403);
  assert.equal((await request.post(tls+'/api/auth/login',{headers:{Origin:'https://evil.example'},data:{username:'owner@example.test',password}})).status(),403);
  assert.equal((await request.get(tls+'/api/status',{headers:{...headers,'Sec-Fetch-Site':'same-site'}})).status(),403);
  assert.equal((await request.post(tls+'/auth/logout',{maxRedirects:0})).status(),400);
  const cancel=await request.post(tls+'/profiles/cancel',{headers,form:{operationId:'stale-operation'},maxRedirects:0});assert.equal(cancel.status(),302);assert(cancel.headers().location.startsWith('/profiles?error='));
  const css=await request.get(tls+'/setup.css',{headers:{'Accept-Encoding':'br'}});assert.equal(css.status(),200);assert.equal(css.headers()['content-encoding'],'br');assert(css.headers().etag);assert.notEqual(css.headers()['cache-control'],'no-store');
  const cssCached=await request.get(tls+'/setup.css',{headers:{'Accept-Encoding':'br','If-None-Match':css.headers().etag}});assert.equal(cssCached.status(),304);
  const keyResponse=await request.post(tls+'/api/api-keys',{headers,data:{name:'security test',scope:'diagnostics',days:1}});assert.equal(keyResponse.status(),201);assert.equal(keyResponse.headers()['content-encoding'],undefined);
  const issued=await keyResponse.json();const api=await request.get(tls+'/api/status',{headers:{Authorization:'Bearer '+issued.token}});assert.equal(api.status(),200);
  assert.equal((await request.get(tls+'/api/status',{headers:{Authorization:'Bearer '+issued.token,Origin:'https://evil.example'}})).status(),403);
  const cached=await page.evaluate(async()=>{const a=await window.xurFetch('/api/status');const b=await window.xurFetch('/api/status');return {first:await a.json(),next:await b.json(),status:b.status};});assert.equal(cached.status,200);assert.deepEqual(cached.first,cached.next);
  let samples=0;await page.route('**/api/network-usage*',async route=>{
   const since=new URL(route.request().url()).searchParams.get('since');samples++;const at=samples===1?'2026-09-19T00:00:00Z':'2026-09-19T00:00:05Z';
   if(samples===2)assert.equal(since,'2026-09-19T00:00:00Z');
   await route.fulfill({status:200,contentType:'application/json',headers:since?{'X-Xur-History-Delta':'true'}:{},body:JSON.stringify({capturedAt:at,adapters:[{name:'eth0',state:'up',reading:{at},history:[{at,receivedBytes:samples}]}]})});
  });
  const history=await page.evaluate(async()=>{await window.xurTelemetry('/api/network-usage?minutes=15');await window.xurTelemetry('/api/network-usage?minutes=15');return (await window.xurTelemetry('/api/network-usage?minutes=15')).adapters[0].history;});assert.equal(history.length,2);assert.equal(history[1].receivedBytes,3);
  const bad=await request.post(tls+'/auth/login',{form:{username:'owner@example.test',password},maxRedirects:0});assert.equal(bad.status(),302);assert.equal(bad.headers().location,'/login?error=refresh');
  assert.equal((await request.get(tls+'/local/login')).status(),404);
  const oldCookie=cookies.find(c=>c.name==='xur.session').value;await stop();await start(request);
  const setup=await request.get(tls+'/setup-account',{maxRedirects:0});assert.equal(setup.status(),302);assert.equal(setup.headers().location,'/');
  assert.equal((await context.cookies()).find(c=>c.name==='xur.session').value,oldCookie);
  console.log(JSON.stringify({suite:'HttpsLogin',result:'Passed',httpRedirect:true,plaintextMutationsDenied:true,oldFormSurvivesRestart:true,secureCookies:true,staleFormRecovery:true,privateServeSocket:true,loginSurvivesRestart:true,cancelRedirect:true,validatedCompression:true,conditionalPolling:true,privateLargeResponseSpool:true,precompressedStaticAssets:true,crossOriginDenied:true}));
 }finally{await stop();agent?.closeAllConnections();if(agent)await new Promise(resolve=>agent.close(resolve));await browser?.close();fs.rmSync(root,{recursive:true,force:true});}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
