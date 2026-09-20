using Xur.Agent;
using Xur.Domain;
using System.Globalization;
static class GpuPowerTests
{
 public static async Task Run(Action<bool,string> check)
 {
  var root=Path.Combine(Path.GetTempPath(),"xur-power-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  try{
   var gpu=new GpuDevice("0000:01:00.0","NVIDIA","RTX 3090","nvidia","0",24576,[],[]);
   GpuDevice[] cards=[gpu];string uuid="GPU-first";double current=350;int writes=0;bool reject=false,ignore=false;
   Task<GpuDevice[]> Inventory()=>Task.FromResult(cards);
   Task<ProcessResult> Run(string exe,string[] args,int timeout){
    check(exe=="nvidia-smi","Power operation uses the NVIDIA driver tool");
    if(args.Any(a=>a.StartsWith("--power-limit="))){
     check(args.Contains("--id="+uuid),"Power write targets GPU UUID instead of transient index");writes++;
     if(reject)return Task.FromResult(new ProcessResult(1,"driver failure"));
     if(!ignore)current=double.Parse(args.Single(a=>a.StartsWith("--power-limit="))[14..],CultureInfo.InvariantCulture);
     return Task.FromResult(new ProcessResult(0,""));
    }
    return Task.FromResult(new ProcessResult(0,$"{uuid}, {current.ToString(CultureInfo.InvariantCulture)}, 350, 100, 400"));
   }
   var power=new GpuPower(root,inventory:Inventory,run:Run);
   check((await power.Status()).Cards is [{CanChange:true,MinimumWatts:100,MaximumWatts:400,DefaultWatts:350}],"Driver bounds and default are exposed");
   await power.Set(new(gpu.Pci,"NVIDIA:"+uuid,275));check(current==275,"Power change is applied and read back");
   current=350;power=new GpuPower(root,inventory:Inventory,run:Run);await power.Reconcile();check(current==275,"Saved power limit is restored by a new agent after driver reset/reboot");
   await power.Set(new(gpu.Pci,"NVIDIA:"+uuid,null));current=275;await new GpuPower(root,inventory:Inventory,run:Run).Reconcile();check(current==350,"Use default persists and restores the driver's default");
   int before=writes;
   foreach(double invalid in new[]{99d,401d,double.NaN,double.PositiveInfinity}){bool denied=false;try{await power.Set(new(gpu.Pci,"NVIDIA:"+uuid,invalid));}catch(InvalidOperationException){denied=true;}check(denied,"Out-of-range or nonfinite power is rejected");}
   uuid="GPU-replacement";bool swapped=false;try{await power.Set(new(gpu.Pci,"NVIDIA:GPU-first",250));}catch(InvalidOperationException){swapped=true;}
   check(swapped&&writes==before,"Replaced GPU cannot receive a stale request");
   cards=[gpu,gpu with{Pci="0000:02:00.0"}];check((await power.Status()).Cards.All(c=>!c.CanChange),"Duplicate GPU UUID disables power changes");cards=[gpu];
   reject=true;bool failed=false;try{await power.Set(new(gpu.Pci,"NVIDIA:"+uuid,250));}catch(InvalidOperationException){failed=true;}check(failed&&(await power.Status()).Cards[0].Message!=null,"Rejected driver write remains visible with saved intent");
   reject=false;await power.Reconcile();check(current==250&&(await power.Status()).Cards[0].Message==null,"Reconciliation retries and clears resolved errors");
   ignore=true;bool unconfirmed=false;try{await power.Set(new(gpu.Pci,"NVIDIA:"+uuid,260));}catch(InvalidOperationException){unconfirmed=true;}check(unconfirmed,"Successful command without matching readback is not reported as applied");
   var sys=root+"/sys";var device=sys+"/bus/pci/devices/0000:03:00.0";var sensor=device+"/hwmon/hwmon4";Directory.CreateDirectory(sensor);
   File.WriteAllText(device+"/unique_id","123456");foreach(var p in new Dictionary<string,string>{{"name","amdgpu"},{"power1_cap","150000000"},{"power1_cap_min","50000000"},{"power1_cap_max","200000000"},{"power1_cap_default","150000000"}})File.WriteAllText(sensor+"/"+p.Key,p.Value);
   var amd=gpu with{Pci="0000:03:00.0",Vendor="AMD",Name="AMD GPU"};cards=[amd];var native=new GpuPower(root+"/native",sys,Inventory,Run);
   var card=(await native.Status()).Cards.Single();check(card.CanChange&&card.CurrentWatts==150,"GPU-local AMD hwmon controls convert microwatts to watts");
   await native.Set(new(amd.Pci,card.Identity,125));check(File.ReadAllText(sensor+"/power1_cap")=="125000000","AMD writes the exact GPU-local power cap");
   File.WriteAllText(sensor+"/power1_cap","150000000");await new GpuPower(root+"/native",sys,Inventory,Run).Reconcile();check(File.ReadAllText(sensor+"/power1_cap")=="125000000","Native power intent survives process restart");
   cards=[amd with{Vendor="Intel"}];File.WriteAllText(sensor+"/name","i915");File.WriteAllText(sensor+"/power1_max","150000000");File.WriteAllText(sensor+"/power1_rated_max","150000000");
   var intel=new GpuPower(root+"/intel",sys,Inventory,Run);var ic=(await intel.Status()).Cards[0];
   check(ic.CanChange&&ic.MinimumWatts==null&&ic.MaximumWatts==150,"Intel uses PL1 with a rated-default ceiling, without inventing a driver minimum");
   await intel.Set(new(cards[0].Pci,ic.Identity,100));check(File.ReadAllText(sensor+"/power1_max")=="100000000"&&File.ReadAllText(sensor+"/power1_cap")=="125000000","Intel writes sustained PL1 and leaves burst PL2 untouched");
   foreach(var invalid in new[]{0d,.5d,151d}){bool denied=false;try{await intel.Set(new(cards[0].Pci,ic.Identity,invalid));}catch(InvalidOperationException){denied=true;}check(denied,"Intel cannot disable limiting with zero or exceed rated TDP");}
   File.WriteAllText(sensor+"/name","xe");File.WriteAllText(sensor+"/power1_max","150000000");await new GpuPower(root+"/intel",sys,Inventory,Run).Reconcile();check(File.ReadAllText(sensor+"/power1_max")=="100000000","Intel xe PL1 restores after agent restart");
   cards=[amd];File.WriteAllText(sensor+"/name","amdgpu");
   File.Delete(sensor+"/power1_cap_min");check(!(await native.Status()).Cards[0].CanChange,"Missing hardware bounds cannot be guessed");
   cards=[gpu with{Vendor="Virtual",Pci="vmbus:display"}];check(!(await native.Status()).Cards[0].CanChange,"Virtual display does not get a fabricated power control");
   File.WriteAllText(root+"/gpu-power.json","broken");check(!(await new GpuPower(root,inventory:Inventory,run:Run).Status()).Cards[0].CanChange,"Malformed saved settings disable power writes without crashing the agent");
  }finally{Directory.Delete(root,true);}
 }
}
