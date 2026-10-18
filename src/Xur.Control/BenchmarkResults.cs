using System.IO.Compression;
using System.Text.Json;
using Xur.Domain;
using Xur.IO;
namespace Xur.Control;

// A permanent result artifact, independent of telemetry retention. Written once per finished run.
public static class BenchmarkResults
{
    public record Document(int Schema,LabRun Run,LabSummary Summary);
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static string PathFor(string directory,string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid benchmark ID.");
        return Path.Combine(directory,id+".json.gz");
    }
    public static LabRun Read(string directory,string id)
    {
        using var input=File.OpenRead(PathFor(directory,id));
        using var gzip=new GZipStream(input,CompressionMode.Decompress);
        var document=JsonSerializer.Deserialize<Document>(gzip,Json);
        if(document is not {Schema:1,Run:not null}||document.Run.Id!=id||document.Run.State is "Running" or "Cancelling")
            throw new InvalidDataException("Invalid permanent benchmark result.");
        return document.Run;
    }
    public static void Write(string directory,LabRun run)
    {
        if(run.State is "Running" or "Cancelling")throw new InvalidOperationException("The benchmark is still running.");
        var path=PathFor(directory,run.Id);
        using var parent=new DirectoryTree(Path.GetFullPath(directory));
        if(File.Exists(path)){Read(directory,run.Id);RandomAccess.FlushToDisk(parent.Root);return;}
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var output=new FileStream(temp,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=(UnixFileMode)384}))
            {
                using(var gzip=new GZipStream(output,CompressionLevel.SmallestSize,leaveOpen:true))
                    JsonSerializer.Serialize(gzip,new Document(1,run,ModelLab.Summary(run)),Json);
                output.Flush(true);
            }
            try{File.Move(temp,path);} // Never replace a previously saved result.
            catch(IOException)when(File.Exists(path)){Read(directory,run.Id);}
            RandomAccess.FlushToDisk(parent.Root);
        }
        finally{File.Delete(temp);}
    }
}
