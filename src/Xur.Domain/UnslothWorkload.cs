namespace Xur.Domain;

public static class UnslothWorkload
{
    public const string Engine="Unsloth";
    public const string Image="mirror.gcr.io/unsloth/unsloth:latest";
    public const int HostPort=8000;
    public const int HttpsPort=8444;
    public const string Backend="http://127.0.0.1:8000";
    public const string Publish="127.0.0.1:8000:8000";
    public const string StudioHome="/opt/unsloth-studio";
    public static bool Is(Recipe recipe)=>recipe.Kind=="Container" && recipe.Engine==Engine;
    // Host-wide storage deliberately survives changes to profile/workload identities.
    public static ContainerMount[] Mounts=>[
        new("xur-volume-756e736c6f7468000000000000000001","/workspace"),
        new("xur-volume-756e736c6f7468000000000000000002",StudioHome)];
    public static Recipe Recipe=>new("unsloth","Unsloth Studio",Image,[],8000,"/","NVIDIA",1,0,
        "Fine-tune models with persistent training data and access to downloaded models.",Kind:"Container",Engine:Engine,
        Container:new(Mounts,new(){{"HF_HOME","/workspace/.cache/huggingface"},{"UNSLOTH_SKIP_NOTEBOOK_SYNC","1"}}));
    public static void Validate(Recipe recipe)
    {
        if(Canonical.Hash(recipe)!=Canonical.Hash(Recipe))
            throw new InvalidOperationException("Select the built-in Unsloth preset with its persistent storage and NVIDIA GPU.");
    }
}

public record UnslothStatus(bool Running);
public record UnslothPassword(string Password);
