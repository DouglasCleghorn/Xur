// Original Xur code: MIT. Downloaded AprilTag artwork retains its BSD-2-Clause license.
// dotnet run --file tools/Xur.Robotics/print_markers.cs --artifacts-path .build/robotics/marker-generator
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string imageRevision = "f3fd9a7add5bfd82a886fc65240fdb8e3c9ac5a1";
const string family = "tagStandard41h12";
const int referenceCells = 5; // AprilTag family width_at_border; full official artwork is 9 cells.
var output = Path.GetFullPath(args.SingleOrDefault() ?? ".build/robotics/markers/print-kit");
var cache = Path.Combine(output, "upstream");
Directory.CreateDirectory(cache);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Xur-Robotics-Marker-Printer/1.0");
using var slots = new SemaphoreSlim(4);
async Task<byte[]> Download(string relative)
{
    await slots.WaitAsync();
    try
    {
        var path = Path.Combine(cache, Path.GetFileName(relative));
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path);
        var data = await http.GetByteArrayAsync($"https://raw.githubusercontent.com/AprilRobotics/apriltag-imgs/{imageRevision}/{relative}");
        await File.WriteAllBytesAsync(path, data);
        return data;
    }
    finally { slots.Release(); }
}

var license = Encoding.UTF8.GetString(await Download("LICENSE"));
var tags = await Task.WhenAll(Enumerable.Range(0, 29).Select(async id =>
{
    var name = $"tag41_12_{id:D5}.png";
    var bytes = await Download($"{family}/{name}");
    return new Tag(id, Decode(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
}));

foreach (var paper in new[] { (Name: "letter", Width: 215.9, Height: 279.4), (Name: "a4", Width: 210.0, Height: 297.0) })
{
    var pdf = new VectorPdf(paper.Width, paper.Height);
    Sheet(0, 16, 15, 4, 44, 46, "Small link markers", "IDs 0-15 | reference 15 mm | complete pattern 27 mm");
    Sheet(16, 9, 25, 3, 61, 66, "Medium link / spare markers", "IDs 16-24 | reference 25 mm | complete pattern 45 mm");
    Sheet(25, 4, 40, 2, 94, 101, "Base / workspace markers", "IDs 25-28 | reference 40 mm | complete pattern 72 mm");
    Checkerboard();
    Instructions();
    var file = Path.Combine(output, $"xur-robotics-markers-{paper.Name}.pdf");
    pdf.Save(file);
    Console.WriteLine(file);

    void Heading(string title, string subtitle)
    {
        pdf.NewPage();
        pdf.Text(10, 13, title, 15);
        pdf.Text(10, 20, family + " | visibility test kit | print at 100% / Actual size", 9);
        pdf.Text(10, 26, subtitle, 8);
        pdf.Text(10, 32, "Matte white paper. Keep the entire pattern and white margin when cutting.", 8);
    }
    void ScaleCheck()
    {
        var y = paper.Height - 17;
        pdf.Line(12, y, 62, y, 0.3);
        for (int mm = 0; mm <= 50; mm += 5)
            pdf.Line(12 + mm, y - (mm % 10 == 0 ? 2 : 1), 12 + mm, y + 1, 0.2);
        pdf.Text(12, y - 4, "Check this line measures exactly 50 mm", 8);
        pdf.Text(10, paper.Height - 7, "AprilTag artwork: AprilRobotics, BSD-2-Clause. Full notice and instructions on page 5.", 7);
    }
    void Sheet(int start, int count, double referenceMm, int columns, double cardWidth, double cardHeight, string title, string subtitle)
    {
        Heading(title, subtitle);
        const double gap = 4;
        var x0 = (paper.Width - columns * cardWidth - (columns - 1) * gap) / 2;
        for (int i = 0; i < count; i++)
        {
            var tag = tags[start + i];
            var x = x0 + i % columns * (cardWidth + gap);
            var y = 39 + i / columns * (cardHeight + gap);
            var cell = referenceMm / referenceCells;
            var white = 11 * cell;
            var inkX = x + (cardWidth - white) / 2 + cell;
            var inkY = y + 2 + cell;
            pdf.CutBox(x, y, cardWidth, cardHeight);
            for (int row = 0; row < 9; row++)
                for (int col = 0; col < 9; col++)
                    if (tag.Black[row, col]) pdf.BlackRectangle(inkX + col * cell, inkY + row * cell, cell, cell);
            pdf.Text(x + 2, y + 2 + white + 4, $"ID {tag.Id:D2} | reference {referenceMm:0} mm", 8);
            pdf.Text(x + 2, y + 2 + white + 7, $"Pattern {9 * cell:0} mm; preserve white margin", 6.5);
        }
        ScaleCheck();
    }
    void Checkerboard()
    {
        Heading("Camera calibration target", "9 x 7 squares | 8 x 6 inner corners | square edge 20 mm");
        const double square = 20;
        var x0 = (paper.Width - 9 * square) / 2;
        const double y0 = 47;
        for (int row = 0; row < 7; row++)
            for (int col = 0; col < 9; col++)
                if ((row + col) % 2 == 0) pdf.BlackRectangle(x0 + col * square, y0 + row * square, square, square);
        pdf.Text(10, 204, "Mount the whole target flat on a rigid board. Keep its white surrounding margin.", 9);
        pdf.Text(10, 211, "Measure square edges after printing. This target calibrates camera optics, not robot joints.", 8);
        pdf.Text(10, 218, "We will capture it from several angles; hold it only while robot motion is disabled.", 8);
        ScaleCheck();
    }
    void Instructions()
    {
        pdf.NewPage();
        pdf.Text(10, 14, "Printing and measurement", 15);
        string[] lines =
        [
            "Print pages 1-4 on the paper size named in the PDF filename, at Actual size / 100%.",
            "Turn off Fit, Shrink, borderless expansion, and toner-saving settings. Use matte white paper.",
            "Before cutting, verify the 50 mm ruler and checkerboard's 20 mm squares with a real ruler.",
            "Keep the entire official pattern and the white margin inside each light dashed cut line.",
            "Do not laminate with glossy film, bend tags around motors, or obstruct joints or ventilation.",
            "Start by showing the uncut small and medium sheets to the cameras for visibility assessment.",
            "Permanent link assignments, marker-to-link transforms and mounting locations are not set yet.",
            "Use each ID once in the robot/workspace view. Additional faces need their own unique IDs.",
            "REFERENCE SIZE IS NOT THE FULL PATTERN WIDTH:",
            "For this family, pose measurement uses the 5-cell square between detection corners.",
            "The complete official image is 9 cells across. Do not crop its outer data cells.",
            "Page 1: reference 15 mm, full pattern 27 mm, pattern plus white margin 33 mm.",
            "Page 2: reference 25 mm, full pattern 45 mm, pattern plus white margin 55 mm.",
            "Page 3: reference 40 mm, full pattern 72 mm, pattern plus white margin 88 mm.",
            "Record actual printed dimensions; these are trial sizes, not proof of full joint coverage.",
            "No powered calibration is approved by printing or attaching this kit."
        ];
        for (int i = 0; i < lines.Length; i++) pdf.Text(10, 24 + 5 * i, lines[i], 8);
        pdf.Text(10, 115, "Source: github.com/AprilRobotics/apriltag-imgs", 8);
        pdf.Text(10, 121, "Pinned artwork revision: " + imageRevision, 7);
        pdf.Text(10, 132, "AprilTag artwork license (unmodified)", 10);
        var y = 141.0;
        foreach (var line in license.Replace("\r", "").Split('\n'))
        {
            pdf.Text(10, y, line, 6.6);
            y += 3.5;
        }
    }
}

using (var stream = File.Create(Path.Combine(output, "manifest.json")))
using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteString("family", family);
    json.WriteString("imageRevision", imageRevision);
    json.WriteNumber("referenceCells", referenceCells);
    json.WriteNumber("fullPatternCells", 9);
    json.WriteNumber("whiteMarginCells", 1);
    json.WriteString("generatedAt", DateTimeOffset.UtcNow);
    json.WriteString("purpose", "Stationary onboard-camera visibility trials; permanent mounts and joint calibration unapproved");
    json.WriteStartObject("checkerboard");
    json.WriteNumber("columns", 9);
    json.WriteNumber("rows", 7);
    json.WriteNumber("innerCornersColumns", 8);
    json.WriteNumber("innerCornersRows", 6);
    json.WriteNumber("squareMm", 20);
    json.WriteEndObject();
    json.WriteStartArray("tags");
    foreach (var tag in tags)
    {
        json.WriteStartObject();
        json.WriteNumber("id", tag.Id);
        json.WriteString("sha256", tag.Sha256);
        json.WriteNumber("referenceSizeMm", tag.Id < 16 ? 15 : tag.Id < 25 ? 25 : 40);
        json.WriteNumber("patternSizeMm", tag.Id < 16 ? 27 : tag.Id < 25 ? 45 : 72);
        json.WriteNull("mountingTransform");
        json.WriteEndObject();
    }
    json.WriteEndArray();
    json.WriteEndObject();
}
await File.WriteAllTextAsync(Path.Combine(output, "AprilTag-LICENSE.txt"), license);

// Separate QL-800 proofs for a 62 mm continuous roll. Integer-sized 300 dpi
// modules preserve crisp edges; these sizes deliberately differ from sheet PDFs.
// Media must be verified before printing. Never scale these files to another roll.
var labelDirectory = Path.Combine(output, "individual-ql800-62mm");
Directory.CreateDirectory(labelDirectory);
using (var stream = File.Create(Path.Combine(labelDirectory, "manifest.json")))
using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
{
    json.WriteStartObject();
    json.WriteString("family", family);
    json.WriteString("imageRevision", imageRevision);
    json.WriteString("requiredMedia", "62 mm continuous white roll; verify before printing");
    json.WriteString("printState", "prepared only; not sent to a printer");
    json.WriteNumber("dpi", 300);
    json.WriteNumber("printableWidthDots", 696);
    json.WriteStartArray("tags");
    foreach (var tag in tags)
    {
        const int width = 696;
        const double dotMm = 25.4 / 300;
        var module = tag.Id < 16 ? 36 : 60;
        var height = 11 * module + 90;
        var referenceMm = 5 * module * dotMm;
        var x0 = (width - 9 * module) / 2;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (int row = 0; row < 9; row++)
            for (int col = 0; col < 9; col++)
                if (tag.Black[row, col])
                    for (int dy = 0; dy < module; dy++)
                        Array.Fill(pixels, (byte)0, (module + row * module + dy) * width + x0 + col * module, module);
        // Readable numeric ID below the complete white margin, without a font dependency.
        string[] digitRows = ["111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
                             "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111"];
        var digits = tag.Id.ToString("D2", CultureInfo.InvariantCulture);
        for (int digit = 0; digit < digits.Length; digit++)
            for (int cell = 0; cell < 15; cell++)
                if (digitRows[digits[digit] - '0'][cell] == '1')
                    for (int dy = 0; dy < 6; dy++)
                        Array.Fill(pixels, (byte)0, (11 * module + 12 + cell / 3 * 6 + dy) * width + 24 + digit * 24 + cell % 3 * 6, 6);
        var stem = $"tag-{tag.Id:D2}";
        using (var pgm = File.Create(Path.Combine(labelDirectory, stem + ".pgm")))
        {
            pgm.Write(Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n"));
            pgm.Write(pixels);
        }
        var proof = new VectorPdf(62, height * dotMm);
        proof.NewPage();
        var marginMm = (62 - width * dotMm) / 2;
        for (int row = 0; row < 9; row++)
            for (int col = 0; col < 9; col++)
                if (tag.Black[row, col])
                    proof.BlackRectangle(marginMm + (x0 + col * module) * dotMm, (module + row * module) * dotMm, module * dotMm, module * dotMm);
        proof.Text(3, (11 * module + 48) * dotMm, $"ID {tag.Id:D2} | reference {referenceMm:0.00} mm", 8);
        proof.Text(3, (11 * module + 78) * dotMm, "300 dpi | 62 mm continuous roll | Actual size", 6);
        proof.Save(Path.Combine(labelDirectory, stem + ".pdf"));
        json.WriteStartObject();
        json.WriteNumber("id", tag.Id);
        json.WriteString("rasterFile", stem + ".pgm");
        json.WriteString("proofFile", stem + ".pdf");
        json.WriteNumber("moduleDots", module);
        json.WriteNumber("referenceSizeMm", referenceMm);
        json.WriteNumber("patternSizeMm", 9 * module * dotMm);
        json.WriteNumber("widthDots", width);
        json.WriteNumber("heightDots", height);
        json.WriteNull("mountingTransform");
        json.WriteEndObject();
    }
    json.WriteEndArray();
    json.WriteEndObject();
}
await File.WriteAllTextAsync(Path.Combine(labelDirectory, "AprilTag-LICENSE.txt"), license);
Console.WriteLine(labelDirectory);

static bool[,] Decode(byte[] bytes)
{
    if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        throw new InvalidDataException("Expected an official PNG tag image");
    using var compressed = new MemoryStream();
    var headerFound = false;
    for (int position = 8; position + 12 <= bytes.Length;)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4)));
        if (length < 0 || length > bytes.Length - position - 12) throw new InvalidDataException("Truncated PNG");
        var kind = Encoding.ASCII.GetString(bytes, position + 4, 4);
        var data = bytes.AsSpan(position + 8, length);
        if (kind == "IHDR")
        {
            if (length != 13 || BinaryPrimitives.ReadInt32BigEndian(data) != 9 ||
                BinaryPrimitives.ReadInt32BigEndian(data[4..]) != 9 || data[8] != 8 || data[9] != 6 ||
                data[10] != 0 || data[11] != 0 || data[12] != 0)
                throw new InvalidDataException("Expected pinned 9x9, 8-bit RGBA, noninterlaced artwork");
            headerFound = true;
        }
        if (kind == "IDAT") compressed.Write(data);
        position += length + 12;
        if (kind == "IEND") break;
    }
    if (!headerFound) throw new InvalidDataException("Missing PNG header");
    compressed.Position = 0;
    using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
    byte[] raw = new byte[9 * (9 * 4 + 1)];
    zlib.ReadExactly(raw);
    if (zlib.ReadByte() != -1) throw new InvalidDataException("Unexpected PNG data length");
    byte[] pixels = new byte[9 * 9 * 4];
    for (int row = 0; row < 9; row++)
    {
        var filter = raw[row * 37];
        for (int col = 0; col < 36; col++)
        {
            var index = row * 36 + col;
            int left = col >= 4 ? pixels[index - 4] : 0;
            int above = row > 0 ? pixels[index - 36] : 0;
            int diagonal = row > 0 && col >= 4 ? pixels[index - 40] : 0;
            var correction = filter switch
            {
                0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2,
                4 => Paeth(left, above, diagonal), _ => throw new InvalidDataException("Invalid PNG filter")
            };
            pixels[index] = unchecked((byte)(raw[row * 37 + 1 + col] + correction));
        }
    }
    var result = new bool[9, 9];
    for (int row = 0; row < 9; row++)
        for (int col = 0; col < 9; col++)
        {
            var index = (row * 9 + col) * 4;
            var value = pixels[index];
            if (value is not (0 or 255) || pixels[index + 1] != value || pixels[index + 2] != value || pixels[index + 3] != 255)
                throw new InvalidDataException("Expected opaque monochrome official artwork");
            result[row, col] = value == 0;
        }
    return result;
}
static int Paeth(int left, int above, int diagonal)
{
    var estimate = left + above - diagonal;
    var a = Math.Abs(estimate - left);
    var b = Math.Abs(estimate - above);
    var c = Math.Abs(estimate - diagonal);
    return a <= b && a <= c ? left : b <= c ? above : diagonal;
}

sealed record Tag(int Id, bool[,] Black, string Sha256);

// Small vector-only PDF writer: exact page dimensions and rectangles, no image resampling.
sealed class VectorPdf(double widthMm, double heightMm)
{
    const double PointsPerMm = 72 / 25.4;
    readonly List<StringBuilder> pages = [];
    StringBuilder Current => pages[^1];
    static string Number(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
    static string Point(double mm) => Number(mm * PointsPerMm);
    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    public void NewPage() => pages.Add(new StringBuilder());
    public void Text(double x, double baselineY, string value, double points) =>
        Current.AppendLine($"BT /F1 {Number(points)} Tf {Point(x)} {Point(heightMm - baselineY)} Td ({Escape(value)}) Tj ET");
    public void BlackRectangle(double x, double y, double width, double height) =>
        Current.AppendLine($"0 g {Point(x)} {Point(heightMm - y - height)} {Point(width)} {Point(height)} re f");
    public void Line(double x1, double y1, double x2, double y2, double width) =>
        Current.AppendLine($"0 G {Point(width)} w {Point(x1)} {Point(heightMm - y1)} m {Point(x2)} {Point(heightMm - y2)} l S");
    public void CutBox(double x, double y, double width, double height) =>
        Current.AppendLine($"q 0.8 G 0.4 w [2 3] 0 d {Point(x)} {Point(heightMm - y - height)} {Point(width)} {Point(height)} re S Q");
    public void Save(string file)
    {
        var objects = new List<string>();
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{4 + 2 * i} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (int i = 0; i < pages.Count; i++)
        {
            var content = pages[i].ToString();
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Point(widthMm)} {Point(heightMm)}] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + 2 * i} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        using var stream = File.Create(file);
        void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
