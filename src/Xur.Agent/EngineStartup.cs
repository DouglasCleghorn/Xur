using System.Text.Json;
using Xur.Domain;
namespace Xur.Agent;

public static class EngineStartup
{
    public static string? Failure(string log)
    {
        if(log.Contains("embed_tokens.weight_packed",StringComparison.Ordinal)&&log.Contains("no module or parameter",StringComparison.Ordinal))
            return "The checkpoint contains packed token-embedding weights (embed_tokens.weight_packed), but this engine expects embed_tokens.weight. A compatible engine recipe is required; waiting or retrying the same image will not fix it.";
        if(log.Contains("max_num_seqs (")&&log.Contains("exceeds available Mamba cache blocks"))
            return "The engine's concurrency limit exceeds its Mamba cache capacity. Select the model again to use the corrected concurrency settings; retrying the saved command will not fix it.";
        if(log.Contains("No module named 'fish_speech'"))
            return "The vLLM-Omni image is missing Fish Speech's codec dependency (fish_speech). A Fish-enabled engine image is required; retrying this image will not fix it.";
        var lines=log.Split('\n').Select(l=>l.Trim()).Where(l=>l.Length>0).ToArray();
        var cause=lines.LastOrDefault(l=>l.Contains("ModuleNotFoundError:")||l.Contains("ValueError:")||l.Contains("CUDA out of memory")||l.Contains("OutOfMemoryError:"));
        if(cause!=null)return "Engine startup failed: "+Redaction.Logs(cause)[..Math.Min(1200,Redaction.Logs(cause).Length)];
        if(log.Contains("EngineCore failed to start.")||log.Contains("Engine core initialization failed."))return "The engine failed during model initialization. Open the workload logs for the root cause.";
        return null;
    }
    public static void ValidateCheckpoint(string engine,string model,JsonElement config,JsonElement? index=null)
    {
        if(engine is not ("vLLM" or "vLLM-Omni"))return;
        var packed=index is {} i&&i.TryGetProperty("weight_map",out var map)&&map.EnumerateObject().Any(p=>p.Name.EndsWith("embed_tokens.weight_packed",StringComparison.Ordinal));
        if(config.TryGetProperty("quantization_config",out var quant)&&quant.ValueKind==JsonValueKind.Object&&quant.TryGetProperty("config_groups",out var groups)&&groups.ValueKind==JsonValueKind.Object)
            packed|=groups.EnumerateObject().Any(g=>g.Value.TryGetProperty("targets",out var targets)&&targets.ValueKind==JsonValueKind.Array&&targets.EnumerateArray().Any(t=>t.ValueKind==JsonValueKind.String&&t.GetString()!.Contains("embed_tokens",StringComparison.Ordinal)));
        if(packed)throw new InvalidOperationException("This checkpoint uses packed token embeddings. Xur's current "+engine+" image cannot load embed_tokens.weight_packed. It needs a separately pinned compatible engine recipe; the model will not be substituted or modified.");
        if(model.Equals("lued/Qwen3.8-27B-INT8-W8A16-DFlash2",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This DFlash2 checkpoint requires its author's patched vLLM runtime and separate drafter. That runtime recipe is not available in this build.");
    }
}
