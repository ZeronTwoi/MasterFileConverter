using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FileConverter.Core.Documents;

/// <summary>What every OpenDocument file is packed like, whatever is in it.</summary>
internal sealed class OdfPackage : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly List<(string Path, string Type)> _entries = [];
    private readonly string _mimeType;

    public OdfPackage(string output, string mimeType)
    {
        _mimeType = mimeType;
        _zip = new ZipArchive(new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None), ZipArchiveMode.Create);
        // first in the file and not compressed: this is how a program knows what it has in hand
        using var stream = _zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open();
        stream.Write(Encoding.ASCII.GetBytes(mimeType));
    }

    public void Add(string path, byte[] data, string type, bool compress = true)
    {
        using (var stream = _zip.CreateEntry(path, compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression).Open())
            stream.Write(data);
        _entries.Add((path, type));
    }

    public void AddXml(string path, XDocument xml) => Add(path, ToBytes(xml), "text/xml");

    /// <summary>A picture under "Pictures/"; returns the path the content refers to it by.</summary>
    public string AddPicture(Picture picture, int number)
    {
        var path = $"Pictures/image-{number:D3}.{picture.Format}";
        // a JPEG is as small as it gets; a PNG written in a hurry still gives a little
        Add(path, picture.Data, picture.Mime, compress: picture.Format == "png");
        return path;
    }

    private static byte[] ToBytes(XDocument xml)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            xml.Save(writer);
        return buffer.ToArray();
    }

    /// <summary>The "office:document-…" element a part of the file starts with, with every prefix the parts use.</summary>
    public static XElement Root(string name, params object?[] content) => new(Odf.Office + name,
        new XAttribute(XNamespace.Xmlns + "office", Odf.Office), new XAttribute(XNamespace.Xmlns + "text", Odf.Text),
        new XAttribute(XNamespace.Xmlns + "table", Odf.Table), new XAttribute(XNamespace.Xmlns + "draw", Odf.Draw),
        new XAttribute(XNamespace.Xmlns + "style", Odf.Style), new XAttribute(XNamespace.Xmlns + "fo", Odf.Fo),
        new XAttribute(XNamespace.Xmlns + "svg", Odf.Svg), new XAttribute(XNamespace.Xmlns + "xlink", Odf.XLink),
        new XAttribute(XNamespace.Xmlns + "dc", Odf.Dc), new XAttribute(XNamespace.Xmlns + "meta", Odf.Meta),
        new XAttribute(XNamespace.Xmlns + "number", Odf.Number), new XAttribute(XNamespace.Xmlns + "presentation", Odf.Presentation),
        new XAttribute(Odf.Office + "version", "1.3"), content);

    public void AddMeta(string? title, string? author) => AddXml("meta.xml", new XDocument(Root("document-meta",
        new XElement(Odf.Office + "meta",
            new XElement(Odf.Meta + "generator", $"{AppInfo.DisplayName}/{AppInfo.Version}"),
            title == null ? null : new XElement(Odf.Dc + "title", title),
            author == null ? null : new XElement(Odf.Dc + "creator", author),
            new XElement(Odf.Dc + "date", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))))));

    public void Dispose()
    {
        // the list of everything in the file, which the format wants written last
        var manifest = new XElement(Odf.Manifest + "manifest",
            new XAttribute(XNamespace.Xmlns + "manifest", Odf.Manifest), new XAttribute(Odf.Manifest + "version", "1.3"),
            new XElement(Odf.Manifest + "file-entry", new XAttribute(Odf.Manifest + "full-path", "/"),
                new XAttribute(Odf.Manifest + "version", "1.3"), new XAttribute(Odf.Manifest + "media-type", _mimeType)));
        foreach (var (path, type) in _entries)
            manifest.Add(new XElement(Odf.Manifest + "file-entry",
                new XAttribute(Odf.Manifest + "full-path", path), new XAttribute(Odf.Manifest + "media-type", type)));
        using (var stream = _zip.CreateEntry("META-INF/manifest.xml", CompressionLevel.Optimal).Open())
            stream.Write(ToBytes(new XDocument(manifest)));
        _zip.Dispose();
    }

    public static string Length(double points) => (points / 72.0).ToString("0.####", CultureInfo.InvariantCulture) + "in";
}

/// <summary>
/// Writes a document as an OpenDocument text file (.odt), LibreOffice Writer's own format:
/// headings as headings, lists as lists, tables as tables, pictures stored inside the file.
/// </summary>
internal sealed class OdtWriter
{
    private const double TextWidthPt = 595.3 - 2 * 72; // A4 with one-inch margins

    private readonly OdfPackage _package;
    private readonly XElement _automatic = new(Odf.Office + "automatic-styles");
    private readonly Dictionary<TextStyle, string> _spans = [];
    private readonly Dictionary<Picture, string> _pictures = new(ReferenceEqualityComparer.Instance);
    private int _tables, _frames;

    private OdtWriter(OdfPackage package) => _package = package;

    public static void Write(RichDocument document, string output)
    {
        using var package = new OdfPackage(output, "application/vnd.oasis.opendocument.text");
        var writer = new OdtWriter(package);

        var text = new XElement(Odf.Office + "text");
        writer.Append(text, document.Blocks, style: "Text_20_body", inCell: false);
        if (!text.HasElements) text.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", "Text_20_body")));

        writer.AddFixedStyles();
        package.AddXml("content.xml", new XDocument(OdfPackage.Root("document-content",
            FontFaces(), writer._automatic, new XElement(Odf.Office + "body", text))));
        package.AddXml("styles.xml", new XDocument(OdfPackage.Root("document-styles", FontFaces(), NamedStyles(), PageStyles())));
        package.AddMeta(document.Title, document.Author);
    }

    // ---------- blocks ----------

    private void Append(XElement parent, IEnumerable<Block> blocks, string style, bool inCell)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                    parent.Add(new XElement(Odf.Text + "h",
                        new XAttribute(Odf.Text + "style-name", $"Heading_20_{heading.Level}"),
                        new XAttribute(Odf.Text + "outline-level", heading.Level),
                        Inlines(heading.Inlines)));
                    break;

                case Paragraph paragraph:
                    parent.Add(new XElement(Odf.Text + "p",
                        new XAttribute(Odf.Text + "style-name", paragraph.Align switch
                        {
                            TextAlign.Center => "P_center",
                            TextAlign.End => "P_end",
                            TextAlign.Justify => "P_justify",
                            _ => style
                        }),
                        Inlines(paragraph.Inlines)));
                    break;

                case ListBlock list:
                {
                    var element = new XElement(Odf.Text + "list",
                        new XAttribute(Odf.Text + "style-name", list.Ordered ? "L_numbers" : "L_bullets"));
                    // numbering goes on from the list before unless told to start again
                    if (list.Ordered) element.Add(new XAttribute(Odf.Text + "continue-numbering", "false"));
                    bool firstItem = true;
                    foreach (var item in list.Items)
                    {
                        var entry = new XElement(Odf.Text + "list-item");
                        if (firstItem && list.Ordered && list.Start != 1) entry.Add(new XAttribute(Odf.Text + "start-value", list.Start));
                        firstItem = false;
                        Append(entry, item, "P_list", inCell);
                        if (!entry.HasElements) entry.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", "P_list")));
                        element.Add(entry);
                    }
                    parent.Add(element);
                    break;
                }

                case CodeBlock code:
                    // every line a paragraph of its own; runs of spaces are written out, or they would count as one
                    foreach (var line in code.Text.Replace("\r\n", "\n").Split('\n'))
                        parent.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", "Preformatted_20_Text"), Spaced(line)));
                    break;

                case Quote quote:
                    Append(parent, quote.Blocks, "Quotations", inCell);
                    break;

                case Table table when !inCell:
                    parent.Add(BuildTable(table));
                    break;

                case Table nested: // a table inside a cell: its rows as lines
                    foreach (var row in nested.Rows)
                        parent.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", style),
                            Spaced(string.Join("\t", row.Select(PlainTextWriter.CellText)))));
                    break;

                case Picture picture:
                    parent.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", style), Frame(picture, inCell ? 150 : TextWidthPt)));
                    break;

                case PageBreak when !inCell:
                    parent.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", "P_break")));
                    break;

                case Rule:
                    parent.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", "Horizontal_20_Line")));
                    break;
            }
        }
    }

    private XElement BuildTable(Table source)
    {
        int columns = Math.Max(source.ColumnCount, 1);
        string name = $"Table{++_tables}";

        _automatic.Add(new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", name), new XAttribute(Odf.Style + "family", "table"),
            new XElement(Odf.Style + "table-properties", new XAttribute(Odf.Style + "width", OdfPackage.Length(TextWidthPt)),
                new XAttribute(Odf.Table + "align", "left"), new XAttribute(Odf.Fo + "margin-bottom", "0.1in"))));
        _automatic.Add(new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", name + ".col"), new XAttribute(Odf.Style + "family", "table-column"),
            new XElement(Odf.Style + "table-column-properties", new XAttribute(Odf.Style + "column-width", OdfPackage.Length(TextWidthPt / columns)))));

        var table = new XElement(Odf.Table + "table", new XAttribute(Odf.Table + "name", name), new XAttribute(Odf.Table + "style-name", name),
            new XElement(Odf.Table + "table-column", new XAttribute(Odf.Table + "style-name", name + ".col"),
                new XAttribute(Odf.Table + "number-columns-repeated", columns)));

        XElement? headers = source.HeaderRows > 0 ? new XElement(Odf.Table + "table-header-rows") : null;
        if (headers != null) table.Add(headers);

        for (int r = 0; r < source.Rows.Count; r++)
        {
            bool heading = r < source.HeaderRows;
            var row = new XElement(Odf.Table + "table-row");
            int used = 0;
            foreach (var cell in source.Rows[r])
            {
                int span = Math.Clamp(cell.ColumnSpan, 1, Math.Max(columns - used, 1));
                used += span;
                var entry = new XElement(Odf.Table + "table-cell",
                    new XAttribute(Odf.Table + "style-name", heading ? "Cell_heading" : "Cell"), new XAttribute(Odf.Office + "value-type", "string"));
                if (span > 1) entry.Add(new XAttribute(Odf.Table + "number-columns-spanned", span));
                Append(entry, cell.Blocks, heading ? "Table_20_Heading" : "Table_20_Contents", inCell: true);
                if (!entry.HasElements) entry.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", "Table_20_Contents")));
                row.Add(entry);
                // the columns a merged cell covers are still there, as covered cells
                for (int covered = 1; covered < span; covered++) row.Add(new XElement(Odf.Table + "covered-table-cell"));
            }
            for (; used < columns; used++)
                row.Add(new XElement(Odf.Table + "table-cell", new XAttribute(Odf.Table + "style-name", "Cell"),
                    new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", "Table_20_Contents"))));
            (heading ? headers! : table).Add(row);
        }
        return table;
    }

    private XElement? Frame(Picture picture, double maxWidthPt)
    {
        if (!_pictures.TryGetValue(picture, out var path))
        {
            path = _package.AddPicture(picture, _pictures.Count + 1);
            _pictures[picture] = path;
        }
        var (width, height) = picture.DisplaySize(maxWidthPt, 650);
        var frame = new XElement(Odf.Draw + "frame",
            new XAttribute(Odf.Draw + "name", $"Image{++_frames}"), new XAttribute(Odf.Draw + "style-name", "Picture"),
            new XAttribute(Odf.Text + "anchor-type", "as-char"),
            new XAttribute(Odf.Svg + "width", OdfPackage.Length(width)), new XAttribute(Odf.Svg + "height", OdfPackage.Length(height)),
            new XElement(Odf.Draw + "image", new XAttribute(Odf.XLink + "href", path), new XAttribute(Odf.XLink + "type", "simple"),
                new XAttribute(Odf.XLink + "show", "embed"), new XAttribute(Odf.XLink + "actuate", "onLoad")));
        if (picture.Alt.Length > 0) frame.Add(new XElement(Odf.Svg + "title", XhtmlWriter.Clean(picture.Alt)));
        return frame;
    }

    // ---------- text ----------

    private IEnumerable<XNode> Inlines(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is LineBreak)
            {
                yield return new XElement(Odf.Text + "line-break");
                continue;
            }
            if (inline is not TextRun run || run.Text.Length == 0) continue;

            object content = Spaced(run.Text);
            if (run.Style != TextStyle.None)
                content = new XElement(Odf.Text + "span", new XAttribute(Odf.Text + "style-name", SpanStyle(run.Style)), content);
            if (run.Link != null)
                content = new XElement(Odf.Text + "a", new XAttribute(Odf.XLink + "type", "simple"), new XAttribute(Odf.XLink + "href", run.Link), content);

            if (content is XNode node) yield return node;
            else foreach (var part in (IEnumerable<XNode>)content) yield return part;
        }
    }

    /// <summary>
    /// Text as this format keeps it: a run of spaces would be read as one, so the second and
    /// further ones are written as a count; tabs and line ends are elements of their own.
    /// </summary>
    internal static List<XNode> Spaced(string text)
    {
        text = XhtmlWriter.Clean(text);
        var nodes = new List<XNode>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            nodes.Add(new XText(plain.ToString()));
            plain.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\t')
            {
                Flush();
                nodes.Add(new XElement(Odf.Text + "tab"));
            }
            else if (c == '\n')
            {
                Flush();
                nodes.Add(new XElement(Odf.Text + "line-break"));
            }
            else if (c == ' ')
            {
                int run = 1;
                while (i + run < text.Length && text[i + run] == ' ') run++;
                // a space at the very start is dropped by readers too, so it is counted out as well
                bool leading = plain.Length == 0 && nodes.Count == 0;
                if (!leading) plain.Append(' ');
                int extra = leading ? run : run - 1;
                if (extra > 0)
                {
                    Flush();
                    nodes.Add(new XElement(Odf.Text + "s", extra > 1 ? new XAttribute(Odf.Text + "c", extra) : null));
                }
                i += run - 1;
            }
            else if (c != '\r')
            {
                plain.Append(c);
            }
        }
        Flush();
        return nodes;
    }

    private string SpanStyle(TextStyle style)
    {
        if (_spans.TryGetValue(style, out var name)) return name;
        name = $"T{_spans.Count + 1}";
        _spans[style] = name;

        var properties = new XElement(Odf.Style + "text-properties");
        if (style.HasFlag(TextStyle.Bold))
            properties.Add(new XAttribute(Odf.Fo + "font-weight", "bold"), new XAttribute(Odf.Style + "font-weight-asian", "bold"), new XAttribute(Odf.Style + "font-weight-complex", "bold"));
        if (style.HasFlag(TextStyle.Italic))
            properties.Add(new XAttribute(Odf.Fo + "font-style", "italic"), new XAttribute(Odf.Style + "font-style-asian", "italic"), new XAttribute(Odf.Style + "font-style-complex", "italic"));
        if (style.HasFlag(TextStyle.Underline))
            properties.Add(new XAttribute(Odf.Style + "text-underline-style", "solid"), new XAttribute(Odf.Style + "text-underline-width", "auto"),
                new XAttribute(Odf.Style + "text-underline-color", "font-color"));
        if (style.HasFlag(TextStyle.Strike)) properties.Add(new XAttribute(Odf.Style + "text-line-through-style", "solid"));
        if (style.HasFlag(TextStyle.Superscript)) properties.Add(new XAttribute(Odf.Style + "text-position", "super 58%"));
        else if (style.HasFlag(TextStyle.Subscript)) properties.Add(new XAttribute(Odf.Style + "text-position", "sub 58%"));
        if (style.HasFlag(TextStyle.Code)) properties.Add(new XAttribute(Odf.Style + "font-name", "Liberation Mono"), new XAttribute(Odf.Fo + "font-size", "10pt"));

        _automatic.Add(new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", name), new XAttribute(Odf.Style + "family", "text"), properties));
        return name;
    }

    // ---------- styles ----------

    private static XElement FontFaces() => new(Odf.Office + "font-face-decls",
        new XElement(Odf.Style + "font-face", new XAttribute(Odf.Style + "name", "Liberation Sans"),
            new XAttribute(Odf.Svg + "font-family", "'Liberation Sans', Arial"), new XAttribute(Odf.Style + "font-family-generic", "swiss")),
        new XElement(Odf.Style + "font-face", new XAttribute(Odf.Style + "name", "Liberation Serif"),
            new XAttribute(Odf.Svg + "font-family", "'Liberation Serif', 'Times New Roman'"), new XAttribute(Odf.Style + "font-family-generic", "roman")),
        new XElement(Odf.Style + "font-face", new XAttribute(Odf.Style + "name", "Liberation Mono"),
            new XAttribute(Odf.Svg + "font-family", "'Liberation Mono', Consolas, 'Courier New'"),
            new XAttribute(Odf.Style + "font-family-generic", "modern"), new XAttribute(Odf.Style + "font-pitch", "fixed")));

    /// <summary>The styles that belong to this file's content alone.</summary>
    private void AddFixedStyles()
    {
        XElement Paragraph(string name, string parent, params XAttribute[] properties) =>
            new(Odf.Style + "style", new XAttribute(Odf.Style + "name", name), new XAttribute(Odf.Style + "family", "paragraph"),
                new XAttribute(Odf.Style + "parent-style-name", parent), new XElement(Odf.Style + "paragraph-properties", properties));

        _automatic.Add(
            Paragraph("P_center", "Text_20_body", new XAttribute(Odf.Fo + "text-align", "center")),
            Paragraph("P_end", "Text_20_body", new XAttribute(Odf.Fo + "text-align", "end")),
            Paragraph("P_justify", "Text_20_body", new XAttribute(Odf.Fo + "text-align", "justify")),
            Paragraph("P_list", "Text_20_body", new XAttribute(Odf.Fo + "margin-bottom", "0.04in")),
            Paragraph("P_break", "Text_20_body", new XAttribute(Odf.Fo + "break-before", "page")),
            new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", "Cell"), new XAttribute(Odf.Style + "family", "table-cell"),
                new XElement(Odf.Style + "table-cell-properties", new XAttribute(Odf.Fo + "padding", "0.04in"), new XAttribute(Odf.Fo + "border", "0.5pt solid #808080"))),
            new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", "Cell_heading"), new XAttribute(Odf.Style + "family", "table-cell"),
                new XElement(Odf.Style + "table-cell-properties", new XAttribute(Odf.Fo + "padding", "0.04in"),
                    new XAttribute(Odf.Fo + "border", "0.5pt solid #808080"), new XAttribute(Odf.Fo + "background-color", "#eeeeee"))),
            new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", "Picture"), new XAttribute(Odf.Style + "family", "graphic"),
                new XElement(Odf.Style + "graphic-properties", new XAttribute(Odf.Style + "vertical-pos", "top"),
                    new XAttribute(Odf.Style + "vertical-rel", "baseline"), new XAttribute(Odf.Fo + "border", "none"))),
            ListStyle("L_bullets", ordered: false),
            ListStyle("L_numbers", ordered: true));
    }

    private static XElement ListStyle(string name, bool ordered)
    {
        string[] bullets = ["•", "◦", "▪"];
        string[] formats = ["1", "a", "i"];
        var style = new XElement(Odf.Text + "list-style", new XAttribute(Odf.Style + "name", name));
        for (int level = 1; level <= 9; level++)
        {
            var element = ordered
                ? new XElement(Odf.Text + "list-level-style-number", new XAttribute(Odf.Text + "level", level),
                    new XAttribute(Odf.Style + "num-format", formats[(level - 1) % 3]), new XAttribute(Odf.Style + "num-suffix", "."))
                : new XElement(Odf.Text + "list-level-style-bullet", new XAttribute(Odf.Text + "level", level),
                    new XAttribute(Odf.Text + "bullet-char", bullets[(level - 1) % 3]));
            element.Add(new XElement(Odf.Style + "list-level-properties", new XAttribute(Odf.Text + "list-level-position-and-space-mode", "label-alignment"),
                new XElement(Odf.Style + "list-level-label-alignment", new XAttribute(Odf.Text + "label-followed-by", "listtab"),
                    new XAttribute(Odf.Text + "list-tab-stop-position", OdfPackage.Length(18 + 18 * level)),
                    new XAttribute(Odf.Fo + "text-indent", OdfPackage.Length(-18)),
                    new XAttribute(Odf.Fo + "margin-left", OdfPackage.Length(18 + 18 * level)))));
            style.Add(element);
        }
        return style;
    }

    /// <summary>The styles a user sees and can change in the program's style list.</summary>
    private static XElement NamedStyles()
    {
        XElement Style(string name, string display, string? parent, XElement? paragraph, XElement? text, string? next = null, int? outline = null)
        {
            var style = new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", name),
                new XAttribute(Odf.Style + "display-name", display), new XAttribute(Odf.Style + "family", "paragraph"));
            if (parent != null) style.Add(new XAttribute(Odf.Style + "parent-style-name", parent));
            if (next != null) style.Add(new XAttribute(Odf.Style + "next-style-name", next));
            if (outline != null) style.Add(new XAttribute(Odf.Style + "default-outline-level", outline), new XAttribute(Odf.Style + "class", "text"));
            if (paragraph != null) style.Add(paragraph);
            if (text != null) style.Add(text);
            return style;
        }

        XElement Text(params XAttribute[] properties) => new(Odf.Style + "text-properties", properties);
        XElement Para(params XAttribute[] properties) => new(Odf.Style + "paragraph-properties", properties);
        XAttribute[] Size(string size) =>
            [new(Odf.Fo + "font-size", size), new(Odf.Style + "font-size-asian", size), new(Odf.Style + "font-size-complex", size)];
        XAttribute[] Bold() =>
            [new(Odf.Fo + "font-weight", "bold"), new(Odf.Style + "font-weight-asian", "bold"), new(Odf.Style + "font-weight-complex", "bold")];

        var styles = new XElement(Odf.Office + "styles",
            new XElement(Odf.Style + "default-style", new XAttribute(Odf.Style + "family", "paragraph"),
                new XElement(Odf.Style + "paragraph-properties", new XAttribute(Odf.Style + "tab-stop-distance", "0.5in")),
                Text([new XAttribute(Odf.Style + "font-name", "Liberation Serif"), .. Size("12pt")])),
            Style("Standard", "Default Paragraph Style", null, null, null),
            Style("Text_20_body", "Text Body", "Standard", Para(new XAttribute(Odf.Fo + "margin-top", "0in"),
                new XAttribute(Odf.Fo + "margin-bottom", "0.1in"), new XAttribute(Odf.Fo + "line-height", "115%")), null),
            Style("Heading", "Heading", "Standard", Para(new XAttribute(Odf.Fo + "margin-top", "0.17in"),
                    new XAttribute(Odf.Fo + "margin-bottom", "0.08in"), new XAttribute(Odf.Fo + "keep-with-next", "always")),
                Text([new XAttribute(Odf.Style + "font-name", "Liberation Sans"), .. Size("14pt")]), next: "Text_20_body"),
            Style("Preformatted_20_Text", "Preformatted Text", "Standard", Para(new XAttribute(Odf.Fo + "margin-top", "0in"),
                    new XAttribute(Odf.Fo + "margin-bottom", "0in"), new XAttribute(Odf.Fo + "background-color", "#f4f4f4")),
                Text([new XAttribute(Odf.Style + "font-name", "Liberation Mono"), .. Size("10pt")])),
            Style("Quotations", "Quotations", "Standard", Para(new XAttribute(Odf.Fo + "margin-left", "0.4in"),
                new XAttribute(Odf.Fo + "margin-right", "0.4in"), new XAttribute(Odf.Fo + "margin-bottom", "0.1in")), null),
            Style("Table_20_Contents", "Table Contents", "Standard", null, null),
            Style("Table_20_Heading", "Table Heading", "Table_20_Contents", null, Text(Bold())),
            Style("Horizontal_20_Line", "Horizontal Line", "Standard", Para(new XAttribute(Odf.Fo + "margin-bottom", "0.2in"),
                new XAttribute(Odf.Fo + "border-bottom", "0.5pt solid #808080"), new XAttribute(Odf.Fo + "padding", "0in")), Text(Size("6pt"))));

        string[] sizes = ["20pt", "16pt", "14pt", "13pt", "12pt", "11pt"];
        for (int level = 1; level <= 6; level++)
            styles.Add(Style($"Heading_20_{level}", $"Heading {level}", "Heading", null, Text([.. Size(sizes[level - 1]), .. Bold()]),
                next: "Text_20_body", outline: level));
        return styles;
    }

    private static object[] PageStyles() =>
    [
        new XElement(Odf.Office + "automatic-styles",
            new XElement(Odf.Style + "page-layout", new XAttribute(Odf.Style + "name", "Page"),
                new XElement(Odf.Style + "page-layout-properties",
                    new XAttribute(Odf.Fo + "page-width", "8.2681in"), new XAttribute(Odf.Fo + "page-height", "11.6929in"),
                    new XAttribute(Odf.Style + "print-orientation", "portrait"),
                    new XAttribute(Odf.Fo + "margin-top", "1in"), new XAttribute(Odf.Fo + "margin-bottom", "1in"),
                    new XAttribute(Odf.Fo + "margin-left", "1in"), new XAttribute(Odf.Fo + "margin-right", "1in")))),
        new XElement(Odf.Office + "master-styles",
            new XElement(Odf.Style + "master-page", new XAttribute(Odf.Style + "name", "Standard"), new XAttribute(Odf.Style + "page-layout-name", "Page")))
    ];
}
