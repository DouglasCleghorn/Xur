using System.Text.RegularExpressions;
namespace Xur.Domain;

// The manifest declares upstream channels; each startup resolves their current image.
public static class EngineImages
{
    static readonly IReadOnlyDictionary<string,string> images=Read();
    static IReadOnlyDictionary<string,string> Read()
    {
        using var stream=typeof(EngineImages).Assembly.GetManifestResourceStream("Xur.EngineImages")!;
        using var reader=new StreamReader(stream);
        return Regex.Matches(reader.ReadToEnd(),@"(?m)^FROM (\S+) AS ([a-z0-9-]+)\r?$")
            .ToDictionary(m=>m.Groups[2].Value,m=>m.Groups[1].Value);
    }
    public static string Image(string id)=>images.TryGetValue(id,out var image)?image:throw new InvalidOperationException("Unknown engine image.");
    public static string Version(string id)
    {var tag=Image(id).Split(':')[^1];return tag=="latest"||tag.StartsWith("server",StringComparison.Ordinal)?"Latest":tag;}
    public static string For(Recipe recipe)=>For(recipe.Engine,recipe.Vendor);
    public static string For(string engine,string vendor)=>Image(engine switch {
        "vLLM"=>vendor switch {"NVIDIA"=>"vllm","AMD"=>"vllm-rocm","Intel"=>"vllm-xpu",_=>throw new InvalidOperationException("vLLM requires an available NVIDIA, AMD or Intel GPU.")},
        "vLLM-Omni"=>vendor switch {"NVIDIA"=>"omni","AMD"=>"omni-rocm","Intel"=>"omni-xpu",_=>throw new InvalidOperationException("vLLM-Omni requires an available NVIDIA, AMD or Intel GPU.")},
        "llama.cpp"=>vendor switch {
            "NVIDIA"=>"server-cuda", "AMD"=>"server-rocm", "Intel"=>"server-vulkan", _=>"server"},
        _=>throw new InvalidOperationException("Unknown model engine.")});
    public static Recipe Resolve(Recipe recipe)=>recipe.Image?.StartsWith("@engine/",StringComparison.Ordinal)==true?recipe with{Image=Image(recipe.Image[8..])}:recipe;
    public static bool Equivalent(string first,string second)
    {
        // Accept the original CPU selection when validating saved recipes. Startup
        // resolves the rolling channel separately without altering their fingerprint.
        const string legacy="ghcr.io/ggml-org/llama.cpp@sha256:e33f80e54fc3f403118ab92b24f21dc1a3125ffd0c7725532028eca8128b548d";
        const string release="ghcr.io/ggml-org/llama.cpp:server";
        const string previous="ghcr.io/ggml-org/llama.cpp:server-b10920";
        return first==second || (first==release && (second==legacy || second==previous)) || (second==release && (first==legacy || first==previous));
    }
}
