using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

// Fish's optional codec is absent from the generic Omni image. Keep the base
// engine pinned and build the small, versioned dependency layer on demand.
public sealed class FishEngine(string state,Func<string,string[],int,Task<ProcessResult>>? execute=null)
{
    static readonly SemaphoreSlim gate=new(1,1);
    readonly Func<string,string[],int,Task<ProcessResult>> run=execute??((exe,args,seconds)=>Processes.Run(exe,args,seconds));
    public static bool Applies(Recipe recipe)=>recipe.Kind=="Model" && recipe.Engine=="vLLM-Omni" && recipe.Hub?.Repository=="fishaudio/s2-pro";
    static string Resource(string name){using var stream=typeof(FishEngine).Assembly.GetManifestResourceStream("Xur.Fish."+name)!;using var reader=new StreamReader(stream);return reader.ReadToEnd();}
    public static string Containerfile=>Resource("Containerfile");
    public static string Requirements=>Resource("requirements.lock");
    public static string BaseImage=>Containerfile.Split('\n')[0][5..].Trim();
    public async Task<string> Prepare(string image)
    {
        // Older saved recipes named Docker Hub directly. Only the same pinned
        // base is eligible; never combine an untested engine with this layer.
        if(image.Replace("docker.io/","mirror.gcr.io/",StringComparison.Ordinal)!=BaseImage)
            throw new InvalidOperationException("This Fish recipe uses a different engine version. Select Fish S2 Pro again before loading it.");
        await gate.WaitAsync();try
        {
            var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Containerfile+"\n"+Requirements)));
            var tag="localhost/xur-fish:"+hash;
            var path=Path.Combine(state,"fish-engine",hash);Directory.CreateDirectory(path);
            var exists=await run("podman",["image","exists",tag],15);
            if(exists.ExitCode!=0)
            {
                await File.WriteAllTextAsync(Path.Combine(path,"Containerfile"),Containerfile);
                await File.WriteAllTextAsync(Path.Combine(path,"requirements.lock"),Requirements);
                var built=await run("podman",["build","--pull=missing","--tag",tag,"--file",Path.Combine(path,"Containerfile"),path],900);
                await File.WriteAllTextAsync(Path.Combine(path,"build.log"),Redaction.Logs(built.Output));
                if(built.ExitCode!=0){var log=Redaction.Logs(built.Output);throw new InvalidOperationException("Could not prepare the Fish speech runtime: "+log[^Math.Min(1800,log.Length)..]);}
            }
            var inspected=await run("podman",["image","inspect",tag],15);
            if(inspected.ExitCode!=0)throw new InvalidOperationException("The prepared Fish runtime could not be inspected.");
            using var doc=JsonDocument.Parse(inspected.Output);var id=doc.RootElement[0].GetProperty("Id").GetString()!;
            if(Regex.IsMatch(id,@"\A[a-f0-9]{64}\z"))id="sha256:"+id;
            if(!Regex.IsMatch(id,@"\Asha256:[a-f0-9]{64}\z"))throw new InvalidOperationException("The Fish runtime has an invalid image identity.");
            var check=await run("podman",["run","--rm","--network=none","--cap-drop=ALL","--security-opt=no-new-privileges","--env","OMP_NUM_THREADS=1","--env","MKL_NUM_THREADS=1","--entrypoint","python3",id,"-c","from vllm_omni.model_executor.models.fish_speech.dac_utils import build_dac_codec; build_dac_codec()"],120);
            if(check.ExitCode!=0)throw new InvalidOperationException(EngineStartup.Failure(check.Output)??"The prepared Fish runtime failed its codec check.");
            return id;
        }
        catch(OperationCanceledException){throw new InvalidOperationException("Fish runtime preparation timed out. Retry loading the profile to resume cached build steps.");}
        finally{gate.Release();}
    }
}
