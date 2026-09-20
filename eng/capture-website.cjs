// Actual Razor UI with synthetic fixtures only. Never connect this script to a live host.
const {chromium}=require('../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const fixtures=path.resolve('.build/website-fixtures');
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--website-render',fixtures],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1200,height:800},deviceScaleFactor:1});
  await page.route('**/*',r=>{
   const u=new URL(r.request().url());
   if(u.origin!=='https://screenshots.test'||r.request().method()!=='GET')return r.abort();
   if(u.pathname==='/api/model-lab/targets')return r.fulfill({json:[{workload:{id:'example-model',name:'Local assistant',gpus:['GPU 3','GPU 4'],recipe:{name:'Example language model',engine:'vLLM'}},instance:{instanceId:'example'}}]});
   if(u.pathname==='/api/benchmarks'||u.pathname.startsWith('/api/model-lab/'))return r.fulfill({json:[]});
   if(u.pathname.startsWith('/api/'))return r.fulfill({status:503,json:{error:'Offline screenshot fixture'}});
   const asset=/\.(css|js|ttf|svg)$/.test(u.pathname);
   const file=asset?path.join('src/Xur.Control/wwwroot',u.pathname):path.join(fixtures,u.pathname.slice(1)+'.html');
   if(!fs.existsSync(file))return r.fulfill({status:404,body:''});
   let body=fs.readFileSync(file);
   if(!asset)body=body.toString().replace(/doug/gi,'example-user').replace(/(<form\b[^>]*>)/g,'$1<input type="hidden" name="__RequestVerificationToken" value="fixture-only">');
   const types={'.css':'text/css','.js':'text/javascript','.ttf':'font/ttf','.svg':'image/svg+xml'};
   return r.fulfill({body,contentType:asset?types[path.extname(file)]:'text/html'});
  });
  async function capture(route,name,prepare,selector){
   await page.goto('https://screenshots.test/'+route);await page.evaluate(()=>document.fonts.ready);
   if(prepare)await prepare();
   await page.evaluate(() => {document.activeElement?.blur();document.querySelectorAll('nav a[aria-current]').forEach(a=>{a.removeAttribute('aria-current');a.classList.remove('active');});const route=location.pathname.replace('/home','/').replace('/profile-edit','/profiles');document.querySelectorAll('nav a').forEach(a=>{if(a.getAttribute('href')===route){a.setAttribute('aria-current','page');a.classList.add('active');}});});
   const target=selector?page.locator(selector):page;
   const text=await (selector?page.locator(selector):page.locator('body')).innerText();
   assert(!/doug|192\.168\.|100\.\d+\.\d+\.\d+|drum-goblin|xuruser|BEGIN PUBLIC KEY/i.test(text),'Private data in '+name);
   await target.screenshot({path:'docs/assets/'+name+'.jpg',type:'jpeg',quality:85});
  }
  await capture('home','control-panel');
  await capture('workstations-populated','workstations');
  await capture('profile-edit','profile-editor');
  await capture('model-lab','model-lab',async()=>{await page.getByRole('button',{name:'Run benchmark',exact:true}).waitFor();await page.locator('#bench-prompt').fill('');});
  await capture('settings','update-channel',async()=>{await page.locator('#release-channel').selectOption('nightly');},'#update-channel');
  console.log('Captured five public documentation screenshots from synthetic Razor fixtures. Review images and docs/screenshots.md before publishing.');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
