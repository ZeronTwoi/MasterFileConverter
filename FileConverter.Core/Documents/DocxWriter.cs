using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace FileConverter.Core.Documents;

/// <summary>
/// Writes a document as a Word file (.docx) that Word treats as one of its own: headings are
/// real headings (they show in the navigation pane and can be restyled at once), lists are real
/// lists, tables are tables, pictures are stored inside the file.
/// </summary>
internal sealed class DocxWriter
{
    private const int BulletDefinition = 1, NumberDefinition = 2;

    /// <summary>The width of the text on an A4 page with one-inch margins, in points.</summary>
    private const double TextWidthPt = (11906 - 2 * 1440) / 20.0;

    private readonly MainDocumentPart _main;
    private readonly Numbering _numbering;
    private readonly Dictionary<Picture, string> _pictures = new(ReferenceEqualityComparer.Instance);
    private int _nextNumberingId = 1;
    private uint _nextDrawingId = 1;

    private DocxWriter(MainDocumentPart main, Numbering numbering)
    {
        _main = main;
        _numbering = numbering;
    }

    public static void Write(RichDocument document, string output)
    {
        using var file = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var main = file.AddMainDocumentPart();
        main.AddNewPart<StyleDefinitionsPart>().Styles = Styles();
        var numbering = NumberingDefinitions();
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = numbering;

        var writer = new DocxWriter(main, numbering);
        var body = new Body();
        writer.Append(body, document.Blocks, indentTwips: 0, list: null);
        // a document must not end in a table, and an empty one still needs a paragraph
        if (body.LastChild is null or W.Table) body.Append(new W.Paragraph());
        body.Append(new SectionProperties(
            new W.PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1440, Right = 1440, Bottom = 1440, Left = 1440, Header = 708, Footer = 708, Gutter = 0 }));

        main.Document = new Document(body);
        main.Document.Save();

        if (document.Title != null) file.PackageProperties.Title = document.Title;
        if (document.Author != null) file.PackageProperties.Creator = document.Author;
    }

    // ---------- blocks ----------

    /// <summary>The list a paragraph is an item of: which numbering, and how deep.</summary>
    private sealed record ListPlace(int NumberingId, int Level);

    private void Append(OpenXmlCompositeElement parent, IEnumerable<Block> blocks, int indentTwips, ListPlace? list)
    {
        // only the first paragraph of a list item carries the bullet or number
        bool first = true;
        foreach (var block in blocks)
        {
            var place = first ? list : null;
            int indent = list != null && !first ? indentTwips + 720 * (list.Level + 1) : indentTwips;
            first = false;

            switch (block)
            {
                case Heading heading:
                {
                    var paragraph = new W.Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{heading.Level}" }));
                    AppendInlines(paragraph, heading.Inlines);
                    parent.Append(paragraph);
                    break;
                }

                case Paragraph text:
                {
                    var properties = new ParagraphProperties();
                    if (place != null)
                    {
                        properties.Append(new ParagraphStyleId { Val = "ListParagraph" });
                        properties.Append(new NumberingProperties(
                            new NumberingLevelReference { Val = place.Level }, new NumberingId { Val = place.NumberingId }));
                    }
                    else if (indent > 0)
                    {
                        properties.Append(new Indentation { Left = indent.ToString() });
                    }
                    if (text.Align != TextAlign.Start)
                        properties.Append(new Justification
                        {
                            Val = text.Align switch
                            {
                                TextAlign.Center => JustificationValues.Center,
                                TextAlign.End => JustificationValues.Right,
                                _ => JustificationValues.Both
                            }
                        });
                    var paragraph = new W.Paragraph();
                    if (properties.HasChildren) paragraph.Append(properties);
                    AppendInlines(paragraph, text.Inlines);
                    parent.Append(paragraph);
                    break;
                }

                case ListBlock items:
                {
                    int level = list == null ? 0 : Math.Min(list.Level + 1, 8);
                    int id = NewNumbering(items.Ordered, level, items.Start);
                    foreach (var item in items.Items)
                    {
                        // an item that starts with something other than text still needs a paragraph to carry its bullet
                        var content = item.Count > 0 && item[0] is Paragraph ? item : [new Paragraph(""), .. item];
                        Append(parent, content, indentTwips, new ListPlace(id, level));
                    }
                    break;
                }

                case CodeBlock code:
                {
                    var properties = new ParagraphProperties(new ParagraphStyleId { Val = "Code" });
                    if (indent > 0) properties.Append(new Indentation { Left = indent.ToString() });
                    var paragraph = new W.Paragraph(properties);
                    var run = new W.Run();
                    var lines = code.Text.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) run.Append(new W.Break());
                        AppendText(run, lines[i]);
                    }
                    paragraph.Append(run);
                    parent.Append(paragraph);
                    break;
                }

                case Quote quote:
                    Append(parent, quote.Blocks, indent + 720, list: null);
                    break;

                case Table table:
                    parent.Append(BuildTable(table, indent));
                    // two tables in a row would grow together, and a cell must not end in a table
                    parent.Append(new W.Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" })));
                    break;

                case Picture picture:
                {
                    var properties = new ParagraphProperties();
                    if (indent > 0) properties.Append(new Indentation { Left = indent.ToString() });
                    var paragraph = new W.Paragraph();
                    if (properties.HasChildren) paragraph.Append(properties);
                    if (BuildPicture(picture, TextWidthPt - indent / 20.0) is { } drawing) paragraph.Append(new W.Run(drawing));
                    parent.Append(paragraph);
                    break;
                }

                case PageBreak:
                    parent.Append(new W.Paragraph(new W.Run(new W.Break { Type = BreakValues.Page })));
                    break;

                case Rule:
                    parent.Append(new W.Paragraph(new ParagraphProperties(new ParagraphBorders(
                        new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "999999" }))));
                    break;
            }
        }
    }

    private W.Table BuildTable(Table source, int indentTwips)
    {
        int columns = Math.Max(source.ColumnCount, 1);
        var table = new W.Table();
        var properties = new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });
        if (indentTwips > 0) properties.Append(new TableIndentation { Width = indentTwips, Type = TableWidthUnitValues.Dxa });
        properties.Append(new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new RightBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "808080" },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "808080" }));
        table.Append(properties);

        int columnWidth = (int)(TextWidthPt * 20 - indentTwips) / columns;
        var grid = new TableGrid();
        for (int i = 0; i < columns; i++) grid.Append(new GridColumn { Width = columnWidth.ToString() });
        table.Append(grid);

        for (int r = 0; r < source.Rows.Count; r++)
        {
            bool heading = r < source.HeaderRows;
            var row = new W.TableRow();
            if (heading) row.Append(new TableRowProperties(new TableHeader()));

            int used = 0;
            foreach (var cell in source.Rows[r])
            {
                int span = Math.Clamp(cell.ColumnSpan, 1, columns - used > 0 ? columns - used : 1);
                used += span;
                var cellProperties = new TableCellProperties(new TableCellWidth { Width = (columnWidth * span).ToString(), Type = TableWidthUnitValues.Dxa });
                if (span > 1) cellProperties.Append(new GridSpan { Val = span });
                if (heading) cellProperties.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "EEEEEE" });

                var target = new W.TableCell(cellProperties);
                Append(target, heading ? Embolden(cell.Blocks) : cell.Blocks, indentTwips: 0, list: null);
                // a cell has to end in a paragraph
                if (target.LastChild is not W.Paragraph) target.Append(new W.Paragraph());
                row.Append(target);
            }
            // a row shorter than the table is filled up, or Word shows a ragged edge
            for (; used < columns; used++)
                row.Append(new W.TableCell(new TableCellProperties(new TableCellWidth { Width = columnWidth.ToString(), Type = TableWidthUnitValues.Dxa }), new W.Paragraph()));
            table.Append(row);
        }
        return table;
    }

    private static List<Block> Embolden(List<Block> blocks) => blocks.Select(block => block is Paragraph paragraph
        ? new Paragraph(paragraph.Inlines.Select(inline => inline is TextRun run
            ? new TextRun(run.Text, run.Style | TextStyle.Bold, run.Link)
            : inline).ToList()) { Align = paragraph.Align }
        : block).ToList();

    // ---------- text ----------

    private void AppendInlines(W.Paragraph paragraph, IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is LineBreak)
            {
                paragraph.Append(new W.Run(new W.Break()));
                continue;
            }
            if (inline is not TextRun text || text.Text.Length == 0) continue;

            var run = new W.Run();
            var properties = new RunProperties();
            // in the order the format wants them
            if (text.Link != null) properties.Append(new RunStyle { Val = "Hyperlink" });
            if (text.Has(TextStyle.Code)) properties.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" });
            if (text.Has(TextStyle.Bold)) properties.Append(new Bold());
            if (text.Has(TextStyle.Italic)) properties.Append(new Italic());
            if (text.Has(TextStyle.Strike)) properties.Append(new Strike());
            if (text.Has(TextStyle.Underline)) properties.Append(new Underline { Val = UnderlineValues.Single });
            if (text.Has(TextStyle.Superscript)) properties.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });
            else if (text.Has(TextStyle.Subscript)) properties.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript });
            if (properties.HasChildren) run.Append(properties);
            AppendText(run, text.Text);

            if (text.Link != null && LinkId(text.Link) is { } id)
                paragraph.Append(new W.Hyperlink(run) { Id = id, History = true });
            else
                paragraph.Append(run);
        }
    }

    /// <summary>Text into a run; tabs and line ends are elements of their own in this format.</summary>
    private static void AppendText(W.Run run, string text)
    {
        text = XhtmlWriter.Clean(text);
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('\t' or '\n')) continue;
            if (i > start) run.Append(new W.Text(text[start..i]) { Space = SpaceProcessingModeValues.Preserve });
            if (i < text.Length) run.Append(text[i] == '\t' ? new TabChar() : new W.Break());
            start = i + 1;
        }
    }

    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);

    private string? LinkId(string address)
    {
        if (_links.TryGetValue(address, out var known)) return known;
        // Word refuses a whole document over one address it cannot read: only plain web and mail addresses become links
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto" or "ftp")) return null;
        var id = _main.AddHyperlinkRelationship(uri, isExternal: true).Id;
        _links[address] = id;
        return id;
    }

    // ---------- lists ----------

    private int NewNumbering(bool ordered, int level, int start)
    {
        int id = _nextNumberingId++;
        var instance = new NumberingInstance(new AbstractNumId { Val = ordered ? NumberDefinition : BulletDefinition }) { NumberID = id };
        // every numbered list counts from its own beginning
        if (ordered) instance.Append(new LevelOverride(new StartOverrideNumberingValue { Val = start }) { LevelIndex = level });
        _numbering.Append(instance);
        return id;
    }

    private static Numbering NumberingDefinitions()
    {
        string[] bullets = ["•", "◦", "▪"];
        var bulleted = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = BulletDefinition };
        var numbered = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = NumberDefinition };
        for (int level = 0; level < 9; level++)
        {
            var indent = new PreviousParagraphProperties(new Indentation { Left = (720 * (level + 1)).ToString(), Hanging = "360" });
            bulleted.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = bullets[level % bullets.Length] },
                new LevelJustification { Val = LevelJustificationValues.Left },
                indent.CloneNode(true)) { LevelIndex = level });
            numbered.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = (level % 3) switch { 0 => NumberFormatValues.Decimal, 1 => NumberFormatValues.LowerLetter, _ => NumberFormatValues.LowerRoman } },
                new LevelText { Val = $"%{level + 1}." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                indent.CloneNode(true)) { LevelIndex = level });
        }
        return new Numbering(bulleted, numbered);
    }

    // ---------- pictures ----------

    private W.Drawing? BuildPicture(Picture picture, double maxWidthPt)
    {
        if (!_pictures.TryGetValue(picture, out var relationship))
        {
            var part = _main.AddImagePart(picture.Format switch
            {
                "jpg" => ImagePartType.Jpeg,
                "gif" => ImagePartType.Gif,
                _ => ImagePartType.Png
            });
            using (var data = new MemoryStream(picture.Data)) part.FeedData(data);
            relationship = _main.GetIdOfPart(part);
            _pictures[picture] = relationship;
        }

        var (width, height) = picture.DisplaySize(Math.Max(maxWidthPt, 36), 650);
        long cx = (long)(width * 12700), cy = (long)(height * 12700); // points → the format's own unit
        uint id = _nextDrawingId++;

        return new W.Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = $"Picture {id}", Description = picture.Alt },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = id, Name = $"image{id}.{picture.Format}" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationship }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0
        });
    }

    // ---------- styles ----------

    private static Styles Styles()
    {
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", EastAsia = "Calibri", ComplexScript = "Calibri" },
                    new FontSize { Val = "22" }, new FontSizeComplexScript { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "160", Line = "259", LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });

        // the sizes Word's own headings have, in half points
        int[] sizes = [40, 32, 28, 26, 24, 22];
        for (int level = 1; level <= 6; level++)
        {
            styles.Append(new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new UIPriority { Val = 9 },
                new PrimaryStyle(),
                new StyleParagraphProperties(
                    new KeepNext(), new KeepLines(),
                    new SpacingBetweenLines { Before = level == 1 ? "360" : "240", After = "120" },
                    new OutlineLevel { Val = level - 1 }),
                new StyleRunProperties(
                    new RunFonts { Ascii = "Calibri Light", HighAnsi = "Calibri Light" },
                    new Bold(),
                    new FontSize { Val = sizes[level - 1].ToString() },
                    new FontSizeComplexScript { Val = sizes[level - 1].ToString() }))
            { Type = StyleValues.Paragraph, StyleId = $"Heading{level}" });
        }

        styles.Append(new Style(
            new StyleName { Val = "List Paragraph" },
            new BasedOn { Val = "Normal" },
            new UIPriority { Val = 34 },
            new PrimaryStyle(),
            new StyleParagraphProperties(new SpacingBetweenLines { After = "80" }, new ContextualSpacing()))
        { Type = StyleValues.Paragraph, StyleId = "ListParagraph" });

        styles.Append(new Style(
            new StyleName { Val = "Code" },
            new BasedOn { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F4F4F4" },
                new SpacingBetweenLines { After = "160", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            new StyleRunProperties(
                new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" },
                new FontSize { Val = "19" }, new FontSizeComplexScript { Val = "19" }))
        { Type = StyleValues.Paragraph, StyleId = "Code" });

        styles.Append(new Style(
            new StyleName { Val = "Hyperlink" },
            new UIPriority { Val = 99 },
            new StyleRunProperties(new Color { Val = "0563C1" }, new Underline { Val = UnderlineValues.Single }))
        { Type = StyleValues.Character, StyleId = "Hyperlink" });

        styles.Append(new Style(
            new StyleName { Val = "Table Grid" },
            new UIPriority { Val = 39 },
            new StyleParagraphProperties(new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            new StyleTableProperties(new TableCellMarginDefault(
                new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 100, Type = TableWidthValues.Dxa },
                new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                new TableCellRightMargin { Width = 100, Type = TableWidthValues.Dxa })))
        { Type = StyleValues.Table, StyleId = "TableGrid" });

        return styles;
    }
}
