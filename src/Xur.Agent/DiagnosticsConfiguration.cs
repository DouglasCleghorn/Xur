using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;
namespace Xur.Agent;

// Deliberately separate from the answer file: never persisted to the installed OS.
public sealed class DiagnosticsConfiguration
{
    public string ApiKey {get;}
    public bool AllowControl {get;}
    DiagnosticsConfiguration(string apiKey,bool allowControl){ApiKey=apiKey;AllowControl=allowControl;}
    public static DiagnosticsConfiguration Parse(string yaml)
    {
        if(yaml.Length>32768)throw new FormatException("Diagnostic configuration exceeds 32 KiB.");
        var stream=new YamlStream();stream.Load(new StringReader(yaml));
        if(stream.Documents.Count!=1 || stream.Documents[0].RootNode is not YamlMappingNode map ||
            map.Children.Keys.Any(k=>k is not YamlScalarNode s || s.Value is not ("schemaVersion" or "apiKey" or "allowControl")))
            throw new FormatException("Invalid diagnostic configuration.");
        string Read(string name)=>map.Children.TryGetValue(new YamlScalarNode(name),out var node)&&node is YamlScalarNode scalar?scalar.Value??"":"";
        if(Read("schemaVersion")!="1" || !Regex.IsMatch(Read("apiKey"),"\\A[a-fA-F0-9]{64}\\z") || Read("allowControl") is not ("" or "true" or "false"))
            throw new FormatException("Use schemaVersion 1, a random 64-character hexadecimal apiKey, and boolean allowControl.");
        if(map.Children.Any(p=>p.Value is not YamlScalarNode))throw new FormatException("Expected scalar diagnostic settings.");
        return new(Read("apiKey"),Read("allowControl")=="true");
    }
}
