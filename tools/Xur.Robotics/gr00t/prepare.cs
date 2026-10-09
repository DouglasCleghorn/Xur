#:property RestorePackagesWithLockFile=false
// Original Xur code: MIT. Offline deployment plan; never starts a robot or GPU job.
// dotnet run --file tools/Xur.Robotics/gr00t/prepare.cs --artifacts-path .build/robotics/gr00t-cli -- plan OUTPUT_DIRECTORY
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

if (args.Length != 2 && args.Length != 3)
    throw new ArgumentException("Use plan OUTPUT_DIRECTORY or inventory NVIDIA_SMI_CSV OUTPUT_DIRECTORY");
if (args[0] is not ("plan" or "inventory") || args[0] == "plan" && args.Length != 2 || args[0] == "inventory" && args.Length != 3)
    throw new ArgumentException("Use plan OUTPUT_DIRECTORY or inventory NVIDIA_SMI_CSV OUTPUT_DIRECTORY");
var output = Path.GetFullPath(args[^1]);
if (!output.Split(Path.DirectorySeparatorChar).Contains(".build"))
    throw new ArgumentException("Keep plans, inventory and private tokens beneath an ignored .build directory");
Directory.CreateDirectory(output);
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
void Save<T>(string name, T value, JsonTypeInfo<T> type) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, type) + "\n");
if (args[0] == "plan")
{
    var token = Path.Combine(output, "policy-token");
    // Re-running preparation must not silently replace an installed credential.
    if (!File.Exists(token))
    {
        using var file = new FileStream(token, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(System.Text.Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) + "\n"));
    }
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(token, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    Save("connection.json", new Connection {
        schema = 1, server = "xur-epyc.drum-goblin.ts.net", tailscaleIp = "100.119.12.49",
        transport = "upstream-gr00t-zmq-over-ssh", host = "127.0.0.1", port = 15555,
        remoteHost = "127.0.0.1", remotePort = 5555, tokenFile = "policy-token",
        model = "nvidia/GR00T-N1.7-3B", mode = "benchmark", executionEnabled = false,
        robotAdapterConfigured = false, jointCalibrationApproved = false,
        note = "Local port becomes available only after an authenticated SSH tunnel is established. Predictions are not motor commands."
    }, PreparationJson.Default.Connection);
    Save("preparation.json", new Plan {
        schema = 1, upstreamRevision = "d2b7e75b937e3ec9aa5dbc798f08b89692c49734",
        checkpointRevision = "2fc962b973bccdd5d8ce4f67cc63b264d6886495",
        backboneRevision = "9ce19a195e423419c349abfc86fd07178b230561",
        cuda = "12.8", python = "3.12", image = "localhost/xur-gr00t:n1.7",
        serverState = "not-verified", inferenceMinimumVramMiB = 16000,
        fineTuningRecommendedVramMiB = 40000,
        steps = new[] {"Power on xur-epyc and verify authenticated access.",
            "Capture NVIDIA inventory; select one available GPU and check Podman CDI.",
            "Build the pinned container and provide an authorized Hugging Face token file.",
            "Prepare the gated backbone and base checkpoint cache.",
            "Run upstream DROID open-loop inference without robot devices.",
            "Start the token-protected loopback policy server; connect through SSH.",
            "Record calibrated XLeRobot demonstrations, convert LeRobot v3 data to GR00T v2, and fine-tune a custom embodiment.",
            "Validate action mapping and latency before implementing a reviewed robot rollout adapter."},
        motionEnabled = false
    }, PreparationJson.Default.Plan);
    Console.WriteLine(Path.Combine(output, "preparation.json"));
    return;
}
var lines = File.ReadAllLines(args[1]).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
var gpus = new List<Gpu>();
foreach (var line in lines)
{
    // nvidia-smi query: uuid,name,memory.total,memory.free,driver_version,compute_cap
    var cells = line.Split(',').Select(value => value.Trim()).ToArray();
    if (cells.Length != 6 || !Regex.IsMatch(cells[0], @"\AGPU-[a-fA-F0-9-]{8,64}\z")
        || !int.TryParse(cells[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total <= 0
        || !int.TryParse(cells[3], NumberStyles.None, CultureInfo.InvariantCulture, out var free) || free < 0 || free > total
        || !Version.TryParse(cells[4], out var driver)
        || !double.TryParse(cells[5], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var compute) || !double.IsFinite(compute))
        throw new InvalidDataException("Invalid NVIDIA inventory. Use the exact six-field nvidia-smi query in the setup guide.");
    var blockers = new List<string>();
    if (total < 16000 || free < 16000) blockers.Add("At least 16000 MiB usable total and currently free VRAM is required by this inference preflight.");
    if (driver < new Version(570, 26)) blockers.Add("Driver is below the conservative CUDA 12.8 toolkit baseline 570.26; compatibility mode is not configured.");
    if (compute < 8) blockers.Add("The pinned FlashAttention 2 stack needs an Ampere-or-newer GPU (compute capability 8.0+).");
    gpus.Add(new(cells[0], cells[1], total, free, cells[4], compute, blockers.ToArray()));
}
if (gpus.Select(gpu => gpu.Uuid).Distinct(StringComparer.Ordinal).Count() != gpus.Count)
    throw new InvalidDataException("Duplicate NVIDIA GPU identities in inventory");
Save("inventory-assessment.json", new Assessment {
    schema = 1, capturedAt = DateTimeOffset.UtcNow, gpus = gpus,
    inferenceHardwareCandidate = gpus.Any(gpu => gpu.Blockers.Length == 0),
    fineTuningHardwareCandidate = gpus.Any(gpu => gpu.Blockers.Length == 0 && gpu.TotalMiB >= 40000 && gpu.FreeMiB >= 40000),
    containerRuntimeVerified = false, modelAccessVerified = false, gpuInferenceTested = false,
    robotCompatible = false, motionEnabled = false
}, PreparationJson.Default.Assessment);
Console.WriteLine(Path.Combine(output, "inventory-assessment.json"));
record Gpu(string Uuid, string Name, int TotalMiB, int FreeMiB, string Driver, double ComputeCapability, string[] Blockers);
// Lowercase member names match the reviewed preparation file format exactly.
record Connection {
    public int schema { get; init; }
    public string server { get; init; } = "";
    public string tailscaleIp { get; init; } = "";
    public string transport { get; init; } = "";
    public string host { get; init; } = "";
    public int port { get; init; }
    public string remoteHost { get; init; } = "";
    public int remotePort { get; init; }
    public string tokenFile { get; init; } = "";
    public string model { get; init; } = "";
    public string mode { get; init; } = "";
    public bool executionEnabled { get; init; }
    public bool robotAdapterConfigured { get; init; }
    public bool jointCalibrationApproved { get; init; }
    public string note { get; init; } = "";
}
record Plan {
    public int schema { get; init; }
    public string upstreamRevision { get; init; } = "";
    public string checkpointRevision { get; init; } = "";
    public string backboneRevision { get; init; } = "";
    public string cuda { get; init; } = "";
    public string python { get; init; } = "";
    public string image { get; init; } = "";
    public string serverState { get; init; } = "";
    public int inferenceMinimumVramMiB { get; init; }
    public int fineTuningRecommendedVramMiB { get; init; }
    public string[] steps { get; init; } = [];
    public bool motionEnabled { get; init; }
}
record Assessment {
    public int schema { get; init; }
    public DateTimeOffset capturedAt { get; init; }
    public List<Gpu> gpus { get; init; } = [];
    public bool inferenceHardwareCandidate { get; init; }
    public bool fineTuningHardwareCandidate { get; init; }
    public bool containerRuntimeVerified { get; init; }
    public bool modelAccessVerified { get; init; }
    public bool gpuInferenceTested { get; init; }
    public bool robotCompatible { get; init; }
    public bool motionEnabled { get; init; }
}
[JsonSerializable(typeof(Connection))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(Assessment))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
partial class PreparationJson : JsonSerializerContext;
