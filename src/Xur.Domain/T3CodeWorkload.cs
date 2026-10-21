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

    // Keep the launcher path stable for saved recipes. The upstream installer and
    // updater own the runtime and symlink inside the persistent, writable home.
    public const string Launcher="""
        #!/bin/sh
        set -eu
        export T3CODE_HOME="${T3CODE_HOME:-$HOME/.t3}"
        export T3CODE_INSTALL_BIN_DIR="${T3CODE_INSTALL_BIN_DIR:-$HOME/.local/bin}"
        t3_command="$T3CODE_INSTALL_BIN_DIR/t3"
        if [ ! -x "$t3_command" ]; then
            printf '%s\n' 'Installing the latest stable T3 Code into persistent storage…'
            installer="$(mktemp)"
            trap 'rm -f "$installer"' EXIT
            trap 'exit 130' INT
            trap 'exit 143' TERM
            curl --fail --location --retry 3 --connect-timeout 15 --max-time 120 \
                https://t3.codes/install.sh -o "$installer"
            sh "$installer"
            test -x "$t3_command" || { printf '%s\n' 'T3 Code installation did not create an executable.' >&2; exit 1; }
            rm -f "$installer"
            trap - EXIT INT TERM
        fi
        exec "$t3_command" "$@"
        """;

    public const string Dockerfile="""
        FROM mirror.gcr.io/library/node:lts
        RUN apt-get update && apt-get install -y --no-install-recommends git openssh-client ca-certificates curl python3 ripgrep \
            && rm -rf /var/lib/apt/lists/*
        COPY t3-launcher /opt/t3/t3
        RUN chmod 755 /opt/t3/t3 && ln -s /opt/t3/t3 /usr/local/bin/t3 \
            && curl --fail --location --retry 3 https://raw.githubusercontent.com/pingdotgg/t3code/main/LICENSE -o /opt/t3/LICENSE
        RUN mkdir -p /home/node/projects /home/node/.t3 /home/node/.local/bin \
            && chown -R node:node /home/node
        ENV HOME=/home/node
        ENV PATH=/home/node/.local/bin:$PATH
        USER node
        WORKDIR /home/node/projects
        """;
}
