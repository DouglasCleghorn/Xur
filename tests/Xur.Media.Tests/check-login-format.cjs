const {chromium}=require('../../.build/browser/node_modules/playwright');
const fs=require('node:fs');
(async()=>{
 const name=process.argv[2],base=process.argv[3]||'http://127.0.0.1:18081';
 const browser=await chromium.launch({headless:true});
 try{
  const page=await browser.newPage();await page.goto(base+'/login');
  const code=page.locator('#code');
  await code.pressSequentially('abc');if(await code.inputValue()!=='ABC-')throw Error('Hyphen not inserted after third character');
  await code.pressSequentially('def');if(await code.inputValue()!=='ABC-DEF')throw Error('Typing six characters did not format 3-3');
  await code.fill('abcdef');if(await code.inputValue()!=='ABC-DEF')throw Error('Compact paste failed');
  await code.fill('abc-def');if(await code.inputValue()!=='ABC-DEF')throw Error('Formatted paste failed');
  await code.evaluate(e=>e.setSelectionRange(4,4));await code.press('Backspace');
  if(await code.inputValue()!=='ABD-EF')throw Error('Backspace got trapped at the hyphen');
  await code.fill('');await code.pressSequentially('abc');await code.press('Backspace');
  if(await code.inputValue()!=='AB')throw Error('Cannot delete across automatically added hyphen');
  await code.fill('abcdef');await code.evaluate(e=>e.setSelectionRange(1,2));await code.pressSequentially('z');
  if(await code.inputValue()!=='AZC-DEF')throw Error('Editing moved or lost characters');
  if((await page.locator('body').innerText()).includes('Expires after 30 minutes'))throw Error('Removed note returned');
  // Never capture a real credential.
  await code.fill('');
  for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844]]){
   await page.setViewportSize({width,height});await page.screenshot({path:`.build/evidence/ui/login-${label}.png`,fullPage:true});
  }
  const receipt={suite:'LoginFormatting',result:'Passed',typing:true,paste:true,backspace:true,caretEditing:true,media:JSON.parse(fs.readFileSync(`.build/vms/${name}/vm-manifest.json`))};
  fs.writeFileSync('.build/evidence/login-format.json',JSON.stringify(receipt,null,2)+'\n');console.log(JSON.stringify(receipt));
 }finally{await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1});
