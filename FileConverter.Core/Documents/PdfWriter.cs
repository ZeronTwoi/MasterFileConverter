using FileConverter.Core.Converters;
using FileConverter.Core.Platform;
using QuestPDF.Elements.Table;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using K = FileConverter.Core.Settings.SettingKeys;
using QDocument = QuestPDF.Fluent.Document;

namespace FileConverter.Core.Documents;

/// <summary>
/// Lays a document out as a PDF: headings, paragraphs with their formatting, lists, tables,
/// pictures and page breaks on pages of the chosen paper size, with page numbers at the foot.
/// This is the built-in way to a PDF — an office program, where one is installed, reproduces a
/// Word or OpenDocument file's own layout instead (see <see cref="DocumentConversion"/>).
/// </summary>
internal sealed class PdfWriter
{
    private readonly float _fontSize;
    private readonly float _contentWidth, _contentHeight;
    private readonly bool _plainTables;

    private PdfWriter(float fontSize, float contentWidth, float contentHeight, bool plainTables)
    {
        _fontSize = fontSize;
        _contentWidth = contentWidth;
        _contentHeight = contentHeight;
        _plainTables = plainTables;
    }

    public static void Write(RichDocument document, string output, ConversionOptions options, string fallbackTitle)
    {
        var detail = options.Detail;
        var paper = MergeConversion.PaperSize(detail.Text(K.PageSize)) ?? PageSizes.A4;
        // millimetres → points; 17 mm when nothing is set
        float margin = (float)(Math.Clamp(detail.Number(K.Margin) ?? 17, 0, 100) * 72.0 / 25.4);
        float fontSize = (float)Math.Clamp(detail.Number(K.FontSize) ?? 10.5, 5, 72);
        float footer = fontSize * 2.4f;

        try
        {
            Generate(document, output, paper, margin, fontSize, footer, fallbackTitle, plainTables: false);
        }
        catch (QuestPDF.Drawing.Exceptions.DocumentLayoutException)
        {
            // Something could not be fitted onto a page as it was — in practice a table row taller
            // than a page, which cannot be divided. The same document with its tables as lines of text.
            Generate(document, output, paper, margin, fontSize, footer, fallbackTitle, plainTables: true);
        }
    }

    private static void Generate(RichDocument document, string output, PageSize paper, float margin, float fontSize, float footer,
        string fallbackTitle, bool plainTables)
    {
        var writer = new PdfWriter(fontSize, paper.Width - 2 * margin, paper.Height - 2 * margin - footer, plainTables);
        QDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(paper);
                page.Margin(margin);
                // Lato ships with QuestPDF; installed system fonts cover the glyphs it lacks (CJK and others)
                page.DefaultTextStyle(style => style.FontSize(fontSize).LineHeight(1.35f)
                    .FontFamily(["Lato", .. PlatformServices.Current.PdfFallbackFonts]));

                page.Content().Column(column => writer.Compose(column, document.Blocks, writer._contentWidth));

                page.Footer().Height(footer).AlignBottom().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(8).FontColor(Colors.Grey.Medium));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = document.Title ?? fallbackTitle,
            Author = document.Author ?? "",
            Creator = $"{AppInfo.DisplayName} {AppInfo.Version}",
            Producer = $"{AppInfo.DisplayName} {AppInfo.Version}"
        })
        .GeneratePdf(output);
    }

    // ---------- blocks ----------

    private static readonly float[] HeadingScale = [1.9f, 1.55f, 1.3f, 1.15f, 1.05f, 1f];

    /// <param name="width">How wide the space for these blocks is, in points: pictures are held to it.</param>
    private void Compose(ColumnDescriptor column, IEnumerable<Block> blocks, float width)
    {
        float gap = _fontSize * 0.7f;
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                {
                    float size = _fontSize * HeadingScale[heading.Level - 1];
                    // a heading stays with what it heads: it moves to the next page rather than end this one
                    column.Item().EnsureSpace(size * 3.5f).PaddingTop(size * 0.55f).PaddingBottom(size * 0.3f).Text(text =>
                    {
                        text.DefaultTextStyle(style => style.FontSize(size).Bold().LineHeight(1.2f));
                        Spans(text, heading.Inlines);
                    });
                    break;
                }

                case Paragraph paragraph:
                    column.Item().PaddingBottom(gap).Text(text =>
                    {
                        switch (paragraph.Align)
                        {
                            case TextAlign.Center: text.AlignCenter(); break;
                            case TextAlign.End: text.AlignRight(); break;
                            case TextAlign.Justify: text.Justify(); break;
                        }
                        Spans(text, paragraph.Inlines);
                    });
                    break;

                case ListBlock list:
                {
                    float markerWidth = _fontSize * (list.Ordered ? 1.4f + 0.55f * (list.Start + list.Items.Count - 1).ToString().Length : 1.5f);
                    for (int i = 0; i < list.Items.Count; i++)
                    {
                        var item = list.Items[i];
                        string marker = list.Ordered ? $"{list.Start + i}." : "•";
                        column.Item().Row(row =>
                        {
                            row.ConstantItem(markerWidth).AlignRight().PaddingRight(_fontSize * 0.5f).Text(marker);
                            row.RelativeItem().Column(inner => Compose(inner, item, Math.Max(width - markerWidth, 40)));
                        });
                    }
                    break;
                }

                case CodeBlock code:
                    column.Item().PaddingBottom(gap).Background(Colors.Grey.Lighten4).Padding(_fontSize * 0.6f)
                        .Text(code.Text.Replace("\t", "    ").Replace("\r\n", "\n"))
                        .FontFamily(FixedWidthFonts.Value).FontSize(_fontSize * 0.88f).LineHeight(1.25f);
                    break;

                case Quote quote:
                    column.Item().PaddingBottom(gap * 0.3f).BorderLeft(2).BorderColor(Colors.Grey.Lighten1).PaddingLeft(_fontSize)
                        .Column(inner => Compose(inner, quote.Blocks, Math.Max(width - _fontSize - 2, 40)));
                    break;

                case Table table when !_plainTables:
                    column.Item().PaddingBottom(gap).Element(container => ComposeTable(container, table, width));
                    break;

                case Table table:
                    foreach (var row in table.Rows)
                    {
                        var line = string.Join("   |   ", row.Select(PlainTextWriter.CellText));
                        column.Item().PaddingBottom(gap * 0.4f).Text(line.Length == 0 ? " " : line);
                    }
                    column.Item().PaddingBottom(gap);
                    break;

                case Picture picture:
                {
                    // never wider than the space it stands in, never taller than a page
                    var (w, h) = picture.DisplaySize(width, _contentHeight * 0.96);
                    // a JPEG or PNG goes in as the data it is, without being compressed again
                    column.Item().PaddingBottom(gap).Width((float)w).Height((float)h).Image(ForPdf(picture)).UseOriginalImage().FitArea();
                    break;
                }

                case PageBreak:
                    column.Item().PageBreak();
                    break;

                case Rule:
                    column.Item().PaddingVertical(gap).LineHorizontal(0.75f).LineColor(Colors.Grey.Medium);
                    break;
            }
        }
    }

    private void ComposeTable(IContainer container, Table table, float width)
    {
        int columns = Math.Max(table.ColumnCount, 1);
        float cellWidth = Math.Max(width / columns - 8, 20);

        container.Table(grid =>
        {
            grid.ColumnsDefinition(definition =>
            {
                for (int i = 0; i < columns; i++) definition.RelativeColumn();
            });

            void Rows(Func<ITableCellContainer> newCell, IEnumerable<List<TableCell>> rows, bool heading)
            {
                uint rowNumber = 1;
                foreach (var row in rows)
                {
                    uint columnNumber = 1;
                    foreach (var cell in row)
                    {
                        uint span = (uint)Math.Clamp(cell.ColumnSpan, 1, Math.Max(columns - (int)columnNumber + 1, 1));
                        var place = newCell().Row(rowNumber).Column(columnNumber).ColumnSpan(span)
                            .Border(0.5f).BorderColor(Colors.Grey.Medium).Background(heading ? Colors.Grey.Lighten3 : Colors.White)
                            .PaddingHorizontal(4).PaddingTop(3);
                        var blocks = cell.Blocks;
                        place.DefaultTextStyle(style => heading ? style.Bold() : style)
                            .Column(inner => ComposeCell(inner, blocks, cellWidth * span));
                        columnNumber += span;
                        if (columnNumber > columns) break;
                    }
                    // the cells a short row lacks, so that the grid has no holes
                    for (; columnNumber <= columns; columnNumber++)
                        newCell().Row(rowNumber).Column(columnNumber).Border(0.5f).BorderColor(Colors.Grey.Medium);
                    rowNumber++;
                }
            }

            // heading rows are repeated on every page the table runs over
            if (table.HeaderRows > 0)
                grid.Header(header => Rows(header.Cell, table.Rows.Take(table.HeaderRows), heading: true));
            Rows(grid.Cell, table.Rows.Skip(table.HeaderRows), heading: false);
        });
    }

    /// <summary>A cell's content, with a little less air than on the page.</summary>
    private void ComposeCell(ColumnDescriptor column, List<Block> blocks, float width)
    {
        if (blocks.Count == 0)
        {
            column.Item().PaddingBottom(3).Text(" ");
            return;
        }
        // a table in a table would have to fit a cell's width and a page's height at once: its text instead
        var flat = blocks.SelectMany(block => block is Table nested
            ? nested.Rows.Select(row => (Block)new Paragraph(string.Join("   |   ", row.Select(PlainTextWriter.CellText))))
            : [block]);
        Compose(column, flat, width);
    }

    // ---------- text ----------

    private void Spans(TextDescriptor text, IEnumerable<Inline> inlines)
    {
        bool any = false;
        foreach (var inline in inlines)
        {
            if (inline is LineBreak)
            {
                text.Span("\n");
                continue;
            }
            if (inline is not TextRun run || run.Text.Length == 0) continue;
            any = true;

            // a tab has no width of its own in a PDF's line of text
            var content = run.Text.Replace("\t", "    ");
            var span = run.Link != null && Uri.TryCreate(run.Link, UriKind.Absolute, out _)
                ? text.Hyperlink(content, run.Link).FontColor(Colors.Blue.Darken2).Underline()
                : text.Span(content);

            if (run.Has(TextStyle.Bold)) span.Bold();
            if (run.Has(TextStyle.Italic)) span.Italic();
            if (run.Has(TextStyle.Underline)) span.Underline();
            if (run.Has(TextStyle.Strike)) span.Strikethrough();
            if (run.Has(TextStyle.Superscript)) span.Superscript();
            if (run.Has(TextStyle.Subscript)) span.Subscript();
            if (run.Has(TextStyle.Code)) span.FontFamily(FixedWidthFonts.Value).FontSize(_fontSize * 0.92f).BackgroundColor(Colors.Grey.Lighten4);
        }
        if (!any) text.Span(" ");
    }

    /// <summary>The fixed-width fonts to set program text in: the first is the one installed here, the rest stand by.</summary>
    private static readonly Lazy<string[]> FixedWidthFonts = new(() =>
    {
        string[] candidates = ["Cascadia Mono", "Consolas", "DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Courier New"];
        var installed = candidates.Where(IsInstalled).ToArray();
        return installed.Length > 0 ? [.. installed, .. PlatformServices.Current.PdfFallbackFonts] : ["Lato", .. PlatformServices.Current.PdfFallbackFonts];
    });

    private static bool IsInstalled(string family)
    {
        try
        {
            using var typeface = SKFontManager.Default.MatchFamily(family);
            return typeface != null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---------- pictures ----------

    /// <summary>The picture's data as the PDF library takes it: an animated GIF goes in as its first picture.</summary>
    private static byte[] ForPdf(Picture picture)
    {
        if (picture.Format != "gif") return picture.Data;

        using var bitmap = SKBitmap.Decode(picture.Data);
        if (bitmap == null) return picture.Data;
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
