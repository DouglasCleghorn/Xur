// Exercise the shipped submit handler without creating Linux users.
const vm=require('node:vm'),fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const button={disabled:false},status={textContent:''},options=[];
 const fields={__RequestVerificationToken:{value:'fixture-token'},namedItem:key=>({value:key==='name'?'Alex':''})};
 let reset=0,requests=0,reply;
 const form={elements:fields,querySelector:s=>s==='button'?button:status,reset:()=>reset++};
 const document={querySelector:s=>s==='#station-display-dialog'?null:{add:o=>options.push(o)},querySelectorAll:s=>s==='.station-add-user'?[form]:[]};
 const fetch=async(url,request)=>{requests++;assert.equal(url,'/api/station-users');assert.deepEqual(JSON.parse(request.body),{name:'Alex'});return reply;};
 vm.runInNewContext(fs.readFileSync('src/Xur.Control/wwwroot/workstations.js','utf8'),{document,window:{fetch},Option:function(name,value,defaultSelected,selected){Object.assign(this,{name,value,selected});}});
 reply={ok:false,status:409,json:async()=>({error:'That user already exists.'})};
 await form.onsubmit({preventDefault(){}});assert.equal(status.textContent,'That user already exists.');assert.equal(reset,0);assert.equal(button.disabled,false);
 reply={ok:false,status:500,json:async()=>{throw Error('Invalid JSON');}};
 await form.onsubmit({preventDefault(){}});assert(status.textContent.includes('HTTP 500'));assert.equal(button.disabled,false);
 reply={ok:true,json:async()=>({name:'Alex',username:'xuruser-test'})};
 await form.onsubmit({preventDefault(){}});assert.equal(options[0].value,'xuruser-test');assert.equal(options[0].selected,true);assert.equal(reset,1);assert.equal(button.disabled,false);
 button.disabled=true;await form.onsubmit({preventDefault(){}});assert.equal(requests,3,'An in-flight submission must not create a duplicate user');
 console.log('Station user submit: validation, non-JSON errors, retry, selection and duplicate-click checks passed');
})().catch(e=>{console.error(e);process.exitCode=1;});
