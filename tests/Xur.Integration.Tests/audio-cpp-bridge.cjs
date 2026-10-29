const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const calls=[];
const location=new URL('https://xur.example/inference/audio/');
const window={fetch:async(input,init)=>{calls.push({input,init});return new Response('{}');}};
vm.runInNewContext(fs.readFileSync('src/Xur.Control/wwwroot/audio-cpp.js','utf8'),{
 window,location,URL,Request,Headers,document:{querySelector:()=>({content:'csrf-token'})}
});
(async()=>{
 await window.fetch('v1/models/load',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
 assert.equal(calls.at(-1).init.headers.get('RequestVerificationToken'),'csrf-token');
 assert.equal(calls.at(-1).init.headers.get('Content-Type'),'application/json');
 const upload=new Request(location+'v1/ui/upload',{method:'POST',body:'audio'});
 await window.fetch(upload);assert.equal(calls.at(-1).init.headers.get('RequestVerificationToken'),'csrf-token');
 assert.equal(calls.at(-1).input,upload);
 for(const url of ['https://other.example/inference/audio/v1/models/load','/inference/another/v1/models/load','/api/settings']){
  await window.fetch(url,{method:'POST'});assert.equal(calls.at(-1).init.headers,undefined);
 }
 await window.fetch('health');assert.equal(calls.at(-1).init,undefined);
 console.log('audio.cpp bridge: JSON, uploads, read requests and token scoping passed');
})();
