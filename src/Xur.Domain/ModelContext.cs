namespace Xur.Domain;

public static class ModelContext
{
    public static bool Supports(Recipe recipe)=>recipe.Kind=="Model"&&recipe.Engine is "llama.cpp" or "vLLM";
    public static void Validate(Workload workload)
    {
        if(workload.ContextTokens is {} tokens&&(!Supports(workload.Recipe)||tokens is <1024 or >2097152))
            throw new InvalidOperationException("Set model serving context between 1,024 and 2,097,152 tokens.");
    }
    public static string[] Command(Workload workload)
    {
        Validate(workload);
        if(workload.ContextTokens is not {} tokens)return workload.Recipe.Command;
        var flags=workload.Recipe.Engine=="llama.cpp"?new[]{"-c","--ctx-size"}:new[]{"--max-model-len"};
        var args=new List<string>();
        for(int i=0;i<workload.Recipe.Command.Length;i++)
        {
            var arg=workload.Recipe.Command[i];
            if(!flags.Contains(arg.Split('=')[0]))args.Add(arg);
            else if(!arg.Contains('='))i++;
        }
        args.AddRange([flags[^1],tokens.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        return args.ToArray();
    }
}
