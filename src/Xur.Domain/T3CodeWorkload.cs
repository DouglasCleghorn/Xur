namespace Xur.Domain;

public record T3CodePrepareRequest(string? Volume=null);
public record T3CodeStatus(string? WorkloadId,string State,bool Installed=false,bool Enabled=false,string? Error=null);
public record T3CodeEnabled(bool Enabled);
public record T3CodePairing(string Credential,DateTimeOffset ExpiresAt);
public record T3StorageConfig(bool AllDrives=true,bool Models=true,string[]? Folders=null);

public static class T3CodeWorkload
{
    public const string Engine="T3 Code";
    public const int HostPort=3773;
    public const string Home="/home/node";
    public const string BaseDirectory=Home+"/.t3";
    public static bool Is(Recipe recipe)=>recipe.Kind=="Container" && recipe.Engine==Engine;
    // No browser or terminal QR is needed; Xur issues fresh links on request.
    public static string[] Command=>["/opt/t3/t3","start","--host","0.0.0.0","--port","3773","--base-dir",BaseDirectory,"--no-browser",Home+"/projects"];
    // Zero GPUs means no exclusive allocation; the runtime grants shared access
    // to every host GPU without changing existing recipes or stored profiles.
    public static ContainerPrepareRequest Prepare(string? volume=null)=>new(
        Dockerfile:Dockerfile,Files:[new("t3-launcher",Launcher)],Name:Engine,Port:3773,Command:Command,Engine:Engine,
        Environment:new(){{"HOME",Home}},Volumes:[new("T3 Code home, projects and credentials",Home,volume)]);

    public static void Validate(Recipe recipe)
    {
        if(recipe.Port!=3773 || recipe.HealthPath!="/" || recipe.Vendor!="CPU" || recipe.GpuCount!=0 ||
            !recipe.Command.SequenceEqual(Command) || recipe.Container is not {Mounts.Length:1} config ||
            config.Mounts[0].Destination!=Home || config.Mounts[0].ReadOnly ||
            config.Environment.Count!=1 || !config.Environment.TryGetValue("HOME",out var home) || home!=Home)
            throw new InvalidOperationException("Use the T3 Code preset with its persistent home volume.");
    }

    // Both saved images and newly prepared images use this current bootstrap.
    public static string Launcher {get;}=ReadLauncher();
    static string ReadLauncher()
    {
        using var stream=typeof(T3CodeWorkload).Assembly.GetManifestResourceStream("Xur.T3Launcher")!;
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }

    public const string Dockerfile="""
        FROM mirror.gcr.io/library/node:lts
        RUN apt-get update && apt-get install -y --no-install-recommends git openssh-client ca-certificates curl python3 ripgrep bash \
            && rm -rf /var/lib/apt/lists/*
        COPY t3-launcher /opt/t3/t3
        RUN chmod 755 /opt/t3/t3 && ln -s /opt/t3/t3 /usr/local/bin/t3 \
            && curl --fail --location --retry 3 https://raw.githubusercontent.com/pingdotgg/t3code/main/LICENSE -o /opt/t3/LICENSE
        RUN mkdir -p /home/node/projects /home/node/.t3 /home/node/.local/bin \
            && chown -R node:node /home/node
        ENV HOME=/home/node
        ENV PATH=/home/node/.local/bin:/home/node/.opencode/bin:/home/node/.local/share/xur-tools/bin:$PATH
        USER node
        WORKDIR /home/node/projects
        """;
}
