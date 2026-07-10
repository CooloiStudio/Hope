using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 从 hope.png（≥256）与 hope-mini.png（<256）生成多尺寸 hope.ico。
var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var fullPng = args.Length > 0 ? args[0] : Path.Combine(repoRoot, "src", "resources", "hope.png");
var miniPng = args.Length > 1 ? args[1] : Path.Combine(repoRoot, "src", "resources", "hope-mini.png");
var icoPath = args.Length > 2 ? args[2] : Path.Combine(repoRoot, "src", "resources", "hope.ico");
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

if (!File.Exists(fullPng))
{
    Console.Error.WriteLine($"PNG not found: {fullPng}");
    return 1;
}
if (!File.Exists(miniPng))
{
    Console.Error.WriteLine($"PNG not found: {miniPng}");
    return 1;
}

using var fullSource = new Bitmap(fullPng);
using var miniSource = new Bitmap(miniPng);
var pngParts = new List<byte[]>();
foreach (var size in sizes)
{
    var source = size >= 256 ? fullSource : miniSource;
    using var resized = ResizeHighQuality(source, size, size);
    using var ms = new MemoryStream();
    resized.Save(ms, ImageFormat.Png);
    pngParts.Add(ms.ToArray());
}

var bytes = BuildIco(pngParts, sizes);
File.WriteAllBytes(icoPath, bytes);
Console.WriteLine($"Wrote {icoPath} ({bytes.Length:N0} bytes, {sizes.Length} sizes; <256=mini, >=256=full)");
return 0;

static Bitmap ResizeHighQuality(Bitmap source, int width, int height)
{
    var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(result);
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.SmoothingMode = SmoothingMode.HighQuality;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.CompositingQuality = CompositingQuality.HighQuality;
    g.Clear(Color.Transparent);
    g.DrawImage(source, new Rectangle(0, 0, width, height));
    return result;
}

static byte[] BuildIco(List<byte[]> pngParts, int[] sizes)
{
    int count = pngParts.Count;
    int headerSize = 6 + count * 16;
    int offset = headerSize;
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    bw.Write((ushort)0);
    bw.Write((ushort)1);
    bw.Write((ushort)count);

    for (int i = 0; i < count; i++)
    {
        int s = sizes[i];
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)(s >= 256 ? 0 : s));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(pngParts[i].Length);
        bw.Write(offset);
        offset += pngParts[i].Length;
    }

    foreach (var part in pngParts)
        bw.Write(part);

    return ms.ToArray();
}
