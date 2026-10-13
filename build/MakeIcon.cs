#:package Magick.NET-Q16-AnyCPU@15.0.0
// Draws the app's logo and writes it in every size and place it is used.
//   dotnet run build/MakeIcon.cs
//
//   FileConverter/Assets/app.ico     the Windows programs and the window (16 … 256 px)
//   build/linux/icons/<size>.png     the Linux applications menu, plus scalable.svg
//   docs/logo.svg                    the README
//
// One drawing, described once in Design() below. The small sizes are not the large picture
// shrunk: they get a heavier line and no margin, and every line lies on whole pixels, so that
// 16 px stays sharp. The window's header draws the same shapes (LogoArrow… in
// FileConverter/Themes/Palette.axaml — this program prints them).

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ImageMagick;
using ImageMagick.Drawing;

const string TileTop = "#5E9BFF", TileBottom = "#2E60DC";
const double SecondArrow = 0.8; // the arrow coming back is a little lighter than the one going out

var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
var name = Regex.Match(File.ReadAllText(Path.Combine(root, "Directory.Build.props")), "<FcDisplayName>(.*?)</FcDisplayName>").Groups[1].Value;

// Windows: one .ico. Up to 48 px as plain bitmaps, the way every part of Windows reads them;
// larger ones compressed, which is what keeps the file at a few kilobytes.
var windows = new List<(int Side, byte[] Data)>();
foreach (int side in new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 })
{
    using var picture = Render(side);
    windows.Add((side, side <= 48 ? IconBitmap(picture) : Png(picture)));
}
Save(Path.Combine(root, "FileConverter", "Assets", "app.ico"), Ico(windows));

// Linux: the sizes of the "hicolor" icon theme, and the drawing itself for desktops that scale it
foreach (int side in new[] { 16, 22, 24, 32, 48, 64, 128, 256, 512 })
{
    using var picture = Render(side);
    Save(Path.Combine(root, "build", "linux", "icons", side + ".png"), Png(picture));
}
var drawing = Encoding.UTF8.GetBytes(Svg(Design(256), name));
Save(Path.Combine(root, "build", "linux", "icons", "scalable.svg"), drawing);
Save(Path.Combine(root, "docs", "logo.svg"), drawing);

var header = Design(32);
Console.WriteLine($"For Palette.axaml (drawn {header.Side} wide, line {N(header.Stroke)}):");
Console.WriteLine("  LogoArrowOut   " + ArrowOut(header));
Console.WriteLine("  LogoArrowBack  " + ArrowBack(header));
return;

static string ThisFile([CallerFilePath] string path = "") => path;

static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

static void Save(string path, byte[] data)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, data);
    Console.WriteLine($"{data.Length,8:N0}  {path}");
}

// What is drawn, measured in pixels of a picture `side` wide.
static Shape Design(int side)
{
    // shares of the side: margin around the tile, its corner radius, the line's weight, half an
    // arrow's length, how far each arrow is from the middle, the size of an arrowhead
    var (inset, radius, stroke, half, apart, head) = side switch
    {
        <= 24 => (0.0, 0.19, 0.125, 0.25, 0.1875, 0.14),
        <= 48 => (0.03, 0.2, 0.105, 0.245, 0.165, 0.13),
        _ => (0.04, 0.215, 0.09, 0.24, 0.15, 0.125)
    };
    double s = side;
    double weight = Math.Max(2, Math.Round(stroke * s));
    // a line is sharp when both its edges fall between two pixels
    double OnPixels(double centre) => Math.Round(centre - weight / 2) + weight / 2;
    double top = OnPixels(s / 2 - apart * s);
    return new Shape(side, Math.Round(inset * s), radius * s, weight,
        Math.Round(s / 2 - half * s), Math.Round(s / 2 + half * s), top, s - top, head * s);
}

static string ArrowOut(Shape d) =>
    $"M{N(d.Left)},{N(d.TopLine)} H{N(d.Right)} " +
    $"M{N(d.Right - d.Head)},{N(d.TopLine - d.Head)} L{N(d.Right)},{N(d.TopLine)} L{N(d.Right - d.Head)},{N(d.TopLine + d.Head)}";

static string ArrowBack(Shape d) =>
    $"M{N(d.Right)},{N(d.BottomLine)} H{N(d.Left)} " +
    $"M{N(d.Left + d.Head)},{N(d.BottomLine - d.Head)} L{N(d.Left)},{N(d.BottomLine)} L{N(d.Left + d.Head)},{N(d.BottomLine + d.Head)}";

static string Svg(Shape d, string title) => $"""
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {d.Side} {d.Side}" width="{d.Side}" height="{d.Side}">
      <title>{title}</title>
      <defs>
        <linearGradient id="tile" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stop-color="{TileTop}"/>
          <stop offset="1" stop-color="{TileBottom}"/>
        </linearGradient>
      </defs>
      <rect x="{N(d.Inset)}" y="{N(d.Inset)}" width="{N(d.Side - 2 * d.Inset)}" height="{N(d.Side - 2 * d.Inset)}" rx="{N(d.Radius)}" fill="url(#tile)"/>
      <g fill="none" stroke="#fff" stroke-width="{N(d.Stroke)}" stroke-linecap="round" stroke-linejoin="round">
        <path d="{ArrowOut(d)}"/>
        <path d="{ArrowBack(d)}" opacity="{N(SecondArrow)}"/>
      </g>
    </svg>

    """.ReplaceLineEndings("\n");

static MagickImage Render(int side)
{
    var d = Design(side);
    const int over = 8; // drawn eight times too large and averaged down: that is what smooths the edges
    uint big = (uint)(side * over);
    // ImageMagick counts from the middle of the first pixel, the design from its edge
    double At(double value) => value * over - 0.5;

    var tile = new MagickImage($"gradient:{TileTop}-{TileBottom}", new MagickReadSettings { Width = big, Height = big });
    using (var mask = new MagickImage(MagickColors.Black, big, big))
    {
        new Drawables()
            .FillColor(MagickColors.White)
            .RoundRectangle(At(d.Inset), At(d.Inset), At(d.Side - d.Inset), At(d.Side - d.Inset), d.Radius * over, d.Radius * over)
            .Draw(mask);
        mask.Alpha(AlphaAction.Off);
        tile.Alpha(AlphaAction.Set);
        tile.Composite(mask, CompositeOperator.CopyAlpha);
    }

    IDrawables<ushort> Pen() => new Drawables()
        .StrokeColor(MagickColors.White)
        .StrokeWidth(d.Stroke * over)
        .StrokeLineCap(LineCap.Round)
        .StrokeLineJoin(LineJoin.Round)
        .FillColor(MagickColors.Transparent);

    Pen()
        .Line(At(d.Left), At(d.TopLine), At(d.Right), At(d.TopLine))
        .Polyline(new PointD(At(d.Right - d.Head), At(d.TopLine - d.Head)), new PointD(At(d.Right), At(d.TopLine)),
            new PointD(At(d.Right - d.Head), At(d.TopLine + d.Head)))
        .Draw(tile);

    // on a sheet of its own: made lighter as one shape, its line and its head do not show through each other
    using (var back = new MagickImage(MagickColors.Transparent, big, big))
    {
        Pen()
            .Line(At(d.Right), At(d.BottomLine), At(d.Left), At(d.BottomLine))
            .Polyline(new PointD(At(d.Left + d.Head), At(d.BottomLine - d.Head)), new PointD(At(d.Left), At(d.BottomLine)),
                new PointD(At(d.Left + d.Head), At(d.BottomLine + d.Head)))
            .Draw(back);
        back.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, SecondArrow);
        tile.Composite(back, CompositeOperator.Over);
    }

    tile.Scale((uint)side, (uint)side);
    return tile;
}

static byte[] Png(MagickImage picture)
{
    picture.Strip();
    picture.Quality = 95;
    // no dates inside: the same drawing gives the same file every time
    picture.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
    return picture.ToByteArray(MagickFormat.Png32);
}

// A picture as an .ico stores it uncompressed: a bitmap header, the rows from the bottom up,
// then one bit per pixel saying where the picture is see-through.
static byte[] IconBitmap(MagickImage picture)
{
    int side = (int)picture.Width;
    using var pixels = picture.GetPixels();
    var bgra = pixels.ToByteArray(PixelMapping.BGRA)!;
    int maskRow = (side + 31) / 32 * 4;

    using var data = new MemoryStream();
    using var writer = new BinaryWriter(data);
    writer.Write(40);
    writer.Write(side);
    writer.Write(side * 2); // the picture and its mask
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);
    writer.Write(side * side * 4 + maskRow * side);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    for (int y = side - 1; y >= 0; y--)
        writer.Write(bgra, y * side * 4, side * 4);
    var mask = new byte[maskRow];
    for (int y = side - 1; y >= 0; y--)
    {
        Array.Clear(mask);
        for (int x = 0; x < side; x++)
            if (bgra[(y * side + x) * 4 + 3] == 0)
                mask[x / 8] |= (byte)(0x80 >> (x % 8));
        writer.Write(mask);
    }
    writer.Flush();
    return data.ToArray();
}

static byte[] Ico(IReadOnlyList<(int Side, byte[] Data)> pictures)
{
    using var file = new MemoryStream();
    using var writer = new BinaryWriter(file);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)pictures.Count);
    int offset = 6 + 16 * pictures.Count;
    foreach (var (side, data) in pictures)
    {
        writer.Write((byte)(side >= 256 ? 0 : side)); // 0 stands for 256
        writer.Write((byte)(side >= 256 ? 0 : side));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(data.Length);
        writer.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in pictures)
        writer.Write(data);
    writer.Flush();
    return file.ToArray();
}

record Shape(int Side, double Inset, double Radius, double Stroke, double Left, double Right, double TopLine, double BottomLine, double Head);
