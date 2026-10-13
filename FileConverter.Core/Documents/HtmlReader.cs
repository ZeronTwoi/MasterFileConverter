using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace FileConverter.Core.Documents;

/// <summary>
/// Reads HTML as a document: its headings, paragraphs, lists, tables, pictures and the formatting
/// of its text. The page's layout — columns, colours, whatever its style sheets arrange — is not
/// reproduced; that takes a browser. Nothing is ever fetched from the internet: pictures come
/// from the file itself, or from beside it through <paramref name="resource"/>.
/// </summary>
internal sealed partial class HtmlReader
{
    private readonly Func<string, byte[]?> _resource;

    /// <param name="resource">Reads a file the HTML refers to by a relative address; null when there is none.</param>
    public HtmlReader(Func<string, byte[]?>? resource = null) => _resource = resource ?? (_ => null);

    /// <summary>An HTML file with the pictures that lie beside it.</summary>
    public static RichDocument ReadFile(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var reader = new HtmlReader(address => ReadBeside(folder, address));
        return reader.Read(Decode(File.ReadAllBytes(path)));
    }

    /// <summary>
    /// A file named by a relative address — only from <paramref name="folder"/> and the folders in
    /// it, which is where a saved page keeps its pictures. A document that points somewhere else
    /// on the computer does not get that file put into its conversion.
    /// </summary>
    internal static byte[]? ReadBeside(string folder, string address)
    {
        try
        {
            if (address.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) address = new Uri(address).LocalPath;
            else address = Uri.UnescapeDataString(address.Split('?', '#')[0]);
            if (address.Length == 0) return null;
            var file = Path.GetFullPath(Path.Combine(folder, address));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            if (!file.StartsWith(root, Platform.PlatformServices.Current.PathComparison)) return null;
            var info = new FileInfo(file);
            return info.Exists && info.Length <= 64 << 20 ? File.ReadAllBytes(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The bytes of an HTML file as text: by its byte-order mark, else by the character set it
    /// names for itself, else as UTF-8 — or, when that does not fit, the system's older code page.
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        var named = CharsetPattern().Match(head);
        if (named.Success)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(named.Groups[1].Value).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // a name no table knows: go by the bytes themselves
            }
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = Platform.PlatformServices.Current.LegacyCodePage;
            try { return Encoding.GetEncoding(codePage > 0 ? codePage : 1252).GetString(bytes); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1.GetString(bytes); }
        }
    }

    [GeneratedRegex("""(?:charset|encoding)\s*=\s*["']?\s*([A-Za-z0-9_\-:.]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetPattern();

    public RichDocument Read(string html)
    {
        var page = new HtmlDocument { OptionFixNestedTags = true, OptionReadEncoding = false };
        page.LoadHtml(html);

        var document = new RichDocument
        {
            Title = Clean(page.DocumentNode.SelectSingleNode("//title")?.InnerText),
            Language = page.DocumentNode.SelectSingleNode("//html")?.GetAttributeValue("lang", "") is { Length: > 0 } language ? language : null,
            Author = page.DocumentNode.SelectSingleNode("//meta[@name='author']")?.GetAttributeValue("content", "") is { Length: > 0 } author
                ? HtmlEntity.DeEntitize(author)
                : null
        };
        document.Blocks.AddRange(ReadBlocks(page.DocumentNode.SelectSingleNode("//body") ?? page.DocumentNode));
        return document;
    }

    private static string? Clean(string? text)
    {
        text = text == null ? null : Whitespace().Replace(HtmlEntity.DeEntitize(text), " ").Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The blocks inside one element.</summary>
    private List<Block> ReadBlocks(HtmlNode container)
    {
        var builder = new BlockBuilder();
        foreach (var child in container.ChildNodes) Walk(child, builder, TextStyle.None, null);
        builder.EndParagraph();
        return builder.Blocks;
    }

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "title", "meta", "link", "noscript", "template", "iframe", "object", "embed", "applet",
        "input", "select", "option", "button", "textarea", "canvas", "audio", "video", "map", "area", "base", "colgroup", "col"
    };

    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "main", "header", "footer", "aside", "figure", "figcaption", "center", "address",
        "details", "summary", "fieldset", "legend", "form", "body", "html", "dl", "dt", "dd", "nav", "hgroup", "caption", "label"
    };

    private void Walk(HtmlNode node, BlockBuilder builder, TextStyle style, string? link)
    {
        switch (node.NodeType)
        {
            case HtmlNodeType.Text:
                AddText(HtmlEntity.DeEntitize(((HtmlTextNode)node).Text), builder, style, link);
                return;
            case HtmlNodeType.Element:
                break;
            default:
                return; // comments and the like
        }

        var name = node.Name.ToLowerInvariant();
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name[(colon + 1)..]; // an e-book's "svg:svg", "epub:…"
        if (Skipped.Contains(name)) return;

        switch (name)
        {
            case "p":
                builder.EndParagraph();
                WalkChildren(node, builder, style | StyleOf(node), link);
                builder.EndParagraph(AlignOf(node));
                break;

            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                builder.EndParagraph();
                WalkChildren(node, builder, style, link);
                builder.EndHeading(name[1] - '0');
                break;

            case "br":
                builder.Break();
                break;

            case "hr":
                builder.Add(new Rule());
                break;

            case "b" or "strong":
                WalkChildren(node, builder, style | TextStyle.Bold, link);
                break;
            case "i" or "em" or "cite" or "var" or "dfn":
                WalkChildren(node, builder, style | TextStyle.Italic, link);
                break;
            case "u" or "ins":
                WalkChildren(node, builder, style | TextStyle.Underline, link);
                break;
            case "s" or "strike" or "del":
                WalkChildren(node, builder, style | TextStyle.Strike, link);
                break;
            case "code" or "kbd" or "samp" or "tt":
                WalkChildren(node, builder, style | TextStyle.Code, link);
                break;
            case "sup":
                WalkChildren(node, builder, (style & ~TextStyle.Subscript) | TextStyle.Superscript, link);
                break;
            case "sub":
                WalkChildren(node, builder, (style & ~TextStyle.Superscript) | TextStyle.Subscript, link);
                break;

            case "a":
            {
                var address = HtmlEntity.DeEntitize(node.GetAttributeValue("href", "")).Trim();
                bool followable = address.Length > 0 && !address.StartsWith('#') &&
                                  !address.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
                WalkChildren(node, builder, style, followable ? address : link);
                break;
            }

            case "ul" or "ol" or "menu":
            {
                var list = new ListBlock(ordered: name == "ol")
                {
                    Start = int.TryParse(node.GetAttributeValue("start", "1"), out int start) ? start : 1
                };
                foreach (var item in node.ChildNodes.Where(child => child.NodeType == HtmlNodeType.Element))
                {
                    // a list straight inside a list (without an item around it) belongs to the item before
                    if (item.Name.Equals("li", StringComparison.OrdinalIgnoreCase)) list.Items.Add(ReadBlocks(item));
                    else if (list.Items.Count > 0) list.Items[^1].AddRange(ReadBlocks(item));
                    else list.Items.Add(ReadBlocks(item));
                }
                list.Items.RemoveAll(item => item.Count == 0);
                if (list.Items.Count > 0) builder.Add(list);
                break;
            }

            case "li": // an item without its list
                builder.EndParagraph();
                WalkChildren(node, builder, style, link);
                builder.EndParagraph();
                break;

            case "blockquote":
            {
                var blocks = ReadBlocks(node);
                if (blocks.Count > 0) builder.Add(new Quote(blocks));
                break;
            }

            case "pre":
            {
                var text = HtmlEntity.DeEntitize(node.InnerText).Replace("\r\n", "\n").Trim('\n');
                if (text.Length > 0) builder.Add(new CodeBlock(text));
                break;
            }

            case "table":
                builder.Add(ReadTable(node));
                break;

            case "img" or "image":
                builder.Add(ReadPicture(node));
                break;

            case "svg":
                // an e-book's cover page: a drawing whose only content is a picture
                foreach (var picture in node.Descendants().Where(n => n.Name.EndsWith("image", StringComparison.OrdinalIgnoreCase)))
                    builder.Add(ReadPicture(picture));
                break;

            default:
                if (Containers.Contains(name))
                {
                    builder.EndParagraph();
                    WalkChildren(node, builder, style | StyleOf(node), link);
                    builder.EndParagraph(AlignOf(node));
                }
                else
                {
                    // span, font, small, mark, abbr, q, time … and anything unknown: just its content
                    WalkChildren(node, builder, style | StyleOf(node), link);
                }
                break;
        }
    }

    private void WalkChildren(HtmlNode node, BlockBuilder builder, TextStyle style, string? link)
    {
        foreach (var child in node.ChildNodes) Walk(child, builder, style, link);
    }

    /// <summary>Text as a browser shows it: any run of spaces, tabs and line ends is one space.</summary>
    internal static void AddText(string text, BlockBuilder builder, TextStyle style, string? link)
    {
        if (text.Length == 0) return;
        var collapsed = new StringBuilder(text.Length);
        bool space = builder.AtLineStart || builder.EndsWithSpace;
        foreach (char c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                if (!space) collapsed.Append(' ');
                space = true;
            }
            else
            {
                collapsed.Append(c);
                space = false;
            }
        }
        builder.Text(collapsed.ToString(), style, link);
    }

    /// <summary>Formatting given in a style attribute, as word processors write it when they save HTML.</summary>
    private static TextStyle StyleOf(HtmlNode node)
    {
        var css = node.GetAttributeValue("style", "");
        if (css.Length == 0) return TextStyle.None;

        var style = TextStyle.None;
        foreach (var declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            var property = declaration[..colon].Trim().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim().ToLowerInvariant();
            switch (property)
            {
                case "font-weight" when value is "bold" or "bolder" || (int.TryParse(value, out int weight) && weight >= 600):
                    style |= TextStyle.Bold;
                    break;
                case "font-style" when value is "italic" or "oblique":
                    style |= TextStyle.Italic;
                    break;
                case "text-decoration" or "text-decoration-line":
                    if (value.Contains("underline")) style |= TextStyle.Underline;
                    if (value.Contains("line-through")) style |= TextStyle.Strike;
                    break;
                case "vertical-align" when value == "super":
                    style |= TextStyle.Superscript;
                    break;
                case "vertical-align" when value == "sub":
                    style |= TextStyle.Subscript;
                    break;
            }
        }
        return style;
    }

    private static TextAlign AlignOf(HtmlNode node)
    {
        var value = node.GetAttributeValue("align", "").ToLowerInvariant();
        var css = node.GetAttributeValue("style", "");
        var match = TextAlignPattern().Match(css);
        if (match.Success) value = match.Groups[1].Value.ToLowerInvariant();
        return value switch
        {
            "center" => TextAlign.Center,
            "right" or "end" => TextAlign.End,
            "justify" => TextAlign.Justify,
            _ => TextAlign.Start
        };
    }

    [GeneratedRegex(@"text-align\s*:\s*([a-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TextAlignPattern();

    private Table? ReadTable(HtmlNode node)
    {
        var table = new Table();
        // rows of this table only, not of tables inside its cells
        foreach (var row in RowsOf(node))
        {
            var cells = new List<TableCell>();
            bool heading = row.ParentNode?.Name.Equals("thead", StringComparison.OrdinalIgnoreCase) == true;
            bool allHeadings = true;
            foreach (var cell in row.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element))
            {
                bool th = cell.Name.Equals("th", StringComparison.OrdinalIgnoreCase);
                if (!th && !cell.Name.Equals("td", StringComparison.OrdinalIgnoreCase)) continue;
                allHeadings &= th;
                int span = int.TryParse(cell.GetAttributeValue("colspan", "1"), out int parsed) ? Math.Clamp(parsed, 1, 100) : 1;
                cells.Add(new TableCell(ReadBlocks(cell)) { ColumnSpan = span });
            }
            if (cells.Count == 0) continue;
            if ((heading || allHeadings) && table.HeaderRows == table.Rows.Count) table.HeaderRows++;
            table.Rows.Add(cells);
        }
        return table.Rows.Count == 0 ? null : table;
    }

    private static IEnumerable<HtmlNode> RowsOf(HtmlNode table)
    {
        foreach (var child in table.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element))
        {
            if (child.Name.Equals("tr", StringComparison.OrdinalIgnoreCase)) yield return child;
            else if (child.Name.ToLowerInvariant() is "thead" or "tbody" or "tfoot")
                foreach (var row in child.ChildNodes.Where(c => c.Name.Equals("tr", StringComparison.OrdinalIgnoreCase)))
                    yield return row;
        }
    }

    private Picture? ReadPicture(HtmlNode node)
    {
        var address = node.GetAttributeValue("src", "");
        if (address.Length == 0) address = node.GetAttributeValue("xlink:href", "");
        if (address.Length == 0) address = node.GetAttributeValue("href", "");
        address = HtmlEntity.DeEntitize(address).Trim();
        if (address.Length == 0) return null;

        byte[]? data;
        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            data = FromDataAddress(address);
        }
        else if (address.Contains("://") && !address.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || address.StartsWith("//"))
        {
            return null; // on the internet: the app does not go there
        }
        else
        {
            data = _resource(address);
        }

        var alt = HtmlEntity.DeEntitize(node.GetAttributeValue("alt", ""));
        return Picture.Load(data, alt, Pixels(node.GetAttributeValue("width", "")), Pixels(node.GetAttributeValue("height", "")));
    }

    /// <summary>A width or height attribute in pixels, as points; null for anything else (a percentage, nothing).</summary>
    private static double? Pixels(string value)
    {
        value = value.Trim();
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) value = value[..^2];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels) && pixels is > 0 and < 100_000
            ? pixels * 0.75
            : null;
    }

    private static byte[]? FromDataAddress(string address)
    {
        int comma = address.IndexOf(',');
        if (comma < 0) return null;
        var header = address[5..comma];
        var payload = address[(comma + 1)..];
        try
        {
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(Whitespace().Replace(Uri.UnescapeDataString(payload), ""))
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
