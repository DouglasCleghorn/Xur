using Xur.Domain;
using YamlDotNet.RepresentationModel;
namespace Xur.Agent;

public record AnswerConfiguration(string? BootstrapToken,NetworkConfiguration[] Network)
{
    public static AnswerConfiguration Parse(string yaml)
    {
        if(yaml.Length>32768)throw new FormatException("Answer file exceeds 32 KiB.");
        var stream=new YamlStream();stream.Load(new StringReader(yaml));
        if(stream.Documents.Count!=1)throw new FormatException("Use one YAML document.");
        var root=Map(stream.Documents[0].RootNode,"schemaVersion","bootstrapToken","network");
        if(Text(root,"schemaVersion") is not ("" or "1"))throw new FormatException("Unsupported answer schema.");
        var token=Text(root,"bootstrapToken");
        var interfaces=new List<NetworkConfiguration>();
        if(Get(root,"network") is {} network)
        {
            var map=Map(network,"interfaces");
            if(Get(map,"interfaces") is not YamlSequenceNode list || list.Children.Count is <1 or >16)throw new FormatException("network.interfaces must contain one to sixteen wired adapters.");
            foreach(var entry in list)
            {
                var adapter=Map(entry,"interface","macAddress","ipv4","ipv6");
                interfaces.Add(NetworkValidation.Validate(new(Text(adapter,"interface"),Text(adapter,"macAddress"),Ip(Get(adapter,"ipv4")),Ip(Get(adapter,"ipv6")))));
            }
            if(interfaces.Where(i=>i.MacAddress.Length>0).GroupBy(i=>i.MacAddress).Any(g=>g.Count()>1) || interfaces.Where(i=>i.Interface.Length>0).GroupBy(i=>i.Interface).Any(g=>g.Count()>1))throw new FormatException("Each network adapter may appear only once.");
        }
        return new(token.Length==0?null:BootstrapCode.Parse(token),interfaces.ToArray());
    }
    static IpConfiguration Ip(YamlNode? node)
    {
        if(node==null)return new();
        var map=Map(node,"method","addresses","gateway","dns");
        var method=Text(map,"method");
        return new(method.Length==0?"auto":method,Strings(Get(map,"addresses")),Text(map,"gateway"),Strings(Get(map,"dns")));
    }
    static string[] Strings(YamlNode? node)=>node==null?[]:node is YamlSequenceNode list && list.Children.All(n=>n is YamlScalarNode)?list.Select(n=>((YamlScalarNode)n).Value??"").ToArray():throw new FormatException("Use a YAML list of addresses.");
    static YamlMappingNode Map(YamlNode node,params string[] keys)
    {
        if(node is not YamlMappingNode map || map.Children.Keys.Any(k=>k is not YamlScalarNode s || !keys.Contains(s.Value)))throw new FormatException("Unknown or invalid answer field.");
        return map;
    }
    static YamlNode? Get(YamlMappingNode map,string key)=>map.Children.TryGetValue(new YamlScalarNode(key),out var value)?value:null;
    static string Text(YamlMappingNode map,string key)=>Get(map,key) switch {null=>"",YamlScalarNode s=>s.Value??"",_=>throw new FormatException("Expected a text value.")};
}
