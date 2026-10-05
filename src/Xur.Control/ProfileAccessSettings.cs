using System.Text.Json;
namespace Xur.Control;

public sealed class ProfileAccessSettings(string path)
{
    public const string Web="web",Console="console",Workstations="workstations";
    readonly object gate=new();
    public string Mode
    {
        get
        {
            lock(gate)
            {
                if(!File.Exists(path))return Workstations;
                try {var mode=JsonSerializer.Deserialize<Stored>(File.ReadAllText(path))?.Mode;return Valid(mode)?mode!:Web;}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){return Web;}
            }
        }
    }
    public bool ConsoleAllowed=>Mode is Console or Workstations;
    public bool WorkstationsAllowed=>Mode==Workstations;
    public static bool Valid(string? mode)=>mode is Web or Console or Workstations;
    public void Save(string mode)
    {
        if(!Valid(mode))throw new InvalidOperationException("Choose a valid profile access option.");
        lock(gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using(var file=new FileStream(path+".tmp",new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,UnixCreateMode=(UnixFileMode)384}))
            {JsonSerializer.Serialize(file,new Stored(mode));file.Flush(true);}
            File.Move(path+".tmp",path,true);
        }
    }
    record Stored(string Mode);
}
