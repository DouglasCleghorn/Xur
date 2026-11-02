// Run the actual stdin bootstrap, isolated from repository MSBuild settings.
// Reuse an installed SDK; restore, publish, notices and MCP verification are real.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {execFileSync}=require('node:child_process');
const dotnet=fs.realpathSync(process.env.XUR_DOTNET);
const version=execFileSync(dotnet,['--version'],{encoding:'utf8'}).trim();
const root=path.resolve('.build/evidence/t3-dotnet-publish');fs.mkdirSync(root,{recursive:true});
const home=fs.mkdtempSync(path.join(root,'home-'));
try{
 for(const name of ['Directory.Build.props','Directory.Build.targets'])fs.writeFileSync(path.join(home,name),'<Project />');
 const versions=path.join(home,'.local/share/dotnet/versions');fs.mkdirSync(versions,{recursive:true});
 fs.symlinkSync(path.dirname(dotnet),path.join(versions,version));
 const sources=Object.fromEntries(Object.entries({'Program.cs':'tools/Xur.Mcp/Program.cs','Xur.Mcp.csproj':'tools/Xur.Mcp/Xur.Mcp.csproj','LICENSE':'LICENSE','licensing.md':'docs/licensing.md'}).map(([name,file])=>[name,fs.readFileSync(file,'utf8')]));
 const index={'releases-index':[{'channel-version':version.split('.')[0]+'.0','latest-sdk':version,'support-phase':'active','releases.json':'https://fixture/releases'}]};
 const releases={releases:[{sdks:[{version,files:[{rid:{x64:'linux-x64',arm64:'linux-arm64'}[process.arch],url:'https://fixture/sdk.tar.gz',hash:'0'.repeat(128)}]}]}]};
 const input='globalThis.fetch=async url=>({ok:true,json:async()=>url.endsWith("releases-index.json")?'+JSON.stringify(index)+':'+JSON.stringify(releases)+'});\n'+fs.readFileSync('src/Xur.Domain/t3-dotnet.cjs','utf8').replace('__XUR_MCP_SOURCES__',JSON.stringify(sources));
 const env={...process.env,HOME:home,DOTNET_CLI_TELEMETRY_OPTOUT:'1',DOTNET_NOLOGO:'1'};
 execFileSync(process.execPath,['-'],{input,env,stdio:['pipe','inherit','inherit'],timeout:180000});
 const mcp=path.join(home,'.local/share/xur-mcp');
 assert.equal(JSON.parse(fs.readFileSync(path.join(mcp,'update-status.json'),'utf8')).state,'Ready');
 assert.ok(fs.readdirSync(path.join(mcp,'current/app/licenses')).some(name=>name.endsWith('.nupkg')));
 assert.ok(fs.existsSync(path.join(mcp,'current/app/LICENSE')));
 // Also exercise the installed wrapper after the staging directory is renamed.
 execFileSync(path.join(mcp,'server'),['--verify'],{env,stdio:'inherit',timeout:45000});
 console.log('Real stdin bootstrap, NuGet manifests/notices, runtime activation and installed MCP verification passed.');
}finally{fs.rmSync(home,{recursive:true,force:true});}
