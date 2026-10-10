using System.Text;
using System.Text.Json;
namespace Xur.Robot;
// Container-local tools. No engine socket, host agent API or client-supplied commands.
public sealed class RoboticsContainer(string directory,Func<string,IEnumerable<string>,int,CancellationToken,Task<ProcessResult>>? run=null,IRobotTools? preparationTools=null)
{
    public const string Name="xur-robot-web";
    public static string Python=>Environment.GetEnvironmentVariable("XUR_ROBOT_PYTHON")??"python";
    public static string ToolsDirectory=>Environment.GetEnvironmentVariable("XUR_ROBOT_TOOLS")??"/opt/xur";
    Task<ProcessResult> Run(IEnumerable<string> args,int seconds,CancellationToken token)=>run!=null?run(Python,args,seconds,token):Processes.Run(Python,args,seconds,token);
    public async Task<bool> Prepare(RoboticsConfiguration configuration,CancellationToken cancellation)
    {
        // The adapter holds its cross-process ownership lease throughout alias
        // validation and atomic readiness publication, including startup recovery.
        Directory.CreateDirectory(directory);
        var result=await (preparationTools??new LeRobotTools()).Run(configuration,"prepare",null,10,cancellation);
        return result.GetProperty("controllerAvailable").GetBoolean();
    }
    public async Task<RobotBusDetection> DetectBuses(string[] ports,CancellationToken cancellation)
    {
        if(ports.Length!=2 || ports.Distinct(StringComparer.Ordinal).Count()!=2)throw new InvalidOperationException("Connect exactly two distinct serial adapters.");
        if(ports.Any(p=>!File.Exists(p)))throw new InvalidOperationException("Serial adapter disconnected during discovery.");
        var result=await Run(new[]{Path.Combine(ToolsDirectory,"discover_buses.py")}.Concat(ports),60,cancellation);
        if(result.ExitCode!=0)throw new InvalidOperationException("Read-only upstream discovery failed: "+Xur.Domain.Redaction.Logs(result.Output[^Math.Min(result.Output.Length,2048)..]));
        var reader=new Utf8JsonReader(Encoding.UTF8.GetBytes(result.Output));
        var inventories=JsonSerializer.Deserialize(ref reader,RobotJson.Default.DictionaryInt32Int32Array)??throw new InvalidOperationException("No motor inventory returned.");
        if(inventories.Length!=ports.Length)throw new InvalidOperationException("Incomplete motor inventory.");
        return RobotBusDetection.MatchXLeRobot(ports.Select((p,i)=>new RobotBusInventory(p,inventories[i])).ToArray());
    }
    public async Task Interrupt()
    {
        var signal=await Run([Path.Combine(ToolsDirectory,"bridge.py"),"--stop"],5,CancellationToken.None);
        if(signal.ExitCode!=0)throw new InvalidOperationException("Upstream software stop failed. Inspect motor power before resetting.");
        for(var attempt=0;attempt<25;attempt++)
        {
            if((await Run([Path.Combine(ToolsDirectory,"bridge.py"),"--idle"],2,CancellationToken.None)).ExitCode==0)return;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("Robot tool did not release its ownership lock. Disconnect motor power and stop the Robotics profile.");
    }
    public Task Stop()=>Interrupt();
}
