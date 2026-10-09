// Original Xur code: MIT. Offline pixel observations; no motor or camera commands.
// dotnet run --file tools/Xur.Robotics/marker_survey.cs --artifacts-path .build/robotics/marker-survey -- DETECTOR OUTPUT CAMERA=PGM_STREAM ...
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 3)
    throw new ArgumentException("Usage: DETECTOR OUTPUT_DIRECTORY CAMERA=PGM_STREAM ...");
var detector = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (!File.Exists(detector)) throw new FileNotFoundException("Build the pinned AprilTag detector first", detector);
if (!output.Split(Path.DirectorySeparatorChar).Any(part => part is ".build" or "dist"))
    throw new ArgumentException("Keep generated survey evidence under an ignored .build/ or dist/ directory");
Directory.CreateDirectory(Path.Combine(output, "frames"));
var observations = new List<Observation>();
var cameras = new HashSet<string>(StringComparer.Ordinal);
foreach (var argument in args.Skip(2))
{
    var parts = argument.Split('=', 2);
    if (parts.Length != 2 || !Regex.IsMatch(parts[0], "^[a-zA-Z0-9_-]{1,32}$") || !cameras.Add(parts[0]))
        throw new ArgumentException("Use distinct logical camera names: head=stream.pgm, hand=stream.pgm");
    var camera = parts[0];
    if (new FileInfo(parts[1]).Length > 128 * 1024 * 1024) throw new ArgumentException("Survey stream is too large");
    var stream = await File.ReadAllBytesAsync(parts[1]);
    var offset = 0;
    var index = 0;
    while (offset < stream.Length)
    {
        if (index >= 100) throw new ArgumentException("Use at most 100 frames per camera");
        var start = offset;
        if (Token(stream, ref offset) != "P5") throw new InvalidDataException("Use native grayscale 8-bit P5 PGM frames");
        var width = int.Parse(Token(stream, ref offset), CultureInfo.InvariantCulture);
        var height = int.Parse(Token(stream, ref offset), CultureInfo.InvariantCulture);
        if (Token(stream, ref offset) != "255" || width is < 1 or > 8192 || height is < 1 or > 8192)
            throw new InvalidDataException("Invalid frame dimensions or grayscale range");
        // P5 has exactly one whitespace delimiter before binary pixels; CRLF is
        // one newline. Never skip more whitespace: a pixel can itself be 0x20.
        if (offset >= stream.Length || !White(stream[offset])) throw new InvalidDataException("Missing PGM pixel delimiter");
        var delimiter = stream[offset++];
        if (delimiter == '\r' && offset < stream.Length && stream[offset] == '\n') offset++;
        var length = checked(width * height);
        if (length > stream.Length - offset) throw new InvalidDataException("Truncated PGM frame");
        offset += length;
        var frame = Path.Combine(output, "frames", $"{camera}-{index:D3}.pgm");
        await File.WriteAllBytesAsync(frame, stream[start..offset]);
        var info = new ProcessStartInfo(detector) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(frame);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Detector failed to start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Detector rejected the frame: " + error);
        using var data = JsonDocument.Parse(await stdout);
        var root = data.RootElement;
        if (root.GetProperty("family").GetString() != "tagStandard41h12" || root.GetProperty("width").GetInt32() != width || root.GetProperty("height").GetInt32() != height)
            throw new InvalidDataException("Detector returned an unexpected family or frame dimensions");
        var tags = new List<Tag>();
        foreach (var tag in root.GetProperty("detections").EnumerateArray())
        {
            var id = tag.GetProperty("id").GetInt32();
            if (id < 0 || tag.GetProperty("hamming").GetInt32() != 0)
                throw new InvalidDataException("Require valid IDs decoded with zero corrected bits");
            var corners = tag.GetProperty("corners").EnumerateArray().Select(Point).ToArray();
            if (corners.Length != 4) throw new InvalidDataException("Expected four reference corners");
            var edges = Enumerable.Range(0, 4).Select(i => Distance(corners[i], corners[(i + 1) % 4])).ToArray();
            if (edges.Min() <= 0) throw new InvalidDataException("Degenerate marker geometry");
            tags.Add(new Tag(id, Point(tag.GetProperty("center")), edges.Min(), edges.Max(), tag.Clone()));
        }
        observations.Add(new Observation(camera, index++, width, height, tags));
    }
    if (index == 0) throw new InvalidDataException("No frames supplied for " + camera);
}

var report = Path.Combine(output, "survey.json");
using (var file = File.Create(report))
using (var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteString("analyzedAt", DateTimeOffset.UtcNow);
    json.WriteString("family", "tagStandard41h12");
    json.WriteString("detectorSha256", Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(detector))));
    json.WriteBoolean("motorCommandsIssued", false);
    json.WriteBoolean("synchronizedStereoFrames", false);
    json.WriteBoolean("metricPoseAvailable", false);
    json.WriteBoolean("jointCalibrationApproved", false);
    json.WriteString("scope", "Marker IDs, projected corner sizes and co-visibility; image pixels are not joint angles or metric poses.");
    json.WriteStartArray("frames");
    foreach (var frame in observations)
    {
        json.WriteStartObject(); json.WriteString("camera", frame.Camera); json.WriteNumber("index", frame.Index);
        json.WriteNumber("width", frame.Width); json.WriteNumber("height", frame.Height);
        json.WriteStartArray("detections");
        foreach (var tag in frame.Tags.OrderBy(t => t.Id)) tag.Raw.WriteTo(json);
        json.WriteEndArray();
        json.WriteStartArray("ambiguousDuplicateIds");
        foreach (var duplicate in frame.Tags.GroupBy(t => t.Id).Where(g => g.Count() > 1)) json.WriteNumberValue(duplicate.Key);
        json.WriteEndArray(); json.WriteEndObject();
    }
    json.WriteEndArray();
    json.WriteStartArray("cameras");
    foreach (var camera in cameras)
    {
        var frames = observations.Where(f => f.Camera == camera).ToArray();
        var unique = frames.SelectMany(f => f.Tags.GroupBy(t => t.Id).Where(g => g.Count() == 1).Select(g => g.Single())).ToArray();
        json.WriteStartObject(); json.WriteString("name", camera); json.WriteNumber("frameCount", frames.Length);
        json.WriteStartArray("markers");
        foreach (var group in unique.GroupBy(t => t.Id).OrderBy(g => g.Key))
        {
            json.WriteStartObject(); json.WriteNumber("id", group.Key); json.WriteNumber("detectedFrames", group.Count());
            json.WriteNumber("minimumReferenceEdgePixels", group.Min(t => t.MinEdge));
            json.WriteNumber("maximumReferenceEdgePixels", group.Max(t => t.MaxEdge));
            json.WriteNumber("centerSpreadPixels", Math.Max(group.Max(t => t.Center.X) - group.Min(t => t.Center.X), group.Max(t => t.Center.Y) - group.Min(t => t.Center.Y)));
            json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject();
    }
    json.WriteEndArray();
    json.WriteStartArray("sharedCameraReferences");
    var names = cameras.ToArray();
    for (var a = 0; a < names.Length; a++) for (var b = a + 1; b < names.Length; b++)
    {
        var shared = UnambiguousIds(names[a]).Intersect(UnambiguousIds(names[b])).Order().ToArray();
        json.WriteStartObject(); json.WriteString("firstCamera", names[a]); json.WriteString("secondCamera", names[b]);
        json.WriteStartArray("ids"); foreach (var id in shared) json.WriteNumberValue(id); json.WriteEndArray(); json.WriteEndObject();
    }
    json.WriteEndArray(); json.WriteEndObject();
}
Console.WriteLine(report);

IEnumerable<int> UnambiguousIds(string camera) => observations.Where(f => f.Camera == camera)
    .SelectMany(f => f.Tags.GroupBy(t => t.Id).Where(g => g.Count() == 1).Select(g => g.Key));

static bool White(byte b) => b is 9 or 10 or 11 or 12 or 13 or 32;
static string Token(byte[] data, ref int offset)
{
    while (offset < data.Length)
    {
        if (White(data[offset])) { offset++; continue; }
        if (data[offset] == '#') { while (offset < data.Length && data[offset] != '\n') offset++; continue; }
        break;
    }
    var start = offset;
    while (offset < data.Length && !White(data[offset])) offset++;
    if (start == offset || offset - start > 32) throw new InvalidDataException("Invalid PGM header token");
    return Encoding.ASCII.GetString(data, start, offset - start);
}
static Pixel Point(JsonElement value)
{
    if (value.GetArrayLength() != 2) throw new InvalidDataException("Invalid image point");
    var x = value[0].GetDouble(); var y = value[1].GetDouble();
    if (!double.IsFinite(x) || !double.IsFinite(y)) throw new InvalidDataException("Non-finite image point");
    return new Pixel(x, y);
}
static double Distance(Pixel a, Pixel b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
record Pixel(double X, double Y);
record Tag(int Id, Pixel Center, double MinEdge, double MaxEdge, JsonElement Raw);
record Observation(string Camera, int Index, int Width, int Height, List<Tag> Tags);
