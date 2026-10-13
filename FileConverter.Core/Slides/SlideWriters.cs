using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileConverter.Core.Documents;
using FileConverter.Core.Platform;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using QDocument = QuestPDF.Fluent.Document;
using TextStyle = FileConverter.Core.Documents.TextStyle;

namespace FileConverter.Core.Slides;

/// <summary>
/// Lays a presentation out as a PDF, a page per slide and of the slide's size: every text box,
/// picture and table where it stands on the slide. Text that does not fit its box at its size is
/// made smaller until it does, as a presentation program does. This is the built-in way —
/// PowerPoint or LibreOffice, where installed, draw the slides with their design.
/// </summary>
internal static class SlidePdfWriter
{
    public static void Write(Deck deck, string output, string fallbackTitle)
    {
        float width = (float)Math.Clamp(deck.Width, 72, 14400), height = (float)Math.Clamp(deck.Height, 72, 14400);
        var slides = deck.AllSlides;

        QDocument.Create(container =>
        {
            bool any = false;
            foreach (var slide in slides)
            {
                any = true;
                container.Page(page =>
                {
                    page.Size(new PageSize(width, height));
                    page.Margin(0);
                    page.DefaultTextStyle(style => style.FontSize(18).LineHeight(1.1f)
                        .FontFamily(["Lato", .. PlatformServices.Current.PdfFallbackFonts]));

                    page.Content().Layers(layers =>
                    {
                        layers.PrimaryLayer().Width(width).Height(height);
                        foreach (var item in slide.Items) Compose(layers.Layer(), item, width, height);
                    });
                });
            }
            if (!any) container.Page(page => page.Size(new PageSize(width, height)));
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = deck.Title ?? fallbackTitle,
            Creator = $"{AppInfo.DisplayName} {AppInfo.Version}",
            Producer = $"{AppInfo.DisplayName} {AppInfo.Version}"
        })
        .GeneratePdf(output);
    }

    private static void Compose(IContainer layer, SlideItem item, float slideWidth, float slideHeight)
    {
        // a box may reach over the slide's edge, but cannot be larger than the slide
        float boxWidth = (float)Math.Clamp(item.Width, 1, slideWidth), boxHeight = (float)Math.Clamp(item.Height, 1, slideHeight);
        float x = (float)Math.Clamp(item.X, -slideWidth, slideWidth), y = (float)Math.Clamp(item.Y, -slideHeight, slideHeight);
        var box = layer.OffsetX(x).OffsetY(y).Width(boxWidth).Height(boxHeight);

        switch (item)
        {
            case SlidePicture picture:
                box.Image(PictureData(picture.Picture)).UseOriginalImage().FitUnproportionally();
                break;

            case SlideText text:
            {
                var fitted = box.ScaleToFit();
                var aligned = text.Anchor switch
                {
                    VerticalAnchor.Middle => fitted.AlignMiddle(),
                    VerticalAnchor.Bottom => fitted.AlignBottom(),
                    _ => fitted.AlignTop()
                };
                aligned.Column(column =>
                {
                    foreach (var paragraph in text.Paragraphs)
                    {
                        float size = (float)paragraph.FontSize;
                        float indent = paragraph.Level * size * 1.2f;
                        var line = column.Item().PaddingBottom(size * 0.25f).PaddingLeft(indent);
                        if (paragraph.Bullet)
                        {
                            line.Row(row =>
                            {
                                row.ConstantItem(size * 0.9f).Text("•").FontSize(size);
                                row.RelativeItem().Text(run => Spans(run, paragraph, size));
                            });
                        }
                        else
                        {
                            line.Text(run => Spans(run, paragraph, size));
                        }
                    }
                });
                break;
            }

            case SlideTable table:
            {
                int columns = Math.Max(table.Rows.Max(row => row.Count), 1);
                box.ScaleToFit().Table(grid =>
                {
                    grid.ColumnsDefinition(definition =>
                    {
                        for (int c = 0; c < columns; c++) definition.RelativeColumn();
                    });
                    for (int r = 0; r < table.Rows.Count; r++)
                    {
                        for (int c = 0; c < columns; c++)
                        {
                            string cell = c < table.Rows[r].Count ? table.Rows[r][c] : "";
                            grid.Cell().Row((uint)(r + 1)).Column((uint)(c + 1)).Border(0.5f).BorderColor(Colors.Grey.Medium)
                                .Padding(3).Text(cell.Length == 0 ? " " : cell).FontSize((float)table.FontSize);
                        }
                    }
                });
                break;
            }
        }
    }

    private static void Spans(TextDescriptor text, SlideParagraph paragraph, float size)
    {
        switch (paragraph.Align)
        {
            case TextAlign.Center: text.AlignCenter(); break;
            case TextAlign.End: text.AlignRight(); break;
            case TextAlign.Justify: text.Justify(); break;
        }
        text.DefaultTextStyle(style => style.FontSize(size));

        bool any = false;
        foreach (var inline in paragraph.Runs)
        {
            if (inline is LineBreak)
            {
                text.Span("\n");
                continue;
            }
            if (inline is not TextRun run || run.Text.Length == 0) continue;
            any = true;
            var span = text.Span(run.Text.Replace("\t", "    "));
            if (run.Has(TextStyle.Bold)) span.Bold();
            if (run.Has(TextStyle.Italic)) span.Italic();
            if (run.Has(TextStyle.Underline)) span.Underline();
            if (run.Has(TextStyle.Strike)) span.Strikethrough();
            if (run.Has(TextStyle.Superscript)) span.Superscript();
            if (run.Has(TextStyle.Subscript)) span.Subscript();
        }
        if (!any) text.Span(" ");
    }

    /// <summary>The picture's data as the PDF library takes it: an animated GIF goes in as its first picture.</summary>
    internal static byte[] PictureData(Picture picture)
    {
        if (picture.Format != "gif") return picture.Data;
        using var bitmap = SKBitmap.Decode(picture.Data);
        if (bitmap == null) return picture.Data;
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}

/// <summary>
/// Writes a PowerPoint presentation (.pptx): slides of the given size with their text boxes,
/// pictures and tables in place, on a plain white master. Every slide is written as it comes, so
/// a presentation of many large pictures needs no more memory than one of few.
/// </summary>
internal static class PptxWriter
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    private const double UnitsPerPoint = 12700; // the format measures in EMU: 914 400 to an inch

    public static void Write(Deck deck, string output, Action<int>? slideWritten = null)
    {
        // PowerPoint takes slides from one inch to 56 inches a side
        double scale = Math.Min(1, 4032 / Math.Max(deck.Width, deck.Height));
        if (Math.Min(deck.Width, deck.Height) * scale < 72) scale = 72 / Math.Min(deck.Width, deck.Height);
        long width = Units(deck.Width * scale), height = Units(deck.Height * scale);

        using var file = PresentationDocument.Create(output, PresentationDocumentType.Presentation);
        var presentation = file.AddPresentationPart();
        var master = presentation.AddNewPart<SlideMasterPart>("rId1");
        var layout = master.AddNewPart<SlideLayoutPart>("rId1");
        layout.AddPart(master, "rId1");
        var theme = master.AddNewPart<ThemePart>("rId2");
        presentation.AddPart(theme, "rId2");

        Feed(theme, Theme());
        Feed(layout, new XElement(P + "sldLayout", Namespaces(), new XAttribute("type", "blank"), new XAttribute("preserve", "1"),
            new XElement(P + "cSld", new XAttribute("name", "Blank"), EmptyTree()),
            new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
        Feed(master, new XElement(P + "sldMaster", Namespaces(),
            new XElement(P + "cSld",
                new XElement(P + "bg", new XElement(P + "bgRef", new XAttribute("idx", "1001"), new XElement(A + "schemeClr", new XAttribute("val", "bg1")))),
                EmptyTree()),
            new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"), new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
                new XAttribute("accent1", "accent1"), new XAttribute("accent2", "accent2"), new XAttribute("accent3", "accent3"),
                new XAttribute("accent4", "accent4"), new XAttribute("accent5", "accent5"), new XAttribute("accent6", "accent6"),
                new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink")),
            new XElement(P + "sldLayoutIdLst", new XElement(P + "sldLayoutId", new XAttribute("id", "2147483649"), new XAttribute(R + "id", "rId1"))),
            new XElement(P + "txStyles", new XElement(P + "titleStyle"), new XElement(P + "bodyStyle"), new XElement(P + "otherStyle"))));

        var ids = new XElement(P + "sldIdLst");
        int number = 0;
        foreach (var slide in deck.AllSlides)
        {
            number++;
            var part = presentation.AddNewPart<SlidePart>($"rId{number + 2}");
            part.AddPart(layout, "rId1");
            Feed(part, SlideXml(slide, part, scale));
            ids.Add(new XElement(P + "sldId", new XAttribute("id", 255 + number), new XAttribute(R + "id", $"rId{number + 2}")));
            slideWritten?.Invoke(number);
        }
        if (number == 0)
        {
            var part = presentation.AddNewPart<SlidePart>("rId3");
            part.AddPart(layout, "rId1");
            Feed(part, SlideXml(new Slide(), part, scale));
            ids.Add(new XElement(P + "sldId", new XAttribute("id", 256), new XAttribute(R + "id", "rId3")));
        }

        Feed(presentation, new XElement(P + "presentation", Namespaces(),
            new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", "2147483648"), new XAttribute(R + "id", "rId1"))),
            ids,
            new XElement(P + "sldSz", new XAttribute("cx", width), new XAttribute("cy", height)),
            new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000))));

        if (deck.Title != null) file.PackageProperties.Title = deck.Title;
    }

    private static long Units(double points) => (long)Math.Round(points * UnitsPerPoint);

    private static object[] Namespaces() =>
        [new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), new XAttribute(XNamespace.Xmlns + "p", P)];

    private static void Feed(OpenXmlPart part, XElement root)
    {
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root).Save(writer);
    }

    /// <summary>The group every slide's shapes hang in, without anything in it yet.</summary>
    private static XElement EmptyTree() => new(P + "spTree",
        new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
        new XElement(P + "grpSpPr", new XElement(A + "xfrm",
            new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)),
            new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)))));

    private static XElement Frame(SlideItem item, double scale, string element = "xfrm", XNamespace? ns = null) => new((ns ?? A) + element,
        new XElement(A + "off", new XAttribute("x", Units(item.X * scale)), new XAttribute("y", Units(item.Y * scale))),
        new XElement(A + "ext", new XAttribute("cx", Math.Max(Units(item.Width * scale), 1)), new XAttribute("cy", Math.Max(Units(item.Height * scale), 1))));

    private static XElement SlideXml(Slide slide, SlidePart part, double scale)
    {
        var tree = EmptyTree();
        int id = 1, pictures = 0;
        foreach (var item in slide.Items)
        {
            id++;
            switch (item)
            {
                case SlidePicture picture:
                {
                    var image = part.AddImagePart(picture.Picture.Format switch
                    {
                        "jpg" => ImagePartType.Jpeg,
                        "gif" => ImagePartType.Gif,
                        _ => ImagePartType.Png
                    }, $"rId{++pictures + 1}");
                    using (var data = new MemoryStream(picture.Picture.Data)) image.FeedData(data);

                    tree.Add(new XElement(P + "pic",
                        new XElement(P + "nvPicPr",
                            new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Picture {id - 1}"), new XAttribute("descr", picture.Picture.Alt)),
                            new XElement(P + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1))),
                            new XElement(P + "nvPr")),
                        new XElement(P + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", $"rId{pictures + 1}")),
                            new XElement(A + "stretch", new XElement(A + "fillRect"))),
                        new XElement(P + "spPr", Frame(item, scale), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))));
                    break;
                }

                case SlideText text:
                    tree.Add(new XElement(P + "sp",
                        new XElement(P + "nvSpPr",
                            new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"{(text.IsTitle ? "Title" : "TextBox")} {id - 1}")),
                            new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
                        new XElement(P + "spPr", Frame(item, scale), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")), new XElement(A + "noFill")),
                        new XElement(P + "txBody",
                            new XElement(A + "bodyPr", new XAttribute("wrap", "square"), new XAttribute("lIns", 0), new XAttribute("tIns", 0),
                                new XAttribute("rIns", 0), new XAttribute("bIns", 0), new XAttribute("rtlCol", 0),
                                new XAttribute("anchor", text.Anchor switch { VerticalAnchor.Middle => "ctr", VerticalAnchor.Bottom => "b", _ => "t" }),
                                new XElement(A + "normAutofit")),
                            new XElement(A + "lstStyle"),
                            text.Paragraphs.Select(paragraph => ParagraphXml(paragraph, scale)))));
                    break;

                case SlideTable table:
                    tree.Add(TableXml(table, id, scale));
                    break;
            }
        }
        return new XElement(P + "sld", Namespaces(), new XElement(P + "cSld", tree), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")));
    }

    private static XElement ParagraphXml(SlideParagraph paragraph, double scale)
    {
        int size = (int)Math.Clamp(Math.Round(paragraph.FontSize * scale * 100), 100, 400000);
        long step = Units(paragraph.FontSize * scale * 1.2), hang = Units(paragraph.FontSize * scale * 0.9);
        var properties = new XElement(A + "pPr",
            new XAttribute("marL", paragraph.Level * step + (paragraph.Bullet ? hang : 0)),
            new XAttribute("indent", paragraph.Bullet ? -hang : 0),
            paragraph.Level > 0 ? new XAttribute("lvl", Math.Min(paragraph.Level, 8)) : null,
            new XAttribute("algn", paragraph.Align switch { TextAlign.Center => "ctr", TextAlign.End => "r", TextAlign.Justify => "just", _ => "l" }),
            paragraph.Bullet
                ? new object[] { new XElement(A + "buFont", new XAttribute("typeface", "Arial")), new XElement(A + "buChar", new XAttribute("char", "•")) }
                : new XElement(A + "buNone"));

        var element = new XElement(A + "p", properties);
        foreach (var inline in paragraph.Runs)
        {
            if (inline is LineBreak)
            {
                element.Add(new XElement(A + "br", new XElement(A + "rPr", new XAttribute("lang", "en-US"), new XAttribute("sz", size))));
                continue;
            }
            if (inline is not TextRun run || run.Text.Length == 0) continue;

            var runProperties = new XElement(A + "rPr", new XAttribute("lang", "en-US"), new XAttribute("sz", size));
            if (run.Has(TextStyle.Bold)) runProperties.Add(new XAttribute("b", 1));
            if (run.Has(TextStyle.Italic)) runProperties.Add(new XAttribute("i", 1));
            if (run.Has(TextStyle.Underline)) runProperties.Add(new XAttribute("u", "sng"));
            if (run.Has(TextStyle.Strike)) runProperties.Add(new XAttribute("strike", "sngStrike"));
            if (run.Has(TextStyle.Superscript)) runProperties.Add(new XAttribute("baseline", 30000));
            else if (run.Has(TextStyle.Subscript)) runProperties.Add(new XAttribute("baseline", -25000));
            runProperties.Add(new XAttribute("dirty", 0));
            element.Add(new XElement(A + "r", runProperties, new XElement(A + "t", XhtmlWriter.Clean(run.Text))));
        }
        // the size an empty paragraph takes up
        element.Add(new XElement(A + "endParaRPr", new XAttribute("lang", "en-US"), new XAttribute("sz", size), new XAttribute("dirty", 0)));
        return element;
    }

    private static XElement TableXml(SlideTable table, int id, double scale)
    {
        int columns = Math.Max(table.Rows.Max(row => row.Count), 1);
        long columnWidth = Math.Max(Units(table.Width * scale) / columns, 1), rowHeight = Math.Max(Units(table.Height * scale) / Math.Max(table.Rows.Count, 1), 1);
        int size = (int)Math.Clamp(Math.Round(table.FontSize * scale * 100), 100, 400000);

        XElement Line(string name) => new(A + name, new XAttribute("w", 6350), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", "808080"))));

        var grid = new XElement(A + "tblGrid");
        for (int c = 0; c < columns; c++) grid.Add(new XElement(A + "gridCol", new XAttribute("w", columnWidth)));
        var body = new XElement(A + "tbl", new XElement(A + "tblPr"), grid);
        foreach (var row in table.Rows)
        {
            var element = new XElement(A + "tr", new XAttribute("h", rowHeight));
            for (int c = 0; c < columns; c++)
            {
                var lines = (c < row.Count ? row[c] : "").Split('\n');
                element.Add(new XElement(A + "tc",
                    new XElement(A + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
                        lines.Select(line => new XElement(A + "p",
                            line.Length == 0 ? null : new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "en-US"), new XAttribute("sz", size), new XAttribute("dirty", 0)),
                                new XElement(A + "t", XhtmlWriter.Clean(line))),
                            new XElement(A + "endParaRPr", new XAttribute("lang", "en-US"), new XAttribute("sz", size), new XAttribute("dirty", 0))))),
                    new XElement(A + "tcPr", Line("lnL"), Line("lnR"), Line("lnT"), Line("lnB"))));
            }
            body.Add(element);
        }

        return new XElement(P + "graphicFrame",
            new XElement(P + "nvGraphicFramePr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Table {id - 1}")),
                new XElement(P + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noGrp", 1))),
                new XElement(P + "nvPr")),
            Frame(table, scale, "xfrm", P),
            new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/table"), body)));
    }

    /// <summary>The colours, fonts and effects a presentation file has to name, kept to the plainest there are.</summary>
    private static XElement Theme()
    {
        XElement Colour(string name, string value) => new(A + name, new XElement(A + "srgbClr", new XAttribute("val", value)));
        XElement Font(string name) => new(A + name,
            new XElement(A + "latin", new XAttribute("typeface", "Calibri")), new XElement(A + "ea", new XAttribute("typeface", "")), new XElement(A + "cs", new XAttribute("typeface", "")));
        XElement Fill() => new(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr")));
        XElement Line(int width) => new(A + "ln", new XAttribute("w", width), Fill(), new XElement(A + "prstDash", new XAttribute("val", "solid")));
        XElement Effect() => new(A + "effectStyle", new XElement(A + "effectLst"));

        return new XElement(A + "theme", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute("name", "Plain"),
            new XElement(A + "themeElements",
                new XElement(A + "clrScheme", new XAttribute("name", "Plain"),
                    new XElement(A + "dk1", new XElement(A + "sysClr", new XAttribute("val", "windowText"), new XAttribute("lastClr", "000000"))),
                    new XElement(A + "lt1", new XElement(A + "sysClr", new XAttribute("val", "window"), new XAttribute("lastClr", "FFFFFF"))),
                    Colour("dk2", "44546A"), Colour("lt2", "E7E6E6"), Colour("accent1", "4472C4"), Colour("accent2", "ED7D31"), Colour("accent3", "A5A5A5"),
                    Colour("accent4", "FFC000"), Colour("accent5", "5B9BD5"), Colour("accent6", "70AD47"), Colour("hlink", "0563C1"), Colour("folHlink", "954F72")),
                new XElement(A + "fontScheme", new XAttribute("name", "Plain"), Font("majorFont"), Font("minorFont")),
                new XElement(A + "fmtScheme", new XAttribute("name", "Plain"),
                    new XElement(A + "fillStyleLst", Fill(), Fill(), Fill()),
                    new XElement(A + "lnStyleLst", Line(6350), Line(12700), Line(19050)),
                    new XElement(A + "effectStyleLst", Effect(), Effect(), Effect()),
                    new XElement(A + "bgFillStyleLst", Fill(), Fill(), Fill()))));
    }
}

/// <summary>
/// Writes an OpenDocument presentation (.odp): slides of the given size with their text boxes
/// and pictures in place, bulleted lines as lists with their levels. (A table is written as lines of text.)
/// </summary>
internal static class OdpWriter
{
    /// <summary>How much further in each level of a bulleted list is set, and the room its bullet takes, in points.</summary>
    private const double LevelStep = 27, BulletWidth = 22;

    public static void Write(Deck deck, string output, Action<int>? slideWritten = null)
    {
        using var package = new OdfPackage(output, "application/vnd.oasis.opendocument.presentation");
        var automatic = new XElement(Odf.Office + "automatic-styles",
            new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", "dp1"), new XAttribute(Odf.Style + "family", "drawing-page")),
            new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", "gr_picture"), new XAttribute(Odf.Style + "family", "graphic"),
                new XElement(Odf.Style + "graphic-properties", new XAttribute(Odf.Draw + "stroke", "none"), new XAttribute(Odf.Draw + "fill", "none"))));
        foreach (var (name, anchor) in new[] { ("gr_top", "top"), ("gr_middle", "middle"), ("gr_bottom", "bottom") })
            automatic.Add(new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", name), new XAttribute(Odf.Style + "family", "graphic"),
                new XElement(Odf.Style + "graphic-properties", new XAttribute(Odf.Draw + "stroke", "none"), new XAttribute(Odf.Draw + "fill", "none"),
                    new XAttribute(Odf.Draw + "textarea-vertical-align", anchor), new XAttribute(Odf.Draw + "auto-grow-height", "false"),
                    new XAttribute(Odf.Draw + "fit-to-size", "shrink-to-fit"),
                    new XAttribute(Odf.Fo + "padding-top", "0in"), new XAttribute(Odf.Fo + "padding-bottom", "0in"),
                    new XAttribute(Odf.Fo + "padding-left", "0in"), new XAttribute(Odf.Fo + "padding-right", "0in"))));

        // one list style for every bulleted line: a bullet per level, each level set further in
        var bullets = new XElement(Odf.Text + "list-style", new XAttribute(Odf.Style + "name", "L_bullets"));
        for (int level = 1; level <= 10; level++)
            bullets.Add(new XElement(Odf.Text + "list-level-style-bullet", new XAttribute(Odf.Text + "level", level), new XAttribute(Odf.Text + "bullet-char", "•"),
                // (how far in a level stands is said by its paragraphs' margin, which both LibreOffice and PowerPoint follow)
                new XElement(Odf.Style + "list-level-properties", new XAttribute(Odf.Text + "space-before", "0in"),
                    new XAttribute(Odf.Text + "min-label-width", OdfPackage.Length(BulletWidth))),
                new XElement(Odf.Style + "text-properties", new XAttribute(Odf.Fo + "font-family", "Arial"), new XAttribute(Odf.Style + "use-window-font-color", "true"),
                    new XAttribute(Odf.Fo + "font-size", "100%"))));
        automatic.Add(bullets);

        var paragraphStyles = new Dictionary<string, string>(StringComparer.Ordinal);
        var textStyles = new Dictionary<string, string>(StringComparer.Ordinal);

        string ParagraphStyle(SlideParagraph paragraph)
        {
            // a bulleted line stands further in for every level, its bullet hanging out to the left of the text
            double hang = paragraph.Bullet ? BulletWidth : 0;
            double indent = paragraph.Bullet ? paragraph.Level * LevelStep : paragraph.Level * paragraph.FontSize * 1.2;
            string align = paragraph.Align switch { TextAlign.Center => "center", TextAlign.End => "end", TextAlign.Justify => "justify", _ => "start" };
            string key = FormattableString.Invariant($"{align}|{indent + hang:0.#}|{hang:0.#}|{paragraph.FontSize:0.#}");
            if (paragraphStyles.TryGetValue(key, out var name)) return name;
            paragraphStyles[key] = name = $"P{paragraphStyles.Count + 1}";
            automatic.Add(new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", name), new XAttribute(Odf.Style + "family", "paragraph"),
                new XElement(Odf.Style + "paragraph-properties", new XAttribute(Odf.Fo + "text-align", align),
                    new XAttribute(Odf.Fo + "margin-left", OdfPackage.Length(indent + hang)), new XAttribute(Odf.Fo + "text-indent", OdfPackage.Length(-hang)),
                    new XAttribute(Odf.Fo + "margin-bottom", OdfPackage.Length(paragraph.FontSize * 0.25))),
                new XElement(Odf.Style + "text-properties", Size(paragraph.FontSize))));
            return name;
        }

        string TextStyleName(TextStyle style, double size)
        {
            string key = FormattableString.Invariant($"{(int)style}|{size:0.#}");
            if (textStyles.TryGetValue(key, out var name)) return name;
            textStyles[key] = name = $"T{textStyles.Count + 1}";
            var properties = new XElement(Odf.Style + "text-properties", Size(size));
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
            automatic.Add(new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", name), new XAttribute(Odf.Style + "family", "text"), properties));
            return name;
        }

        var presentation = new XElement(Odf.Office + "presentation");
        int number = 0, pictures = 0;
        foreach (var slide in deck.AllSlides)
        {
            number++;
            var page = new XElement(Odf.Draw + "page", new XAttribute(Odf.Draw + "name", $"Slide {number}"),
                new XAttribute(Odf.Draw + "style-name", "dp1"), new XAttribute(Odf.Draw + "master-page-name", "Default"));

            foreach (var item in slide.Items)
            {
                var frame = new XElement(Odf.Draw + "frame", new XAttribute(Odf.Draw + "layer", "layout"),
                    new XAttribute(Odf.Svg + "x", OdfPackage.Length(item.X)), new XAttribute(Odf.Svg + "y", OdfPackage.Length(item.Y)),
                    new XAttribute(Odf.Svg + "width", OdfPackage.Length(Math.Max(item.Width, 1))), new XAttribute(Odf.Svg + "height", OdfPackage.Length(Math.Max(item.Height, 1))));

                switch (item)
                {
                    case SlidePicture picture:
                        frame.Add(new XAttribute(Odf.Draw + "style-name", "gr_picture"),
                            new XElement(Odf.Draw + "image", new XAttribute(Odf.XLink + "href", package.AddPicture(picture.Picture, ++pictures)),
                                new XAttribute(Odf.XLink + "type", "simple"), new XAttribute(Odf.XLink + "show", "embed"), new XAttribute(Odf.XLink + "actuate", "onLoad")));
                        break;

                    case SlideText text:
                    {
                        var box = new XElement(Odf.Draw + "text-box");
                        foreach (var paragraph in text.Paragraphs)
                        {
                            var element = new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", ParagraphStyle(paragraph)));
                            foreach (var inline in paragraph.Runs)
                            {
                                if (inline is LineBreak) element.Add(new XElement(Odf.Text + "line-break"));
                                else if (inline is TextRun { Text.Length: > 0 } run)
                                    element.Add(new XElement(Odf.Text + "span", new XAttribute(Odf.Text + "style-name", TextStyleName(run.Style, paragraph.FontSize)), OdtWriter.Spaced(run.Text)));
                            }
                            if (!paragraph.Bullet)
                            {
                                box.Add(element);
                                continue;
                            }

                            // A line on the third level is an item of a list in a list in a list: that is how the format says "level".
                            XElement entry = new(Odf.Text + "list-item", element);
                            for (int level = Math.Min(paragraph.Level, 9); level > 0; level--)
                                entry = new XElement(Odf.Text + "list-item", new XElement(Odf.Text + "list", entry));
                            box.Add(new XElement(Odf.Text + "list", new XAttribute(Odf.Text + "style-name", "L_bullets"), entry));
                        }
                        frame.Add(new XAttribute(Odf.Draw + "style-name", text.Anchor switch { VerticalAnchor.Middle => "gr_middle", VerticalAnchor.Bottom => "gr_bottom", _ => "gr_top" }), box);
                        break;
                    }

                    case SlideTable table:
                    {
                        var box = new XElement(Odf.Draw + "text-box");
                        var style = ParagraphStyle(new SlideParagraph { FontSize = table.FontSize });
                        foreach (var row in table.Rows)
                            box.Add(new XElement(Odf.Text + "p", new XAttribute(Odf.Text + "style-name", style), OdtWriter.Spaced(string.Join("\t", row.Select(cell => cell.Replace('\n', ' '))))));
                        frame.Add(new XAttribute(Odf.Draw + "style-name", "gr_top"), box);
                        break;
                    }
                }
                page.Add(frame);
            }
            presentation.Add(page);
            slideWritten?.Invoke(number);
        }
        if (number == 0)
            presentation.Add(new XElement(Odf.Draw + "page", new XAttribute(Odf.Draw + "name", "Slide 1"),
                new XAttribute(Odf.Draw + "style-name", "dp1"), new XAttribute(Odf.Draw + "master-page-name", "Default")));

        package.AddXml("content.xml", new XDocument(OdfPackage.Root("document-content", automatic, new XElement(Odf.Office + "body", presentation))));
        package.AddXml("styles.xml", new XDocument(OdfPackage.Root("document-styles",
            new XElement(Odf.Office + "styles",
                new XElement(Odf.Style + "default-style", new XAttribute(Odf.Style + "family", "graphic"),
                    new XElement(Odf.Style + "text-properties", new XAttribute(Odf.Fo + "font-family", "'Liberation Sans', Arial"), Size(18)))),
            new XElement(Odf.Office + "automatic-styles",
                new XElement(Odf.Style + "page-layout", new XAttribute(Odf.Style + "name", "PM1"),
                    new XElement(Odf.Style + "page-layout-properties",
                        new XAttribute(Odf.Fo + "margin-top", "0in"), new XAttribute(Odf.Fo + "margin-bottom", "0in"),
                        new XAttribute(Odf.Fo + "margin-left", "0in"), new XAttribute(Odf.Fo + "margin-right", "0in"),
                        new XAttribute(Odf.Fo + "page-width", OdfPackage.Length(deck.Width)), new XAttribute(Odf.Fo + "page-height", OdfPackage.Length(deck.Height)),
                        new XAttribute(Odf.Style + "print-orientation", deck.Width >= deck.Height ? "landscape" : "portrait")))),
            new XElement(Odf.Office + "master-styles",
                new XElement(Odf.Draw + "layer-set",
                    new[] { "layout", "background", "backgroundobjects", "controls", "measurelines" }.Select(layer => new XElement(Odf.Draw + "layer", new XAttribute(Odf.Draw + "name", layer)))),
                new XElement(Odf.Style + "master-page", new XAttribute(Odf.Style + "name", "Default"), new XAttribute(Odf.Style + "page-layout-name", "PM1"))))));
        package.AddMeta(deck.Title, null);
    }

    private static XAttribute[] Size(double points)
    {
        string size = points.ToString("0.#", CultureInfo.InvariantCulture) + "pt";
        return [new(Odf.Fo + "font-size", size), new(Odf.Style + "font-size-asian", size), new(Odf.Style + "font-size-complex", size)];
    }
}
