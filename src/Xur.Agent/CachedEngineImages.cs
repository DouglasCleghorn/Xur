using System.Text.Json;
using System.Text.RegularExpressions;
using Xur.Domain;
namespace Xur.Agent;

public sealed class CachedEngineImages(Func<string,string[],int,Task<ProcessResult>> run)
{
    public async Task<string?> Newest(Recipe recipe)
    {
        var reference=EngineImages.For(recipe);var split=reference.LastIndexOf(':');
        var repository=reference[..split];var channel=reference[(split+1)..];
        bool Matches(string name)
        {
            name=name.Replace("docker.io/","mirror.gcr.io/",StringComparison.Ordinal);
            if(!name.StartsWith(repository+":",StringComparison.Ordinal))return false;
            var tag=name[(repository.Length+1)..];
            return channel=="latest"?tag=="latest" || Regex.IsMatch(tag,@"\Av?\d+(\.\d+){1,3}\z"):
                tag==channel || channel.StartsWith("server",StringComparison.Ordinal) && Regex.IsMatch(tag,"\\A"+Regex.Escape(channel)+@"-b\d+\z");
        }
        var listed=await run("podman",["image","ls","--no-trunc","--format={{.ID}} {{.Repository}}:{{.Tag}}"],20);
        var ids=new HashSet<string>(StringComparer.Ordinal);
        if(listed.ExitCode==0)foreach(var line in listed.Output.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length==2&&Matches(parts[1])&&Regex.IsMatch(parts[0],@"\A(sha256:)?[a-f0-9]{64}\z"))ids.Add(parts[0]);
        }
        // Original saved recipes may have pulled by digest without creating a
        // tag. They still identify this engine's variant and approved registry.
        var selected=recipe.Image.Replace("docker.io/","mirror.gcr.io/",StringComparison.Ordinal);
        if(Matches(selected) || selected.StartsWith(repository+"@sha256:",StringComparison.Ordinal))
        {ids.Add(recipe.Image);ids.Add(selected);}
        string? newest=null;var created=DateTimeOffset.MinValue;
        foreach(var id in ids)
        {
            var inspected=await run("podman",["image","inspect",id],15);
            if(inspected.ExitCode!=0)continue;
            using var doc=JsonDocument.Parse(inspected.Output);
            foreach(var image in doc.RootElement.EnumerateArray())
            {
                if(image.GetProperty("Architecture").GetString()!="amd64" || image.GetProperty("Os").GetString()!="linux")continue;
                if(!DateTimeOffset.TryParse(image.GetProperty("Created").GetString(),System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var timestamp))continue;
                var identity=image.GetProperty("Id").GetString()!;
                if(Regex.IsMatch(identity,@"\A[a-f0-9]{64}\z"))identity="sha256:"+identity;
                if(Regex.IsMatch(identity,@"\Asha256:[a-f0-9]{64}\z")&&timestamp>=created){newest=identity;created=timestamp;}
            }
        }
        return newest;
    }
}
