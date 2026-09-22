using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;
public sealed class ComputerNameSettings(string filename="/etc/xur/computer-name",Func<string,string[],int,Task<ProcessResult>>? runner=null,bool updateTailscale=true)
{
    readonly SemaphoreSlim gate=new(1,1);
    Task<ProcessResult> Run(string exe,string[] args)=>runner!=null?runner(exe,args,15):Processes.Run(exe,args,15);
    public static string Validate(string? value)
    {
        var name=(value??"").Trim().ToLowerInvariant();
        if(name.Length is <1 or >63 || !Regex.IsMatch(name,@"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$") || name.All(char.IsDigit) || name is "localhost" or "localhost.localdomain")
            throw new InvalidOperationException("Use 1–63 letters, numbers and hyphens, beginning and ending with a letter or number. Choose a name other than localhost.");
        return name;
    }
    public ComputerNameStatus Read()
    {
        if(File.Exists(filename))try{return new(Validate(File.ReadAllText(filename)),true);}catch(InvalidOperationException){}
        var current=Environment.MachineName;
        try{current=Validate(current);}catch(InvalidOperationException){current="xur";}
        return new(current,false);
    }
    public async Task<ComputerNameStatus> Set(string name)
    {
        name=Validate(name);await gate.WaitAsync();try
        {
            if((await Run("hostnamectl",["set-hostname",name])).ExitCode!=0)throw new InvalidOperationException("Could not save the server name. Try again from the console.");
            Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
            await File.WriteAllTextAsync(filename+".tmp",name+"\n");File.SetUnixFileMode(filename+".tmp",UnixFileMode.UserRead|UnixFileMode.UserWrite);File.Move(filename+".tmp",filename,true);
            var message="Server name saved. It will be kept after reboot and installation.";
            if(!updateTailscale)return new(name,true,message);
            // An already-enrolled node keeps its identity while updating its advertised name.
            try
            {
                var status=await Run("tailscale",["status","--json"]);
                if(status.ExitCode==0 && System.Text.Json.JsonDocument.Parse(status.Output) is {} doc)
                {
                    using(doc)if(doc.RootElement.TryGetProperty("BackendState",out var state)&&state.GetString()=="Running" && (await Run("tailscale",["set","--hostname="+name])).ExitCode!=0)
                        message+=" Tailscale could not update its name; check its settings.";
                }
            }catch{message+=" Tailscale is unavailable; its name will be used when you enroll.";}
            return new(name,true,message);
        }finally{gate.Release();}
    }
}
