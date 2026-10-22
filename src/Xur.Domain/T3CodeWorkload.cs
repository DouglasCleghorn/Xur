namespace Xur.Domain;

public record T3CodePrepareRequest(string? Volume=null);
public record T3CodeStatus(string? WorkloadId,string State);
public record T3CodePairing(string Credential,DateTimeOffset ExpiresAt);

public static class T3CodeWorkload
{
    public const string Engine="T3 Code";
    public const int HostPort=3773;
    public const string Home="/home/node";
    public const string BaseDirectory=Home+"/.t3";
    public static bool Is(Recipe recipe)=>recipe.Kind=="Container" && recipe.Engine==Engine;
    // No browser or terminal QR is needed; Xur issues fresh links on request.
    public static string[] Command=>["/opt/t3/t3","start","--host","0.0.0.0","--port","3773","--base-dir",BaseDirectory,"--no-browser",Home+"/projects"];
    public static ContainerPrepareRequest Prepare(string? volume=null)=>new(
        Dockerfile:Dockerfile,Name:Engine,Port:3773,Command:Command,Engine:Engine,
        Environment:new(){{"HOME",Home}},Volumes:[new("T3 Code home, projects and credentials",Home,volume)]);

    public static void Validate(Recipe recipe)
    {
        if(recipe.Port!=3773 || recipe.HealthPath!="/" || recipe.Vendor!="CPU" || recipe.GpuCount!=0 ||
            !recipe.Command.SequenceEqual(Command) || recipe.Container is not {Mounts.Length:1} config ||
            config.Mounts[0].Destination!=Home || config.Mounts[0].ReadOnly ||
            config.Environment.Count!=1 || !config.Environment.TryGetValue("HOME",out var home) || home!=Home)
            throw new InvalidOperationException("Use the T3 Code preset with its persistent home volume.");
    }

    // The upstream release includes the web assets and dependency license notices.
    // Keep the complete distribution: node-pty loads files beside the executable.
    public const string Dockerfile="""
        FROM mirror.gcr.io/library/node:24-bookworm
        RUN apt-get update && apt-get install -y --no-install-recommends git openssh-client ca-certificates curl python3 ripgrep \
            && rm -rf /var/lib/apt/lists/*
        ARG XUR_T3_PREPARATION
        RUN test -n "$XUR_T3_PREPARATION" \
            && curl --fail --location --retry 3 https://t3.codes/install.sh -o /tmp/t3-install.sh \
            && T3CODE_CHANNEL=stable T3CODE_HOME=/opt/t3 T3CODE_INSTALL_BIN_DIR=/opt/t3 sh /tmp/t3-install.sh \
            && rm /tmp/t3-install.sh && ln -s /opt/t3/t3 /usr/local/bin/t3 \
            && version="$(cat "$(dirname "$(readlink -f /opt/t3/t3)")/.install-complete")" \
            && curl --fail --location --retry 3 "https://raw.githubusercontent.com/pingdotgg/t3code/v${version}/LICENSE" -o /opt/t3/LICENSE \
            && /opt/t3/t3 --version
        RUN mkdir -p /home/node/projects /home/node/.t3 /home/node/.local/bin \
            && chown -R node:node /home/node
        ENV HOME=/home/node
        ENV PATH=/home/node/.local/bin:$PATH
        USER node
        WORKDIR /home/node/projects
        """;
}
