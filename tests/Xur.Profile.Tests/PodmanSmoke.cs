using System.Diagnostics;
using System.Text.Json;
using Xur.Agent;
using Xur.Control;
using Xur.Domain;
static class PodmanSmoke
{
    public static async Task Run(string root,string catalogDirectory,string gatewayBinary)
    {
        Directory.CreateDirectory(root);var catalog=new RecipeCatalog(catalogDirectory);var recipe=catalog.Recipes.Single(r=>r.Id=="smollm2-135m-cpu");
        var pi=new ProcessStartInfo(gatewayBinary){UseShellExecute=false};pi.Environment["XUR_RUN"]=root;pi.Environment["XUR_GATEWAY_STATE"]=root;
        using var gateway=Process.Start(pi)!;
        try
        {
            using var admin=LocalClient.Create(root+"/gateway-admin.sock");using var data=LocalClient.Create(root+"/gateway.sock");
            for(int n=0;n<100;n++){try{(await admin.GetAsync("/routes")).EnsureSuccessStatusCode();break;}catch{await Task.Delay(100);}}
            var runtime=new WorkloadRuntime(root+"/workloads",catalog);using var store=new ProfileStore(root);
            var manager=new ProfileManager(store,runtime,new LocalWorkloadGateway(admin),catalog);
            var llm=new Workload("smoke-chat","Chat",recipe,[],"chat");var aux=llm with {Id="smoke-aux",Name="Auxiliary",Route="aux"};
            await manager.Save(new("smoke-a","Chat",0,[llm]));await manager.Save(new("smoke-b","Two models",0,[llm,aux]));await manager.Save(new("smoke-idle","Idle",0,[]));
            async Task Apply(string id)
            {
                Console.WriteLine("Applying "+id);var p=await manager.Preview(id);await manager.Apply(new(p.Id,p.Digest));
                var waiting=manager.Wait();while(!waiting.IsCompleted){await manager.State();await Task.Delay(10);}await waiting;
                var s=await manager.State();if(s.Operation?.Stage!="Complete")throw new Exception(JsonSerializer.Serialize(s.Operation));
            }
            await Apply("smoke-a");var original=(await runtime.Observe()).Instances.Single();
            var request=new {model="smollm2",messages=new[]{new {role="user",content="What is the capital of France?"}},max_tokens=24,temperature=0};
            var response=await data.PostAsJsonAsync("/chat/v1/chat/completions",request);response.EnsureSuccessStatusCode();
            using var answer=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if(string.IsNullOrWhiteSpace(answer.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()))throw new Exception("No real inference output");
            for(int cycle=0;cycle<5;cycle++){await Apply("smoke-b");await Apply("smoke-a");}
            var current=(await runtime.Observe()).Instances.Single();if(current.Pid!=original.Pid || current.InstanceId!=original.InstanceId)throw new Exception("Unchanged model restarted");
            var after=await data.PostAsJsonAsync("/chat/v1/chat/completions",request);after.EnsureSuccessStatusCode();
            await Apply("smoke-idle");if((await runtime.Observe()).Instances.Length!=0)throw new Exception("Containers not released");
            File.WriteAllText(root+"/receipt.json",JsonSerializer.Serialize(new {suite="RealPodmanModel",result="Passed",recipe.Image,recipe.Model,realInference=true,unchangedPid=original.Pid,unchangedContainer=original.InstanceId,profileSwitch=true,concurrentStatusDuringFiveCycles=true,cleanStop=true},new JsonSerializerOptions{WriteIndented=true})+"\n");
            Console.WriteLine("Real container inference and profile switch passed");
        }
        finally{if(!gateway.HasExited){gateway.Kill(true);await gateway.WaitForExitAsync();}}
    }
}
