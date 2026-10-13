using System.Text;
using ImageMagick;
using SkiaSharp;

namespace FileConverter.Core.Documents;

/// <summary>
/// A text document as what it says rather than how it looks: headings, paragraphs, lists,
/// tables and pictures. Every format the built-in converter reads is brought into this form and
/// every format it writes is made from it, so a format added on either side works with all the
/// others at once.
/// </summary>
internal sealed class RichDocument
{
    public string? Title { get; set; }
    public string? Author { get; set; }

    /// <summary>The language as a tag such as "en" or "tr", when the source says.</summary>
    public string? Language { get; set; }

    public List<Block> Blocks { get; } = [];

    /// <summary>Every picture in the document, wherever it sits.</summary>
    public IEnumerable<Picture> Pictures => Flatten(Blocks).OfType<Picture>();

    public bool IsEmpty => !Flatten(Blocks).Any(block => block is Picture or CodeBlock ||
        (block is Paragraph paragraph && paragraph.Inlines.Any(i => i is TextRun { Text.Length: > 0 })) ||
        (block is Heading heading && heading.Inlines.Count > 0));

    internal static IEnumerable<Block> Flatten(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var inner = block switch
            {
                ListBlock list => list.Items.SelectMany(item => item),
                Quote quote => quote.Blocks,
                Table table => table.Rows.SelectMany(row => row.SelectMany(cell => cell.Blocks)),
                _ => []
            };
            foreach (var nested in Flatten(inner)) yield return nested;
        }
    }

    /// <summary>The words of a run of text without their formatting.</summary>
    public static string PlainText(IEnumerable<Inline> inlines)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is TextRun run) text.Append(run.Text);
            else if (inline is LineBreak) text.Append(' ');
        }
        return text.ToString();
    }
}

internal abstract class Block;

internal enum TextAlign
{
    Start,
    Center,
    End,
    Justify
}

internal sealed class Paragraph(List<Inline> inlines) : Block
{
    public List<Inline> Inlines { get; } = inlines;
    public TextAlign Align { get; init; }

    public Paragraph(string text) : this([new TextRun(text)]) { }
}

/// <summary>A heading, level 1 (the largest) to 6.</summary>
internal sealed class Heading(int level, List<Inline> inlines) : Block
{
    public int Level { get; } = Math.Clamp(level, 1, 6);
    public List<Inline> Inlines { get; } = inlines;
}

/// <summary>A bulleted or numbered list; every item is a run of blocks, so lists can nest.</summary>
internal sealed class ListBlock(bool ordered) : Block
{
    public bool Ordered { get; } = ordered;
    public int Start { get; init; } = 1;
    public List<List<Block>> Items { get; } = [];
}

/// <summary>Text kept exactly as it is, in a fixed-width font.</summary>
internal sealed class CodeBlock(string text) : Block
{
    public string Text { get; } = text;
}

internal sealed class Quote(List<Block> blocks) : Block
{
    public List<Block> Blocks { get; } = blocks;
}

internal sealed class TableCell(List<Block> blocks)
{
    public List<Block> Blocks { get; } = blocks;
    public int ColumnSpan { get; init; } = 1;
}

internal sealed class Table : Block
{
    public List<List<TableCell>> Rows { get; } = [];

    /// <summary>How many rows at the top are headings.</summary>
    public int HeaderRows { get; set; }

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(row => row.Sum(cell => Math.Max(cell.ColumnSpan, 1)));
}

/// <summary>Starts a new page in the formats that have pages.</summary>
internal sealed class PageBreak : Block;

/// <summary>A horizontal line between two parts of the text.</summary>
internal sealed class Rule : Block;

/// <summary>
/// A picture, always as PNG, JPEG or GIF data — what every format written here can hold.
/// Anything else a source carries (TIFF, BMP, WebP, a Windows metafile, SVG) is turned into a
/// PNG when it is read; a picture that cannot be read at all is left out.
/// </summary>
internal sealed class Picture : Block
{
    public required byte[] Data { get; init; }

    /// <summary>"png", "jpg" or "gif".</summary>
    public required string Format { get; init; }
    public required int PixelWidth { get; init; }
    public required int PixelHeight { get; init; }
    public string Alt { get; init; } = "";

    /// <summary>The size the source shows the picture at, in points, when it says.</summary>
    public double? WidthPt { get; init; }
    public double? HeightPt { get; init; }

    public string Mime => Format switch { "jpg" => "image/jpeg", "gif" => "image/gif", _ => "image/png" };

    /// <summary>
    /// The size to show the picture at: what the source says, else its pixels at 96 per inch,
    /// scaled down in proportion to fit <paramref name="maxWidthPt"/> by <paramref name="maxHeightPt"/>.
    /// </summary>
    public (double Width, double Height) DisplaySize(double maxWidthPt, double maxHeightPt = double.MaxValue)
    {
        double width = WidthPt ?? PixelWidth * 0.75, height = HeightPt ?? PixelHeight * 0.75;
        // one side given: the other follows from the picture's proportions
        if (WidthPt != null && HeightPt == null) height = width * PixelHeight / Math.Max(PixelWidth, 1);
        if (HeightPt != null && WidthPt == null) width = height * PixelWidth / Math.Max(PixelHeight, 1);
        double fit = Math.Min(1.0, Math.Min(maxWidthPt / Math.Max(width, 0.1), maxHeightPt / Math.Max(height, 0.1)));
        return (Math.Max(width * fit, 1), Math.Max(height * fit, 1));
    }

    /// <summary>Reads picture data of any kind; null when nothing here can decode it.</summary>
    public static Picture? Load(byte[]? data, string alt = "", double? widthPt = null, double? heightPt = null)
    {
        if (data is not { Length: > 16 }) return null;
        try
        {
            string? format = Sniff(data);
            if (format != null)
            {
                using var codec = SKCodec.Create(new MemoryStream(data));
                // a print-colour (CMYK) JPEG is one the other formats' readers show in wrong colours
                bool plain = codec != null && codec.Info.Width > 0 && codec.Info.Height > 0 &&
                             !(format == "jpg" && codec.EncodedFormat == SKEncodedImageFormat.Jpeg && IsFourColourJpeg(data));
                if (plain)
                {
                    var (width, height) = codec!.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                        or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom
                        ? (codec.Info.Height, codec.Info.Width)
                        : (codec.Info.Width, codec.Info.Height);
                    return new Picture
                    {
                        Data = data, Format = format, PixelWidth = width, PixelHeight = height,
                        Alt = alt, WidthPt = widthPt, HeightPt = heightPt
                    };
                }
            }

            // everything else goes through ImageMagick once and is kept as a PNG
            Converters.ImageEngine.EnsureInitialized();
            var settings = new MagickReadSettings();
            if (LooksLikeSvg(data))
            {
                settings.Format = MagickFormat.Svg;
                settings.Density = new Density(192);
                settings.BackgroundColor = MagickColors.Transparent;
            }
            using var image = new MagickImage(data, settings);
            image.AutoOrient();
            if (image.ColorSpace == ColorSpace.CMYK) image.ColorSpace = ColorSpace.sRGB;
            image.Depth = 8;
            image.Strip();
            var png = image.ToByteArray(MagickFormat.Png);
            bool vector = settings.Format == MagickFormat.Svg;
            return new Picture
            {
                Data = png, Format = "png", PixelWidth = (int)image.Width, PixelHeight = (int)image.Height, Alt = alt,
                // drawn at twice its size so that it stays sharp: shown at the size it was meant to have
                WidthPt = widthPt ?? (vector ? image.Width * 0.375 : null),
                HeightPt = heightPt ?? (vector ? image.Height * 0.375 : null)
            };
        }
        catch (Exception ex) when (ex is MagickException or ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "jpg";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "gif";
        return null;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        var head = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]);
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Does the JPEG's frame header name four colour channels?</summary>
    private static bool IsFourColourJpeg(ReadOnlySpan<byte> data)
    {
        int at = 2;
        while (at + 9 < data.Length && data[at] == 0xFF)
        {
            int marker = data[at + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2) return data[at + 9] == 4;
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { at += 2; continue; }
            at += 2 + ((data[at + 2] << 8) | data[at + 3]);
        }
        return false;
    }
}

internal abstract class Inline;

[Flags]
internal enum TextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Strike = 8,
    /// <summary>Fixed-width, as for a piece of program text.</summary>
    Code = 16,
    Superscript = 32,
    Subscript = 64
}

internal sealed class TextRun(string text, TextStyle style = TextStyle.None, string? link = null) : Inline
{
    public string Text { get; set; } = text;
    public TextStyle Style { get; } = style;

    /// <summary>The address the text links to, if it is a link.</summary>
    public string? Link { get; } = link;

    public bool Has(TextStyle style) => (Style & style) != 0;
}

/// <summary>A new line inside a paragraph.</summary>
internal sealed class LineBreak : Inline;

/// <summary>
/// Collects what a reader comes across — text with its formatting, pictures, and the blocks they
/// add up to — and takes care of the things every reader would otherwise do itself: runs of the
/// same formatting are joined, a paragraph of nothing but spaces is dropped, and a picture in the
/// middle of a paragraph divides it in two.
/// </summary>
internal sealed class BlockBuilder
{
    private List<Inline> _inlines = [];

    public List<Block> Blocks { get; } = [];

    /// <summary>The paragraph being written has nothing in it yet (or only ends in a space).</summary>
    public bool AtLineStart => _inlines.Count == 0 || _inlines[^1] is LineBreak;

    public bool EndsWithSpace => _inlines.Count > 0 && _inlines[^1] is TextRun { Text.Length: > 0 } run && char.IsWhiteSpace(run.Text[^1]);

    public void Text(string text, TextStyle style = TextStyle.None, string? link = null)
    {
        if (text.Length == 0) return;
        if (_inlines.Count > 0 && _inlines[^1] is TextRun last && last.Style == style && last.Link == link)
            last.Text += text;
        else
            _inlines.Add(new TextRun(text, style, link));
    }

    public void Break() => _inlines.Add(new LineBreak());

    /// <summary>Ends the paragraph being written, if there is one.</summary>
    public void EndParagraph(TextAlign align = TextAlign.Start)
    {
        var inlines = TakeInlines();
        if (inlines.Count > 0) Blocks.Add(new Paragraph(inlines) { Align = align });
    }

    public void EndHeading(int level)
    {
        var inlines = TakeInlines();
        if (inlines.Count > 0) Blocks.Add(new Heading(level, inlines));
    }

    /// <summary>A block of its own: whatever text came before it becomes a paragraph first.</summary>
    public void Add(Block? block)
    {
        if (block == null) return;
        EndParagraph();
        Blocks.Add(block);
    }

    public void AddRange(IEnumerable<Block> blocks)
    {
        EndParagraph();
        Blocks.AddRange(blocks);
    }

    /// <summary>The text collected so far, tidied, as the content of a block; the builder starts afresh.</summary>
    public List<Inline> TakeInlines()
    {
        var inlines = _inlines;
        _inlines = [];

        // spaces and line breaks at either end carry nothing
        while (inlines.Count > 0 && IsBlank(inlines[0])) inlines.RemoveAt(0);
        while (inlines.Count > 0 && IsBlank(inlines[^1])) inlines.RemoveAt(inlines.Count - 1);
        if (inlines.Count > 0 && inlines[0] is TextRun first) first.Text = first.Text.TrimStart(' ', '\t', '\r', '\n');
        if (inlines.Count > 0 && inlines[^1] is TextRun last) last.Text = last.Text.TrimEnd(' ', '\t', '\r', '\n');
        return inlines;
    }

    private static bool IsBlank(Inline inline) => inline is LineBreak || inline is TextRun run && string.IsNullOrWhiteSpace(run.Text);
}
