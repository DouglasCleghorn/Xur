const {chromium}=require('../../.build/browser/node_modules/playwright');
const {execFileSync}=require('child_process'),fs=require('fs'),path=require('path'),assert=require('assert/strict');
(async()=>{
 const out=path.resolve('.build/fast/cancellation-ui');fs.mkdirSync(out,{recursive:true});
 execFileSync(process.env.XUR_DOTNET||path.join(process.env.HOME,'.local/share/xur-build/dotnet/dotnet'),['run','--project','tests/Xur.Unit.Tests','-c','Release','--','--cancellation-render',out],{stdio:'pipe'});
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();await page.route('http://cancel.test/**',route=>{
   const url=new URL(route.request().url());const file=url.pathname==='/setup.css'?'src/Xur.Control/wwwroot/setup.css':(url.pathname.startsWith('/fonts/')||url.pathname.startsWith('/icons/'))?'src/Xur.Control/wwwroot'+url.pathname:path.join(out,path.basename(url.pathname));
   if(!fs.existsSync(file))return route.fulfill({status:404,body:''});
   return route.fulfill({body:fs.readFileSync(file),contentType:url.pathname.endsWith('.css')?'text/css':url.pathname.endsWith('.ttf')?'font/ttf':url.pathname.endsWith('.svg')?'image/svg+xml':'text/html'});
  });
  for(const stage of ['Applying','Cancelling','Cancelled']){
   await page.goto('http://cancel.test/'+stage+'.html');
   assert.equal(await page.getByRole('button',{name:'Cancel change',exact:true}).count(),stage==='Applying'?1:0);
   if(stage==='Applying')assert.equal(await page.locator('[name=operationId]').inputValue(),'operation-fixture');
   if(stage==='Cancelling')assert(await page.getByText('Waiting for the current action to finish:',{exact:false}).isVisible());
   if(stage==='Cancelled')assert(await page.getByText('Remaining actions cancelled.',{exact:false}).isVisible());
   assert.equal(await page.getByRole('button',{name:'Create profile',exact:true}).isDisabled(),stage!=='Cancelled');
   await page.getByText('Completed changes (1)',{exact:true}).click();assert(await page.getByText('llm: Keep language model and active requests',{exact:true}).isVisible());
   for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
    await page.setViewportSize({width,height});assert(!(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1)));await page.screenshot({path:path.join(out,stage+'-'+label+'.png'),fullPage:true});
   }
  }
  console.log(JSON.stringify({suite:'CancellationUi',result:'Passed',actualRazor:true,operationBound:true,applyingCancellingCancelled:true,completedActionsVisible:true,desktopAndMobile:true}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
