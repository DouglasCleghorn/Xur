using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Text;
using YamlDotNet.RepresentationModel;
namespace Xur.Agent;

// Deliberately separate from the answer file: never persisted to the installed OS.
public sealed class DiagnosticsConfiguration
{
    public string ApiKey {get;}
    public bool AllowControl {get;}
    public string[] SshAuthorizedKeys {get;}
    DiagnosticsConfiguration(string apiKey,bool allowControl,string[] sshAuthorizedKeys){ApiKey=apiKey;AllowControl=allowControl;SshAuthorizedKeys=sshAuthorizedKeys;}
    static string PublicKey(string value)
    {
        if(value.Length>1024 || value.Any(c=>c<' ' || c>'~'))throw new FormatException("Use a single-line Ed25519 SSH public key without authorized_keys options.");
        var fields=value.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(fields.Length<2 || fields[0]!="ssh-ed25519")throw new FormatException("Use an Ed25519 SSH public key without authorized_keys options.");
        byte[] blob;
        try{blob=Convert.FromBase64String(fields[1]);}catch(FormatException){throw new FormatException("Invalid SSH public key encoding.");}
        if(blob.Length!=51 || BinaryPrimitives.ReadUInt32BigEndian(blob)!=11 || Encoding.ASCII.GetString(blob,4,11)!="ssh-ed25519" || BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(15))!=32)
            throw new FormatException("Invalid Ed25519 SSH public key.");
        return "ssh-ed25519 "+Convert.ToBase64String(blob);
    }
    public static DiagnosticsConfiguration Parse(string yaml)
    {
        if(yaml.Length>32768)throw new FormatException("Diagnostic configuration exceeds 32 KiB.");
        var stream=new YamlStream();stream.Load(new StringReader(yaml));
        if(stream.Documents.Count!=1 || stream.Documents[0].RootNode is not YamlMappingNode map ||
            map.Children.Keys.Any(k=>k is not YamlScalarNode s || s.Value is not ("schemaVersion" or "apiKey" or "allowControl" or "sshAuthorizedKeys")))
            throw new FormatException("Invalid diagnostic configuration.");
        string Read(string name)=>map.Children.TryGetValue(new YamlScalarNode(name),out var node)&&node is YamlScalarNode scalar?scalar.Value??"":"";
        if(Read("schemaVersion")!="1" || !Regex.IsMatch(Read("apiKey"),"\\A[a-fA-F0-9]{64}\\z") || Read("allowControl") is not ("" or "true" or "false"))
            throw new FormatException("Use schemaVersion 1, a random 64-character hexadecimal apiKey, and boolean allowControl.");
        if(map.Children.Any(p=>((YamlScalarNode)p.Key).Value!="sshAuthorizedKeys" && p.Value is not YamlScalarNode))throw new FormatException("Expected scalar diagnostic settings.");
        string[] keys=[];
        if(map.Children.TryGetValue(new YamlScalarNode("sshAuthorizedKeys"),out var keyNode))
        {
            if(keyNode is not YamlSequenceNode sequence || sequence.Children.Count>8 || sequence.Children.Any(n=>n is not YamlScalarNode))
                throw new FormatException("sshAuthorizedKeys must be a list of at most eight Ed25519 public keys.");
            keys=sequence.Children.Select(n=>PublicKey(((YamlScalarNode)n).Value??"")).ToArray();
            if(keys.Distinct(StringComparer.Ordinal).Count()!=keys.Length)throw new FormatException("Duplicate SSH public keys.");
            if(keys.Length>0 && Read("allowControl")!="true")throw new FormatException("SSH root access requires allowControl: true.");
        }
        return new(Read("apiKey"),Read("allowControl")=="true",keys);
    }
}
