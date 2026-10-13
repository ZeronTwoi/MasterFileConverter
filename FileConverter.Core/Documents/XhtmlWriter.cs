using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace FileConverter.Core.Documents;

/// <summary>
/// Writes a document as HTML: one file that needs nothing beside it — the pictures are inside
/// it, and so is the little styling it has. The same elements, written as strict XML, are the
/// chapters of an e-book (see <see cref="EpubWriter"/>).
/// </summary>
internal static class XhtmlWriter
{
    public static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    internal const string Css = """
        body { font-family: Georgia, "Times New Roman", serif; line-height: 1.5; max-width: 46em; margin: 2em auto; padding: 0 1em; color: #1a1a1a; }
        h1, h2, h3, h4, h5, h6 { font-family: "Segoe UI", Helvetica, Arial, sans-serif; line-height: 1.25; margin: 1.4em 0 0.5em; }
        p { margin: 0 0 0.8em; }
        img { max-width: 100%; height: auto; }
        pre, code { font-family: Consolas, "DejaVu Sans Mono", "Liberation Mono", monospace; font-size: 0.92em; }
        pre { background: #f4f4f4; padding: 0.8em 1em; overflow-x: auto; white-space: pre-wrap; }
        blockquote { margin: 0 0 0.8em; padding-left: 1em; border-left: 3px solid #bbb; color: #444; }
        table { border-collapse: collapse; margin: 0 0 1em; }
        th, td { border: 1px solid #999; padding: 0.3em 0.6em; vertical-align: top; text-align: left; }
        th { background: #eee; }
        hr { border: 0; border-top: 1px solid #999; margin: 1.5em 0; }
        .page-break { break-after: page; page-break-after: always; }
        """;

    public static void Write(RichDocument document, string output, string fallbackTitle)
    {
        var body = new XElement(Xhtml + "body");
        // the picture itself goes into the address, so the page is whole wherever it is taken
        Append(body, document.Blocks, picture => $"data:{picture.Mime};base64,{Convert.ToBase64String(picture.Data)}", headingIds: null);

        var html = new XElement(Xhtml + "html",
            new XAttribute("lang", document.Language ?? "en"),
            new XElement(Xhtml + "head",
                new XElement(Xhtml + "meta", new XAttribute("charset", "utf-8")),
                new XElement(Xhtml + "meta", new XAttribute("name", "viewport"), new XAttribute("content", "width=device-width, initial-scale=1")),
                document.Author == null ? null : new XElement(Xhtml + "meta", new XAttribute("name", "author"), new XAttribute("content", document.Author)),
                new XElement(Xhtml + "meta", new XAttribute("name", "generator"), new XAttribute("content", AppInfo.DisplayName)),
                new XElement(Xhtml + "title", document.Title ?? fallbackTitle),
                new XElement(Xhtml + "style", "\n" + Css)),
            body);

        using var file = new StreamWriter(output, false, new UTF8Encoding(false));
        file.Write("<!DOCTYPE html>\n");
        file.Write(Serialize(html, asXml: false));
        file.Write('\n');
    }

    /// <summary>
    /// The element as text. Browsers read HTML, not XML: an empty <c>&lt;p/&gt;</c> or
    /// <c>&lt;td/&gt;</c> would swallow what follows it, so everything but the elements that
    /// never have content is given a closing tag of its own.
    /// </summary>
    internal static string Serialize(XElement root, bool asXml)
    {
        foreach (var element in root.DescendantsAndSelf().Where(e => e.IsEmpty && !VoidElements.Contains(e.Name.LocalName)))
            element.Add("");

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            Encoding = new UTF8Encoding(false)
        };
        var text = new StringBuilder();
        using (var writer = XmlWriter.Create(text, settings)) root.Save(writer);
        return asXml ? text.ToString() : text.Replace(" xmlns=\"http://www.w3.org/1999/xhtml\"", "").ToString();
    }

    private static readonly HashSet<string> VoidElements = ["br", "hr", "img", "meta", "link", "col", "input"];

    /// <summary>Adds the blocks to <paramref name="parent"/> as HTML elements.</summary>
    /// <param name="pictureAddress">What an <c>img</c> names as its source for a picture.</param>
    /// <param name="headingIds">Receives every heading with the id it was given, for a table of contents; null to give none.</param>
    internal static void Append(XElement parent, IEnumerable<Block> blocks, Func<Picture, string> pictureAddress,
        List<(Heading Heading, string Id)>? headingIds)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                {
                    var element = new XElement(Xhtml + $"h{heading.Level}", Inlines(heading.Inlines));
                    if (headingIds != null)
                    {
                        var id = $"h{headingIds.Count + 1}";
                        element.Add(new XAttribute("id", id));
                        headingIds.Add((heading, id));
                    }
                    parent.Add(element);
                    break;
                }

                case Paragraph paragraph:
                {
                    var element = new XElement(Xhtml + "p", Inlines(paragraph.Inlines));
                    if (paragraph.Align != TextAlign.Start)
                        element.Add(new XAttribute("style", "text-align: " + paragraph.Align switch
                        {
                            TextAlign.Center => "center",
                            TextAlign.End => "right",
                            _ => "justify"
                        }));
                    parent.Add(element);
                    break;
                }

                case ListBlock list:
                {
                    var element = new XElement(Xhtml + (list.Ordered ? "ol" : "ul"));
                    if (list.Ordered && list.Start != 1) element.Add(new XAttribute("start", list.Start));
                    foreach (var item in list.Items)
                    {
                        var entry = new XElement(Xhtml + "li");
                        // an item that is one line of text needs no paragraph around it
                        if (item.Count == 1 && item[0] is Paragraph only) entry.Add(Inlines(only.Inlines));
                        else if (item.Count >= 1 && item[0] is Paragraph lead && item.Skip(1).All(b => b is ListBlock))
                        {
                            entry.Add(Inlines(lead.Inlines));
                            Append(entry, item.Skip(1), pictureAddress, headingIds);
                        }
                        else Append(entry, item, pictureAddress, headingIds);
                        element.Add(entry);
                    }
                    parent.Add(element);
                    break;
                }

                case CodeBlock code:
                    parent.Add(new XElement(Xhtml + "pre", new XElement(Xhtml + "code", code.Text)));
                    break;

                case Quote quote:
                {
                    var element = new XElement(Xhtml + "blockquote");
                    Append(element, quote.Blocks, pictureAddress, headingIds);
                    parent.Add(element);
                    break;
                }

                case Table table:
                {
                    var element = new XElement(Xhtml + "table");
                    XElement? head = table.HeaderRows > 0 ? new XElement(Xhtml + "thead") : null;
                    var bodyRows = new XElement(Xhtml + "tbody");
                    for (int r = 0; r < table.Rows.Count; r++)
                    {
                        bool heading = r < table.HeaderRows;
                        var row = new XElement(Xhtml + "tr");
                        foreach (var cell in table.Rows[r])
                        {
                            var entry = new XElement(Xhtml + (heading ? "th" : "td"));
                            if (cell.ColumnSpan > 1) entry.Add(new XAttribute("colspan", cell.ColumnSpan));
                            if (cell.Blocks.Count == 1 && cell.Blocks[0] is Paragraph only) entry.Add(Inlines(only.Inlines));
                            else Append(entry, cell.Blocks, pictureAddress, headingIds);
                            row.Add(entry);
                        }
                        (heading ? head! : bodyRows).Add(row);
                    }
                    if (head != null) element.Add(head);
                    if (bodyRows.HasElements) element.Add(bodyRows);
                    parent.Add(element);
                    break;
                }

                case Picture picture:
                {
                    var (width, height) = picture.DisplaySize(double.MaxValue);
                    parent.Add(new XElement(Xhtml + "p", new XElement(Xhtml + "img",
                        new XAttribute("src", pictureAddress(picture)),
                        new XAttribute("alt", picture.Alt),
                        // points → the pixels a browser counts in
                        new XAttribute("width", (int)Math.Round(width / 0.75)),
                        new XAttribute("height", (int)Math.Round(height / 0.75)))));
                    break;
                }

                case PageBreak:
                    parent.Add(new XElement(Xhtml + "div", new XAttribute("class", "page-break")));
                    break;

                case Rule:
                    parent.Add(new XElement(Xhtml + "hr"));
                    break;
            }
        }
    }

    internal static IEnumerable<XNode> Inlines(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is LineBreak)
            {
                yield return new XElement(Xhtml + "br");
                continue;
            }
            if (inline is not TextRun run || run.Text.Length == 0) continue;

            XNode node = new XText(Clean(run.Text));
            // innermost first
            if (run.Has(TextStyle.Code)) node = new XElement(Xhtml + "code", node);
            if (run.Has(TextStyle.Superscript)) node = new XElement(Xhtml + "sup", node);
            if (run.Has(TextStyle.Subscript)) node = new XElement(Xhtml + "sub", node);
            if (run.Has(TextStyle.Strike)) node = new XElement(Xhtml + "s", node);
            if (run.Has(TextStyle.Underline)) node = new XElement(Xhtml + "u", node);
            if (run.Has(TextStyle.Italic)) node = new XElement(Xhtml + "em", node);
            if (run.Has(TextStyle.Bold)) node = new XElement(Xhtml + "strong", node);
            if (run.Link != null) node = new XElement(Xhtml + "a", new XAttribute("href", run.Link), node);
            yield return node;
        }
    }

    /// <summary>Text without the characters XML has no way of writing.</summary>
    internal static string Clean(string text)
    {
        if (text.All(XmlConvert.IsXmlChar)) return text;
        var clean = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (XmlConvert.IsXmlChar(c)) clean.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) clean.Append(c).Append(text[++i]);
        }
        return clean.ToString();
    }
}
