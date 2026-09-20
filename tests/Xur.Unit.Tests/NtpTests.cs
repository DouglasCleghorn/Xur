using Xur.Agent;
using Xur.Domain;
static class NtpTests
{
 public static async Task Run(Action<bool,string> check)
 {
  var root=Path.Combine(Path.GetTempPath(),"xur-ntp-"+Guid.NewGuid());Directory.CreateDirectory(root);
  var config=root+"/chrony.conf";var source=root+"/chrony.d/xur.conf";var original="pool default.example iburst\nsourcedir /run/chrony-dhcp\n";File.WriteAllText(config,original);
  var enabled=true;var active=true;var reject=false;
  Task<ProcessResult> Run(string exe,string[] args,int timeout)
  {
   if(exe=="chronyc")return Task.FromResult(new ProcessResult(0,"Leap status : Normal\n"));
   if(exe=="chronyd")return Task.FromResult(new ProcessResult(reject?1:0,"validation"));
   if(exe=="systemctl")switch(args[0]){
    case "is-enabled":return Task.FromResult(new ProcessResult(enabled?0:1,""));
    case "is-active":return Task.FromResult(new ProcessResult(active?0:1,""));
    case "enable":enabled=true;if(args.Contains("--now"))active=true;break;
    case "disable":enabled=false;if(args.Contains("--now"))active=false;break;
    case "restart":active=true;break;
    case "stop":active=false;break;
   }
   return Task.FromResult(new ProcessResult(0,""));
  }
  try{
   foreach(var invalid in new[]{"https://pool.example","host iburst","host\nallow all","-option","fe80::1%eth0"}){
    var failed=false;try{NtpSettings.Validate([invalid]);}catch(InvalidOperationException){failed=true;}check(failed,"NTP rejects invalid server: "+invalid.Replace('\n',' '));
   }
   var settings=new NtpSettings(config,source,Run);
   await settings.Initialize();check((await settings.Read()).Servers.SequenceEqual(new[]{"time.cloudflare.com"}),"First NTP setup defaults to Cloudflare");
   var value=await settings.Set(new(true,["time.example","192.0.2.1","2001:db8::1"]));
   check(value.Enabled&&value.Active&&value.Synchronized&&value.Servers.Length==3,"NTP saves preferred hosts and reports synchronization");
   await settings.Initialize();check((await settings.Read()).Servers.SequenceEqual(value.Servers),"NTP boot initialization preserves custom sources");
   check(File.ReadAllText(config).StartsWith(original),"NTP preserves OS and DHCP sources");
   await settings.Set(new(false,[]));check(!enabled&&!active,"NTP disable stops service and persists disabled state");
   check(File.ReadAllText(config).Split("include ").Length==2,"NTP repeated saves do not duplicate includes");
   await settings.Initialize();check(!enabled&&(await settings.Read()).Servers.Length==0,"NTP initialization preserves explicit disabled or OS-only choices");
   var savedConfig=File.ReadAllText(config);var savedSources=File.ReadAllText(source);reject=true;
   try{await settings.Set(new(true,["bad.example"]));throw new Exception("Expected validation rejection");}catch(InvalidOperationException){}
   check(File.ReadAllText(config)==savedConfig&&File.ReadAllText(source)==savedSources&&!enabled&&!active,"Invalid Chrony configuration rolls back files and service state");
   var reloaded=await new NtpSettings(config,source,Run).Read();check(!reloaded.Enabled&&reloaded.Servers.Length==0,"NTP settings survive settings service recreation");
  }finally{Directory.Delete(root,true);}
 }
}
