using System.Text;
using SkiaSharp;

// Regenerates src/JustPlay.Stream/Assets/juststream.ico — the JUST STREAM brand mark in the DEFAULT
// (Aurora) theme: a cyan->pink rounded chip with the WHITE radio-tower / Funkturm glyph, the same mark
// ThemedWindowIcon renders live at runtime. Uses the REAL glyph geometry from
// JustPlay.UI.Theming.BrandGlyphs.RadioTower (Lucide "radio-tower") — kept verbatim below, not invented.
// Re-run only if the STREAM brand mark changes: dotnet run --project build/make-stream-icon -c Release

// BrandGlyphs.RadioTower.PathData (verbatim). Stroked line-art; thickness 0.04, box 0.52 of the icon.
const string pathData =
    "M4.9 16.1 C1 12.2 1 5.8 4.9 1.9 " +
    "M7.8 4.7 a6.14 6.14 0 0 0 -.8 7.5 " +
    "M10 9 A2 2 0 1 0 14 9 A2 2 0 1 0 10 9 " +
    "M16.2 4.8 c2 2 2.26 5.11 .8 7.47 " +
    "M19.1 1.9 a9.96 9.96 0 0 1 0 14.1 " +
    "M9.5 18 h5 " +
    "M8 22 l4 -11 l4 11";
const double boxRatio = 0.52;
const double thickness = 0.04;

var outPath = args.Length > 0 ? args[0] : Path.Combine(FindRepoRoot(), "src", "JustPlay.Stream", "Assets", "juststream.ico");
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
// Aurora accents (App.axaml: AccentA cyan #6CE0FF -> AccentB pink #FF5FAE).
var cyan = new SKColor(0x6C, 0xE0, 0xFF);
var pink = new SKColor(0xFF, 0x5F, 0xAE);

var pngs = new Dictionary<int, byte[]>();
foreach (var s in sizes) pngs[s] = RenderPng(s);

WriteIco(outPath, sizes, pngs);
Console.WriteLine($"Wrote {outPath} ({new FileInfo(outPath).Length / 1024.0:F1} KB, sizes: {string.Join(", ", sizes)})");

byte[] RenderPng(int size)
{
    using var bmp = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var canvas = new SKCanvas(bmp);
    canvas.Clear(SKColors.Transparent);

    // Rounded-rect chip with the Aurora diagonal gradient (corner radius 0.25*size, like ThemedWindowIcon).
    using var chip = new SKPaint { IsAntialias = true };
    chip.Shader = SKShader.CreateLinearGradient(
        new SKPoint(0, 0), new SKPoint(size, size),
        new[] { cyan, pink }, null, SKShaderTileMode.Clamp);
    canvas.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, size, size), size * 0.25f), chip);

    // The real radio-tower path, scaled Uniform to a centred box (0.52*size), stroked white.
    using var path = SKPath.ParseSvgPathData(pathData)
        ?? throw new InvalidOperationException("Failed to parse RadioTower path data.");
    var b = path.TightBounds;
    float scale = (float)(size * boxRatio) / Math.Max(b.Width, b.Height);
    float tx = size / 2f - b.MidX * scale;
    float ty = size / 2f - b.MidY * scale;
    path.Transform(new SKMatrix(scale, 0, tx, 0, scale, ty, 0, 0, 1));

    using var glyph = new SKPaint
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        Color = SKColors.White,
        StrokeWidth = (float)(size * thickness),
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };
    // Soft drop shadow like the in-app mark (subtle; a file icon doesn't need much).
    glyph.ImageFilter = SKImageFilter.CreateDropShadow(
        0, size * 0.008f, size * 0.016f, size * 0.016f, new SKColor(0, 0, 0, 128));
    canvas.DrawPath(path, glyph);
    canvas.Flush();

    using var img = SKImage.FromBitmap(bmp);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// Assemble a PNG-compressed .ico (ICONDIR + entries + PNG blobs) — Vista+/Win10 read PNG frames.
static void WriteIco(string outPath, int[] sizes, Dictionary<int, byte[]> pngs)
{
    using var fs = File.Create(outPath);
    using var bw = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
    bw.Write((ushort)0);            // reserved
    bw.Write((ushort)1);            // type: 1 = icon
    bw.Write((ushort)sizes.Length); // image count

    int offset = 6 + 16 * sizes.Length;
    foreach (var s in sizes)
    {
        var data = pngs[s];
        bw.Write((byte)(s >= 256 ? 0 : s)); // width  (0 => 256)
        bw.Write((byte)(s >= 256 ? 0 : s)); // height
        bw.Write((byte)0);                  // palette
        bw.Write((byte)0);                  // reserved
        bw.Write((ushort)1);                // colour planes
        bw.Write((ushort)32);               // bpp
        bw.Write((uint)data.Length);
        bw.Write((uint)offset);
        offset += data.Length;
    }
    foreach (var s in sizes) bw.Write(pngs[s]);
}

// Walk up from the executable to the repo root (the folder holding JustPlay.slnx).
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JustPlay.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("JustPlay.slnx not found above the tool.");
}
