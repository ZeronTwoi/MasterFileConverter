using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using FileConverter.Core.Documents;

namespace FileConverter.Core.Slides;

/// <summary>
/// Reads an OpenDocument presentation (.odp, LibreOffice Impress's format): every slide's text
/// boxes, pictures and tables with their places on the slide.
/// </summary>
internal sealed class OdpReader
{
    private sealed record StyleInfo(string? Parent, TextStyle Style, double? FontSize, TextAlign? Align);

    private readonly ZipArchive _zip;
    private readonly Dictionary<string, StyleInfo> _styles = new(StringComparer.Ordinal);

    private OdpReader(ZipArchive zip) => _zip = zip;

    public static Deck ReadFile(string path)
    {
        using var zip = Odf.Open(path);
        var content = Odf.ReadXml(zip, "content.xml") ?? throw new FriendlyException("This OpenDocument file is damaged.");
        var presentation = content.Root?.Element(Odf.Office + "body")?.Element(Odf.Office + "presentation")
            ?? throw new FriendlyException("This file is not an OpenDocument presentation.");

        var styles = Odf.ReadXml(zip, "styles.xml");
        var reader = new OdpReader(zip);
        reader.ReadStyles(styles);
        reader.ReadStyles(content);

        var deck = new Deck
        {
            Title = Odf.ReadXml(zip, "meta.xml")?.Descendants(Odf.Dc + "title").FirstOrDefault()?.Value is { Length: > 0 } title ? title : null
        };

        // the slide size is that of the page layout the first master page names
        var layoutName = styles?.Descendants(Odf.Style + "master-page").FirstOrDefault()?.Attribute(Odf.Style + "page-layout-name")?.Value;
        var layout = styles?.Descendants(Odf.Style + "page-layout").FirstOrDefault(l => l.Attribute(Odf.Style + "name")?.Value == layoutName)
            ?.Element(Odf.Style + "page-layout-properties");
        if (Odf.Points(layout?.Attribute(Odf.Fo + "page-width")?.Value) is { } width and > 0 &&
            Odf.Points(layout?.Attribute(Odf.Fo + "page-height")?.Value) is { } height and > 0)
        {
            deck.Width = width;
            deck.Height = height;
        }

        foreach (var page in presentation.Elements(Odf.Draw + "page"))
        {
            var slide = new Slide();
            reader.ReadShapes(page, slide, 0, 0);
            deck.Slides.Add(slide);
        }
        return deck;
    }

    private void ReadStyles(XDocument? source)
    {
        foreach (var style in source?.Root?.Descendants(Odf.Style + "style") ?? [])
        {
            var name = style.Attribute(Odf.Style + "name")?.Value;
            if (name == null) continue;

            var text = style.Element(Odf.Style + "text-properties");
            var flags = TextStyle.None;
            double? size = null;
            if (text != null)
            {
                if (text.Attribute(Odf.Fo + "font-weight")?.Value is "bold" or "600" or "700" or "800" or "900") flags |= TextStyle.Bold;
                if (text.Attribute(Odf.Fo + "font-style")?.Value is "italic" or "oblique") flags |= TextStyle.Italic;
                if (text.Attribute(Odf.Style + "text-underline-style")?.Value is { } underline and not "none") flags |= TextStyle.Underline;
                if (text.Attribute(Odf.Style + "text-line-through-style")?.Value is { } strike and not "none") flags |= TextStyle.Strike;
                size = Odf.Points(text.Attribute(Odf.Fo + "font-size")?.Value);
            }
            TextAlign? align = style.Element(Odf.Style + "paragraph-properties")?.Attribute(Odf.Fo + "text-align")?.Value switch
            {
                "center" => TextAlign.Center,
                "end" or "right" => TextAlign.End,
                "justify" => TextAlign.Justify,
                "start" or "left" => TextAlign.Start,
                _ => null
            };
            _styles[name] = new StyleInfo(style.Attribute(Odf.Style + "parent-style-name")?.Value, flags, size, align);
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

    // ---------- shapes ----------

    private void ReadShapes(XElement container, Slide slide, double offsetX, double offsetY)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == Odf.Draw + "g")
            {
                ReadShapes(element, slide, offsetX, offsetY);
                continue;
            }
            if (element.Name.Namespace != Odf.Draw) continue;
            // the date, the footer and the page number a master puts on every slide
            if (element.Attribute(Odf.Presentation + "class")?.Value is "date-time" or "footer" or "page-number" or "header" or "notes") continue;

            double x = (Odf.Points(element.Attribute(Odf.Svg + "x")?.Value) ?? 0) + offsetX, y = (Odf.Points(element.Attribute(Odf.Svg + "y")?.Value) ?? 0) + offsetY;
            double width = Odf.Points(element.Attribute(Odf.Svg + "width")?.Value) ?? 0, height = Odf.Points(element.Attribute(Odf.Svg + "height")?.Value) ?? 0;
            if (width <= 0 || height <= 0) continue;

            string role = element.Attribute(Odf.Presentation + "class")?.Value ?? "";
            bool title = role is "title" or "subtitle";

            if (element.Name == Odf.Draw + "frame")
            {
                if (element.Element(Odf.Draw + "image") != null)
                {
                    if (Odf.ReadPicture(_zip, element) is { } picture)
                        slide.Items.Add(new SlidePicture(picture) { X = x, Y = y, Width = width, Height = height });
                }
                else if (element.Element(Odf.Table + "table") is { } table)
                {
                    var read = new SlideTable { X = x, Y = y, Width = width, Height = height };
                    foreach (var row in table.Elements(Odf.Table + "table-row"))
                        read.Rows.Add(row.Elements(Odf.Table + "table-cell").Select(cell =>
                            string.Join("\n", cell.Elements(Odf.Text + "p").Select(p => p.Value)).Trim()).ToList());
                    if (read.Rows.Count > 0) slide.Items.Add(read);
                }
                else if (element.Element(Odf.Draw + "text-box") is { } box)
                {
                    AddText(box, slide, x, y, width, height, title, role is "outline" or "object" ? 28 : title ? 40 : 18);
                }
            }
            else
            {
                // a drawn shape: what is written in it
                AddText(element, slide, x, y, width, height, isTitle: false, defaultSize: 18);
            }
        }
    }

    private void AddText(XElement container, Slide slide, double x, double y, double width, double height, bool isTitle, double defaultSize)
    {
        var text = new SlideText { X = x, Y = y, Width = width, Height = height, IsTitle = isTitle, Anchor = isTitle ? VerticalAnchor.Middle : VerticalAnchor.Top };
        ReadParagraphs(container, text, level: 0, bullet: false, defaultSize);
        while (text.Paragraphs.Count > 0 && text.Paragraphs[^1].Runs.Count == 0) text.Paragraphs.RemoveAt(text.Paragraphs.Count - 1);
        if (!text.IsEmpty) slide.Items.Add(text);
    }

    private void ReadParagraphs(XElement container, SlideText text, int level, bool bullet, double defaultSize)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == Odf.Text + "p" || element.Name == Odf.Text + "h")
            {
                var styleName = element.Attribute(Odf.Text + "style-name")?.Value;
                var builder = new BlockBuilder();
                double? size = Chain(styleName).Select(s => s.FontSize).FirstOrDefault(s => s != null);
                ReadInlines(element, builder, Flags(styleName), ref size);

                var paragraph = new SlideParagraph
                {
                    Level = level,
                    Bullet = bullet,
                    // every level of an outline is set a little smaller than the one above it
                    FontSize = Math.Clamp(size ?? Math.Max(defaultSize - 4 * level, 12), 4, 400),
                    Align = Chain(styleName).Select(s => s.Align).FirstOrDefault(a => a != null) ?? (text.IsTitle ? TextAlign.Center : TextAlign.Start)
                };
                paragraph.Runs.AddRange(builder.TakeInlines());
                text.Paragraphs.Add(paragraph);
            }
            else if (element.Name == Odf.Text + "list")
            {
                foreach (var item in element.Elements().Where(e => e.Name == Odf.Text + "list-item" || e.Name == Odf.Text + "list-header"))
                    ReadParagraphs(item, text, level: bullet ? level + 1 : level, bullet: true, defaultSize);
            }
        }
    }

    private TextStyle Flags(string? styleName)
    {
        var flags = TextStyle.None;
        foreach (var style in Chain(styleName)) flags |= style.Style;
        return flags;
    }

    private void ReadInlines(XElement container, BlockBuilder builder, TextStyle style, ref double? size)
    {
        foreach (var node in container.Nodes())
        {
            if (node is XText plain)
            {
                HtmlReader.AddText(plain.Value, builder, style, null);
                continue;
            }
            if (node is not XElement element) continue;

            if (element.Name == Odf.Text + "span")
            {
                var name = element.Attribute(Odf.Text + "style-name")?.Value;
                // the size of the paragraph's first run stands for the paragraph
                size ??= Chain(name).Select(s => s.FontSize).FirstOrDefault(s => s != null);
                ReadInlines(element, builder, style | Flags(name), ref size);
            }
            else if (element.Name == Odf.Text + "s")
                builder.Text(new string(' ', int.TryParse(element.Attribute(Odf.Text + "c")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? Math.Clamp(count, 1, 1000) : 1), style);
            else if (element.Name == Odf.Text + "tab")
                builder.Text("\t", style);
            else if (element.Name == Odf.Text + "line-break")
                builder.Break();
            else if (element.Name.Namespace == Odf.Text)
                ReadInlines(element, builder, style, ref size); // links and fields: the text they show
        }
    }
}
