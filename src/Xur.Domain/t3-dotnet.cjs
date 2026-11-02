// Node is the container's bootstrap runtime. MCP itself is implemented in C#.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const {execFileSync}=require('node:child_process');
const sources=__XUR_MCP_SOURCES__;
const home=process.env.HOME,root=path.join(home,'.local/share/xur-mcp');
const sdkRoot=path.join(home,'.local/share/dotnet');
const env={...process.env,DOTNET_CLI_TELEMETRY_OPTOUT:'1',DOTNET_NOLOGO:'1'};
function run(command,args,options={}){return execFileSync(command,args,{env,stdio:['ignore',2,2],timeout:600000,...options});}
function atomic(file,content,mode=0o600){fs.mkdirSync(path.dirname(file),{recursive:true,mode:0o700});const tmp=file+'.tmp';fs.writeFileSync(tmp,content,{mode});fs.renameSync(tmp,file);}
function link(target,file){const tmp=file+'.tmp';try{fs.unlinkSync(tmp);}catch(e){if(e.code!=='ENOENT')throw e;}fs.symlinkSync(target,tmp);fs.renameSync(tmp,file);}
async function json(url){const result=await fetch(url,{signal:AbortSignal.timeout(30000)});if(!result.ok)throw Error('Metadata download failed: '+result.status);return result.json();}
function stable(value){return typeof value==='string'&&/^\d+\.\d+\.\d+$/.test(value);}
function latest(index){return index['releases-index'].filter(x=>['active','maintenance'].includes(x['support-phase'])&&stable(x['latest-sdk']))
 .sort((a,b)=>b['channel-version'].localeCompare(a['channel-version'],undefined,{numeric:true}))[0];}
async function update(){
 fs.mkdirSync(root,{recursive:true,mode:0o700});fs.mkdirSync(sdkRoot,{recursive:true,mode:0o700});
 const channel=latest(await json('https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json'));
 if(!channel)throw Error('No supported stable .NET SDK found.');
 const version=channel['latest-sdk'],rid={x64:'linux-x64',arm64:'linux-arm64'}[process.arch];
 if(!rid)throw Error('Unsupported .NET architecture: '+process.arch);
 const releases=await json(channel['releases.json']);
 const sdk=releases.releases.flatMap(r=>r.sdks??[r.sdk]).find(s=>s?.version===version);
 const asset=sdk?.files.find(f=>f.rid===rid&&f.url.endsWith('.tar.gz'));
 if(!asset||!/^https:\/\//.test(asset.url)||!(/^[a-f0-9]{128}$/i.test(asset.hash)))throw Error('Missing SDK asset or SHA-512 checksum.');
 const sdkDir=path.join(sdkRoot,'versions',version);
 if(!fs.existsSync(path.join(sdkDir,'dotnet'))){
  const stage=fs.mkdtempSync(path.join(sdkRoot,'.stage-'));
  try{
   atomic(path.join(stage,'global.json'),JSON.stringify({sdk:{version,rollForward:'disable'}}));
   const archive=path.join(stage,'sdk.tar.gz');
   run('curl',['--fail','--silent','--show-error','--location','--proto','=https','--retry','2','--connect-timeout','10','--max-time','600',asset.url,'-o',archive]);
   const hash=crypto.createHash('sha512');for await(const chunk of fs.createReadStream(archive))hash.update(chunk);
   if(hash.digest('hex')!==asset.hash.toLowerCase())throw Error('SDK checksum mismatch; saved runtime retained.');
   const unpack=path.join(stage,'sdk');fs.mkdirSync(unpack);run('tar',['-xzf',archive,'-C',unpack]);
   const found=run(path.join(unpack,'dotnet'),['--version'],{cwd:stage,encoding:'utf8',stdio:['ignore','pipe',2]}).trim();
   if(found!==version)throw Error('SDK verification failed.');
   fs.mkdirSync(path.dirname(sdkDir),{recursive:true});fs.renameSync(unpack,sdkDir);
  }finally{fs.rmSync(stage,{recursive:true,force:true});}
 }
 const stage=fs.mkdtempSync(path.join(root,'.stage-'));
 try{
  atomic(path.join(stage,'global.json'),JSON.stringify({sdk:{version,rollForward:'disable'}}));
  const source=path.join(stage,'source');fs.mkdirSync(source);
  for(const [name,text]of Object.entries(sources))atomic(path.join(source,name),text);
  // Build away from project global.json files. Floating stable packages are
  // resolved on every refresh; no saved lock prevents upstream updates.
  const dotnet=path.join(sdkDir,'dotnet'),output=path.join(stage,'app');
  run(dotnet,['publish',path.join(source,'Xur.Mcp.csproj'),'-c','Release','--self-contained','false','-p:TargetFramework=net'+version.split('.')[0]+'.0','-o',output],{cwd:stage});
  const assets=JSON.parse(fs.readFileSync(path.join(source,'.build/obj/project.assets.json'),'utf8'));
  const packages=Object.keys(assets.packageFolders)[0];
  // Preserve NuGet distributions, including their original notices and metadata.
  const notices=path.join(output,'licenses');fs.mkdirSync(notices);
  for(const entry of Object.values(assets.libraries).filter(x=>x.type==='package')){
   const folder=path.join(packages,entry.path);
   for(const name of fs.readdirSync(folder).filter(n=>n.endsWith('.nupkg')))
    fs.copyFileSync(path.join(folder,name),path.join(notices,name));
  }
  for(const name of ['LICENSE','licensing.md'])fs.copyFileSync(path.join(source,name),path.join(output,name));
  atomic(path.join(stage,'run'),'#!/bin/sh\nexec "'+dotnet+'" "$(dirname "$(readlink -f "$0")")/app/Xur.Mcp.dll" "$@"\n',0o700);
  // A real SDK-client handshake/list probe must succeed before selecting this pair.
  run(dotnet,[path.join(output,'Xur.Mcp.dll'),'--verify'],{cwd:stage});
  const generation=crypto.createHash('sha256').update(JSON.stringify({version,sources,libraries:assets.libraries})).digest('hex');
  const selected=path.join(root,'versions',generation);fs.mkdirSync(path.dirname(selected),{recursive:true});
  if(!fs.existsSync(selected))fs.renameSync(stage,selected);
  link(selected,path.join(root,'current'));
  link(sdkDir,path.join(sdkRoot,'current'));
  fs.mkdirSync(path.join(home,'.local/bin'),{recursive:true});link(path.join(sdkRoot,'current/dotnet'),path.join(home,'.local/bin/dotnet'));
  atomic(path.join(root,'server'),'#!/bin/sh\nexec "'+root+'/current/run" "$@"\n',0o700);
  atomic(path.join(root,'update-status.json'),JSON.stringify({checkedAt:new Date().toISOString(),sdk:version,state:'Ready'}));
 }finally{fs.rmSync(stage,{recursive:true,force:true});}
}
if(require.main===module)update().catch(error=>{
 console.error('Xur .NET/MCP update failed; any previous working installation is retained:',error.message);
 atomic(path.join(root,'update-status.json'),JSON.stringify({checkedAt:new Date().toISOString(),state:'Failed',error:error.message}));process.exitCode=1;
});
module.exports={latest,stable};
