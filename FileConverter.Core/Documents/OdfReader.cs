using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace FileConverter.Core.Documents;

/// <summary>The XML names OpenDocument files (.odt, .ods, .odp, .odg) are written in.</summary>
internal static class Odf
{
    public static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    public static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    public static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    public static readonly XNamespace Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    public static readonly XNamespace Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    public static readonly XNamespace Meta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    public static readonly XNamespace Manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    public static readonly XNamespace Number = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
    public static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    /// <summary>Opens an OpenDocument file; a password-protected or damaged one is an error the user can act on.</summary>
    public static ZipArchive Open(string path)
    {
        try
        {
            var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("content.xml") == null)
            {
                zip.Dispose();
                throw new FriendlyException("This file is not an OpenDocument file.");
            }
            // an encrypted file says so in its list of contents
            if (ReadXml(zip, "META-INF/manifest.xml")?.Descendants(Manifest + "encryption-data").Any() == true)
            {
                zip.Dispose();
                throw new FriendlyException("This document is protected by a password. Remove the password first, then convert it.");
            }
            return zip;
        }
        catch (InvalidDataException)
        {
            throw new FriendlyException("This file is not an OpenDocument file, or it is damaged.");
        }
    }

    public static XDocument? ReadXml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return null;
        try
        {
            using var stream = entry.Open();
            // no document type is ever fetched or expanded
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static byte[]? ReadBytes(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? zip.GetEntry(Uri.UnescapeDataString(name));
        if (entry == null || entry.Length > 256 << 20) return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>A length such as "2.54cm", "1in" or "12pt", in points; null when it is none of those.</summary>
    public static double? Points(string? length)
    {
        if (string.IsNullOrWhiteSpace(length)) return null;
        length = length.Trim();
        (string unit, double factor) = length switch
        {
            _ when length.EndsWith("cm") => ("cm", 72 / 2.54),
            _ when length.EndsWith("mm") => ("mm", 72 / 25.4),
            _ when length.EndsWith("in") => ("in", 72.0),
            _ when length.EndsWith("pt") => ("pt", 1.0),
            _ when length.EndsWith("pc") => ("pc", 12.0),
            _ when length.EndsWith("px") => ("px", 0.75),
            _ => ("", 0.0)
        };
        if (factor == 0) return null;
        return double.TryParse(length[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value * factor : null;
    }

    /// <summary>The picture a frame shows: stored in the file, or written into the XML itself.</summary>
    public static Picture? ReadPicture(ZipArchive zip, XElement frame)
    {
        var image = frame.Element(Draw + "image");
        if (image == null) return null;

        byte[]? data = null;
        var address = image.Attribute(XLink + "href")?.Value;
        if (!string.IsNullOrEmpty(address) && !address.Contains("://"))
            data = ReadBytes(zip, address.TrimStart('.', '/'));
        else if (image.Element(Office + "binary-data")?.Value is { Length: > 0 } inline)
        {
            try { data = Convert.FromBase64String(inline); } catch (FormatException) { /* not a picture then */ }
        }

        var alt = frame.Element(Svg + "title")?.Value ?? frame.Element(Svg + "desc")?.Value ?? "";
        return Picture.Load(data, alt, Points(frame.Attribute(Svg + "width")?.Value), Points(frame.Attribute(Svg + "height")?.Value));
    }
}

/// <summary>
/// Reads an OpenDocument text file (.odt, LibreOffice Writer's format): headings, paragraphs
/// with their character formatting, lists, tables, pictures, links and footnotes.
/// </summary>
internal sealed class OdtReader
{
    private sealed record StyleInfo(string? Parent, TextStyle Style, bool BreakBefore, TextAlign? Align, string? ListStyle);

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, StyleInfo> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _numberedLevels = new(StringComparer.Ordinal);
    private readonly List<List<Block>> _notes = [];

    private OdtReader(ZipArchive zip) => _zip = zip;

    public static RichDocument ReadFile(string path)
    {
        using var zip = Odf.Open(path);
        var content = Odf.ReadXml(zip, "content.xml") ?? throw new FriendlyException("This OpenDocument file is damaged.");
        var reader = new OdtReader(zip);
        reader.ReadStyles(Odf.ReadXml(zip, "styles.xml"));
        reader.ReadStyles(content);

        var meta = Odf.ReadXml(zip, "meta.xml");
        var document = new RichDocument
        {
            Title = meta?.Descendants(Odf.Dc + "title").FirstOrDefault()?.Value is { Length: > 0 } title ? title : null,
            Author = meta?.Descendants(Odf.Dc + "creator").FirstOrDefault()?.Value is { Length: > 0 } author ? author : null,
            Language = meta?.Descendants(Odf.Dc + "language").FirstOrDefault()?.Value is { Length: > 0 } language ? language : null
        };

        var body = content.Root?.Element(Odf.Office + "body")?.Element(Odf.Office + "text");
        if (body == null) return document;

        document.Blocks.AddRange(reader.ReadBlocks(body, listStyle: null, depth: 0));
        if (reader._notes.Count > 0)
        {
            document.Blocks.Add(new Rule());
            var notes = new ListBlock(ordered: true);
            notes.Items.AddRange(reader._notes);
            document.Blocks.Add(notes);
        }
        return document;
    }

    // ---------- styles ----------

    private static readonly string[] FixedWidthFonts = ["Consolas", "Courier", "Cascadia", "Liberation Mono", "DejaVu Sans Mono", "Monospace", "Lucida Console"];

    private void ReadStyles(XDocument? source)
    {
        if (source?.Root == null) return;

        foreach (var style in source.Root.Descendants(Odf.Style + "style"))
        {
            var name = style.Attribute(Odf.Style + "name")?.Value;
            if (name == null) continue;

            var text = style.Element(Odf.Style + "text-properties");
            var paragraph = style.Element(Odf.Style + "paragraph-properties");
            var flags = TextStyle.None;
            if (text != null)
            {
                if (text.Attribute(Odf.Fo + "font-weight")?.Value is "bold" or "600" or "700" or "800" or "900") flags |= TextStyle.Bold;
                if (text.Attribute(Odf.Fo + "font-style")?.Value is "italic" or "oblique") flags |= TextStyle.Italic;
                if (text.Attribute(Odf.Style + "text-underline-style")?.Value is { } underline and not "none") flags |= TextStyle.Underline;
                if (text.Attribute(Odf.Style + "text-line-through-style")?.Value is { } strike and not "none") flags |= TextStyle.Strike;
                var position = text.Attribute(Odf.Style + "text-position")?.Value ?? "";
                if (position.StartsWith("super")) flags |= TextStyle.Superscript;
                else if (position.StartsWith("sub")) flags |= TextStyle.Subscript;
                var font = text.Attribute(Odf.Style + "font-name")?.Value ?? text.Attribute(Odf.Fo + "font-family")?.Value ?? "";
                if (FixedWidthFonts.Any(known => font.Contains(known, StringComparison.OrdinalIgnoreCase))) flags |= TextStyle.Code;
            }

            TextAlign? align = paragraph?.Attribute(Odf.Fo + "text-align")?.Value switch
            {
                "center" => TextAlign.Center,
                "end" or "right" => TextAlign.End,
                "justify" => TextAlign.Justify,
                "start" or "left" => TextAlign.Start,
                _ => null
            };
            _styles[name] = new StyleInfo(
                style.Attribute(Odf.Style + "parent-style-name")?.Value,
                flags,
                paragraph?.Attribute(Odf.Fo + "break-before")?.Value == "page",
                align,
                style.Attribute(Odf.Style + "list-style-name")?.Value);
        }

        // which levels of each list style count 1, 2, 3 rather than showing a bullet
        foreach (var list in source.Root.Descendants(Odf.Text + "list-style"))
        {
            var name = list.Attribute(Odf.Style + "name")?.Value;
            if (name == null) continue;
            var numbered = new HashSet<int>();
            foreach (var level in list.Elements(Odf.Text + "list-level-style-number"))
            {
                // a "number" level without a number format is how an unnumbered level is written
                if (level.Attribute(Odf.Style + "num-format")?.Value is { Length: > 0 } &&
                    int.TryParse(level.Attribute(Odf.Text + "level")?.Value, out int depth))
                    numbered.Add(depth);
            }
            _numberedLevels[name] = numbered;
        }
    }

    private IEnumerable<StyleInfo> Chain(string? name)
    {
        for (int depth = 0; name != null && depth < 20 && _styles.TryGetValue(name, out var style); depth++)
        {
            yield return style;
            name = style.Parent;
        }
    }

    private TextStyle TextStyleOf(string? name)
    {
        var style = TextStyle.None;
        foreach (var info in Chain(name)) style |= info.Style;
        return style;
    }

    // ---------- blocks ----------

    private List<Block> ReadBlocks(XElement container, string? listStyle, int depth)
    {
        var blocks = new List<Block>();
        foreach (var element in container.Elements())
        {
            if (element.Name == Odf.Text + "p" || element.Name == Odf.Text + "h")
            {
                var styleName = element.Attribute(Odf.Text + "style-name")?.Value;
                if (Chain(styleName).Any(s => s.BreakBefore)) blocks.Add(new PageBreak());

                var builder = new BlockBuilder();
                ReadInlines(element, builder, TextStyle.None, null);
                if (element.Name == Odf.Text + "h")
                    builder.EndHeading(int.TryParse(element.Attribute(Odf.Text + "outline-level")?.Value, out int level) ? level : 1);
                else
                    builder.EndParagraph(Chain(styleName).Select(s => s.Align).FirstOrDefault(a => a != null) ?? TextAlign.Start);
                blocks.AddRange(builder.Blocks);
            }
            else if (element.Name == Odf.Text + "list")
            {
                // only the outermost list names its style; the lists inside it follow
                var style = element.Attribute(Odf.Text + "style-name")?.Value ?? listStyle;
                bool ordered = style != null && _numberedLevels.TryGetValue(style, out var numbered) && numbered.Contains(depth + 1);
                var list = new ListBlock(ordered);
                foreach (var item in element.Elements().Where(e => e.Name == Odf.Text + "list-item" || e.Name == Odf.Text + "list-header"))
                {
                    var content = ReadBlocks(item, style, depth + 1);
                    if (content.Count > 0) list.Items.Add(content);
                }
                if (list.Items.Count > 0) blocks.Add(list);
            }
            else if (element.Name == Odf.Table + "table")
            {
                if (ReadTable(element) is { } table) blocks.Add(table);
            }
            else if (element.Name == Odf.Text + "section" || element.Name == Odf.Text + "index-body" ||
                     element.Name.LocalName is "table-of-content" or "alphabetical-index" or "illustration-index" or "bibliography" or "user-index")
            {
                blocks.AddRange(ReadBlocks(element, listStyle, depth));
            }
            else if (element.Name == Odf.Draw + "frame")
            {
                // a picture or text box anchored to the page rather than to a paragraph
                var builder = new BlockBuilder();
                ReadFrame(element, builder);
                blocks.AddRange(builder.Blocks);
            }
        }
        return blocks;
    }

    private Table? ReadTable(XElement source)
    {
        var table = new Table();
        void AddRows(XElement container, bool header)
        {
            foreach (var element in container.Elements())
            {
                if (element.Name == Odf.Table + "table-header-rows") AddRows(element, header: true);
                else if (element.Name == Odf.Table + "table-rows" || element.Name == Odf.Table + "table-row-group") AddRows(element, header);
                else if (element.Name == Odf.Table + "table-row")
                {
                    var cells = new List<TableCell>();
                    foreach (var cell in element.Elements(Odf.Table + "table-cell"))
                    {
                        int span = int.TryParse(cell.Attribute(Odf.Table + "number-columns-spanned")?.Value, out int parsed) ? Math.Clamp(parsed, 1, 100) : 1;
                        cells.Add(new TableCell(ReadBlocks(cell, null, 0)) { ColumnSpan = span });
                    }
                    if (cells.Count == 0) continue;
                    if (header && table.HeaderRows == table.Rows.Count) table.HeaderRows++;
                    table.Rows.Add(cells);
                }
            }
        }
        AddRows(source, header: false);
        return table.Rows.Count == 0 ? null : table;
    }

    // ---------- text ----------

    private void ReadInlines(XElement container, BlockBuilder builder, TextStyle style, string? link)
    {
        foreach (var node in container.Nodes())
        {
            if (node is XText text)
            {
                HtmlReader.AddText(text.Value, builder, style, link); // runs of spaces count as one; more are written as text:s
                continue;
            }
            if (node is not XElement element) continue;

            if (element.Name == Odf.Text + "span")
                ReadInlines(element, builder, style | TextStyleOf(element.Attribute(Odf.Text + "style-name")?.Value), link);
            else if (element.Name == Odf.Text + "a")
                ReadInlines(element, builder, style, element.Attribute(Odf.XLink + "href")?.Value is { Length: > 0 } address && !address.StartsWith('#') ? address : link);
            else if (element.Name == Odf.Text + "s")
                builder.Text(new string(' ', int.TryParse(element.Attribute(Odf.Text + "c")?.Value, out int count) ? Math.Clamp(count, 1, 1000) : 1), style, link);
            else if (element.Name == Odf.Text + "tab")
                builder.Text("\t", style, link);
            else if (element.Name == Odf.Text + "line-break")
                builder.Break();
            else if (element.Name == Odf.Text + "note")
            {
                var body = element.Element(Odf.Text + "note-body");
                var blocks = body == null ? [] : ReadBlocks(body, null, 0);
                if (blocks.Count == 0) continue;
                _notes.Add(blocks);
                builder.Text(_notes.Count.ToString(), TextStyle.Superscript, link);
            }
            else if (element.Name == Odf.Draw + "frame")
                ReadFrame(element, builder);
            else if (element.Name == Odf.Draw + "a") // a linked picture
                foreach (var frame in element.Elements(Odf.Draw + "frame")) ReadFrame(frame, builder);
            else if (element.Name.Namespace == Odf.Text && element.Name.LocalName is "tracked-changes" or "change" or "change-start" or "change-end"
                         or "bookmark" or "bookmark-start" or "bookmark-end" or "reference-mark" or "soft-page-break" or "annotation" or "annotation-end")
            {
                // marks that show nothing
            }
            else if (element.Name.Namespace == Odf.Text)
            {
                // fields (a date, a page number, a reference …) carry the text they last showed
                ReadInlines(element, builder, style, link);
            }
        }
    }

    private void ReadFrame(XElement frame, BlockBuilder builder)
    {
        if (frame.Element(Odf.Draw + "text-box") is { } box)
        {
            builder.AddRange(ReadBlocks(box, null, 0));
            return;
        }
        builder.Add(Odf.ReadPicture(_zip, frame));
    }
}
