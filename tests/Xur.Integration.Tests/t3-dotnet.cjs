// Exercise the updater with fake downloads/processes and real isolated files.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),crypto=require('node:crypto');
const root=path.resolve('.build/evidence/t3-dotnet');fs.mkdirSync(root,{recursive:true});
const home=fs.mkdtempSync(path.join(root,'home-'));
const source=fs.readFileSync('src/Xur.Domain/t3-dotnet.cjs','utf8').replace('__XUR_MCP_SOURCES__',JSON.stringify({'Program.cs':'fixture','Xur.Mcp.csproj':'fixture','LICENSE':'MIT fixture','licensing.md':'notices fixture'}));
let mismatch=false,publishFails=false;
const content=Buffer.from('SDK fixture'),hash=crypto.createHash('sha512').update(content).digest('hex');
const sdkVersion='10.0.403';
const channels={'releases-index':[
 {'channel-version':'11.0','latest-sdk':'11.0.100-preview.1','support-phase':'go-live'},
 {'channel-version':'9.0','latest-sdk':'9.0.320','support-phase':'maintenance'},
 {'channel-version':'10.0','latest-sdk':sdkVersion,'support-phase':'active','releases.json':'https://fixture/releases'}]};
const fetch=async url=>({ok:true,json:async()=>url.endsWith('releases-index.json')?channels:{releases:[{sdks:[{version:channels['releases-index'][2]['latest-sdk'],files:[{rid:'linux-x64',url:'https://fixture/sdk.tar.gz',hash:mismatch?'0'.repeat(128):hash}]}]}]}});
function run(command,args){
 if(command==='curl'){fs.writeFileSync(args.at(-1),content);return;}
 if(command==='tar'){fs.writeFileSync(path.join(args.at(-1),'dotnet'),'fixture');return;}
 if(args[0]==='--version')return sdkVersion+'\n';
 if(args[0]==='publish'){
  if(publishFails)throw Error('fixture publish failure');
  const out=args.at(-1),src=path.dirname(args[1]);fs.mkdirSync(out);fs.writeFileSync(path.join(out,'Xur.Mcp.dll'),'fixture');
  fs.mkdirSync(path.join(src,'.build/obj'),{recursive:true});
  fs.writeFileSync(path.join(src,'.build/obj/project.assets.json'),JSON.stringify({packageFolders:{[home]:{}},libraries:{}}));return;
 }
 if(args.at(-1)==='--verify')return;
 throw Error('Unexpected process: '+command);
}
const mod={exports:{}};
const context={require:name=>name==='node:child_process'?{execFileSync:run}:require(name),module:mod,process:{env:{HOME:home},arch:'x64'},fetch,AbortSignal,console};
vm.createContext(context);vm.runInContext(source,context);
(async()=>{
 try{
  assert.equal(mod.exports.latest(channels)['latest-sdk'],sdkVersion);
  await vm.runInContext('update()',context);
  const current=path.join(home,'.local/share/xur-mcp/current');
  const selected=fs.readlinkSync(current);
  assert.ok(fs.existsSync(path.join(home,'.local/bin/dotnet')));
  assert.ok(fs.readFileSync(path.join(selected,'run'),'utf8').includes('/versions/'+sdkVersion+'/dotnet'));
  await vm.runInContext('update()',context);assert.equal(fs.readlinkSync(current),selected,'identical updates reuse a generation');
  publishFails=true;
  await assert.rejects(vm.runInContext('update()',context),/fixture publish failure/);
  assert.equal(fs.readlinkSync(current),selected,'failed builds preserve the selected pair');
  publishFails=false;mismatch=true;channels['releases-index'][2]['latest-sdk']='10.0.404';
  // A bad published checksum must fail before extraction/selection.
  await assert.rejects(vm.runInContext('update()',context),/checksum mismatch/);
  assert.equal(fs.readlinkSync(current),selected);
  console.log('Stable SDK selection, persistent runtime, verified publication, repeat updates and failure recovery passed.');
 }finally{fs.rmSync(home,{recursive:true,force:true});}
})().catch(error=>{console.error(error);process.exitCode=1;});
