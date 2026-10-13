using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using V = DocumentFormat.OpenXml.Vml;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WP = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace FileConverter.Core.Documents;

/// <summary>
/// Reads a Word document (.docx): headings, paragraphs with their bold, italic and other
/// character formatting, bulleted and numbered lists, tables, pictures, links, footnotes and
/// the text in text boxes. Page layout, headers and footers are an office program's business.
/// </summary>
internal sealed class DocxReader
{
    private readonly MainDocumentPart _main;
    private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
    private readonly List<List<Block>> _notes = [];

    private DocxReader(MainDocumentPart main)
    {
        _main = main;
        foreach (var style in main.StyleDefinitionsPart?.Styles?.Elements<Style>() ?? [])
            if (style.StyleId?.Value is { } id) _styles[id] = style;
    }

    public static RichDocument ReadFile(string path)
    {
        WordprocessingDocument file;
        try
        {
            file = WordprocessingDocument.Open(path, isEditable: false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            throw new FriendlyException("This file is not a Word document, is damaged, or is protected by a password.");
        }

        using (file)
        {
            var main = file.MainDocumentPart;
            var document = new RichDocument
            {
                Title = NullIfEmpty(file.PackageProperties.Title),
                Author = NullIfEmpty(file.PackageProperties.Creator),
                Language = NullIfEmpty(file.PackageProperties.Language)
            };
            if (main?.Document?.Body is not { } body) return document;

            var reader = new DocxReader(main);
            document.Blocks.AddRange(reader.ReadBlocks(body));
            if (reader._notes.Count > 0)
            {
                document.Blocks.Add(new Rule());
                var notes = new ListBlock(ordered: true);
                notes.Items.AddRange(reader._notes);
                document.Blocks.Add(notes);
            }
            return document;
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---------- blocks ----------

    /// <summary>One paragraph that is a list item, before the items are put together into lists.</summary>
    private sealed record ListEntry(int Level, bool Ordered, int NumberingId, List<Block> Blocks);

    private List<Block> ReadBlocks(OpenXmlElement container)
    {
        var blocks = new List<Block>();
        var pending = new List<ListEntry>();

        void EndList()
        {
            if (pending.Count == 0) return;
            blocks.AddRange(BuildLists(pending));
            pending.Clear();
        }

        foreach (var element in BodyElements(container))
        {
            switch (element)
            {
                case W.Paragraph paragraph:
                {
                    var builder = new BlockBuilder();
                    var properties = paragraph.ParagraphProperties;
                    if (IsOn(properties?.PageBreakBefore)) { EndList(); blocks.Add(new PageBreak()); }

                    var after = new List<Block>(); // what a paragraph carries besides its own text: text boxes
                    ReadRuns(paragraph, builder, TextStyle.None, null, after);

                    int? heading = HeadingLevel(properties);
                    var numbering = heading == null ? Numbering(properties) : null;
                    if (heading is { } level) builder.EndHeading(level);
                    else builder.EndParagraph(AlignOf(properties));

                    if (numbering is { } list && builder.Blocks.Count > 0)
                    {
                        pending.Add(new ListEntry(list.Level, list.Ordered, list.Id, builder.Blocks));
                    }
                    else
                    {
                        // an empty paragraph between two items of the same list does not end the list
                        if (builder.Blocks.Count > 0 || after.Count > 0) EndList();
                        blocks.AddRange(builder.Blocks);
                    }
                    if (after.Count > 0) { EndList(); blocks.AddRange(after); }
                    break;
                }

                case W.Table table:
                    EndList();
                    if (ReadTable(table) is { } read) blocks.Add(read);
                    break;
            }
        }
        EndList();
        return blocks;
    }

    /// <summary>Paragraphs and tables in order, looking inside content controls and the like.</summary>
    private static IEnumerable<OpenXmlElement> BodyElements(OpenXmlElement container)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case W.Paragraph or W.Table:
                    yield return child;
                    break;
                case SdtBlock or SdtContentBlock or CustomXmlBlock or InsertedRun or MoveToRun:
                    foreach (var inner in BodyElements(child)) yield return inner;
                    break;
            }
        }
    }

    /// <summary>Turns a run of list paragraphs, each with its depth, into lists inside lists.</summary>
    private static List<Block> BuildLists(List<ListEntry> entries)
    {
        var result = new List<Block>();
        int index = 0;
        while (index < entries.Count) result.Add(Build(entries[index].Level));
        return result;

        ListBlock Build(int level)
        {
            var first = entries[index];
            var list = new ListBlock(first.Ordered);
            while (index < entries.Count)
            {
                var entry = entries[index];
                if (entry.Level < level) break;
                if (entry.Level > level)
                {
                    // deeper: a list of its own inside the item before it
                    var nested = Build(entry.Level);
                    if (list.Items.Count == 0) list.Items.Add([]);
                    list.Items[^1].Add(nested);
                    continue;
                }
                // the same depth but another list (bullets after numbers): the caller starts a new one
                if (entry.NumberingId != first.NumberingId && entry.Ordered != first.Ordered) break;
                list.Items.Add([.. entry.Blocks]);
                index++;
            }
            return list;
        }
    }

    private Documents.Table? ReadTable(W.Table source)
    {
        var table = new Documents.Table();
        foreach (var row in source.Elements<W.TableRow>())
        {
            var cells = new List<Documents.TableCell>();
            foreach (var cell in row.Descendants<W.TableCell>().Where(c => c.Ancestors<W.TableRow>().First() == row))
            {
                var properties = cell.TableCellProperties;
                int span = properties?.GridSpan?.Val?.Value ?? 1;
                // the lower cells of a cell merged downwards: they are there, and empty
                bool continued = properties?.VerticalMerge is { } merge && (merge.Val == null || merge.Val.Value == MergedCellValues.Continue);
                cells.Add(new Documents.TableCell(continued ? [] : ReadBlocks(cell)) { ColumnSpan = Math.Clamp(span, 1, 100) });
            }
            if (cells.Count == 0) continue;
            if (row.TableRowProperties?.GetFirstChild<TableHeader>() is { } header && header.Val?.Value != OnOffOnlyValues.Off &&
                table.HeaderRows == table.Rows.Count)
                table.HeaderRows++;
            table.Rows.Add(cells);
        }
        return table.Rows.Count == 0 ? null : table;
    }

    // ---------- what a paragraph is ----------

    private IEnumerable<Style> StyleChain(string? id)
    {
        for (int depth = 0; id != null && depth < 20 && _styles.TryGetValue(id, out var style); depth++)
        {
            yield return style;
            id = style.BasedOn?.Val?.Value;
        }
    }

    /// <summary>1–6 when the paragraph is a heading: by its style's built-in name, or by an outline level.</summary>
    private int? HeadingLevel(ParagraphProperties? properties)
    {
        if (properties?.OutlineLevel?.Val?.Value is { } direct and >= 0 and < 9) return Math.Min(direct + 1, 6);

        foreach (var style in StyleChain(properties?.ParagraphStyleId?.Val?.Value))
        {
            // the name is the built-in English one whatever language Word runs in ("heading 1"); the id is not
            var name = style.StyleName?.Val?.Value?.ToLowerInvariant() ?? "";
            if (name.StartsWith("heading ") && int.TryParse(name.AsSpan(8), out int level) && level is >= 1 and <= 9)
                return Math.Min(level, 6);
            if (name == "title") return 1;
            if (name == "subtitle") return 2;
            if (style.StyleParagraphProperties?.OutlineLevel?.Val?.Value is { } outline and >= 0 and < 9)
                return Math.Min(outline + 1, 6);
        }
        return null;
    }

    private (int Id, int Level, bool Ordered)? Numbering(ParagraphProperties? properties)
    {
        int? id = properties?.NumberingProperties?.NumberingId?.Val?.Value;
        int? level = properties?.NumberingProperties?.NumberingLevelReference?.Val?.Value;
        if (id == null)
        {
            foreach (var style in StyleChain(properties?.ParagraphStyleId?.Val?.Value))
            {
                var numbering = style.StyleParagraphProperties?.NumberingProperties;
                if (numbering?.NumberingId?.Val?.Value is not { } styleId) continue;
                id = styleId;
                level ??= numbering.NumberingLevelReference?.Val?.Value;
                break;
            }
        }
        if (id is null or 0) return null; // 0 switches a style's numbering off

        int depth = Math.Clamp(level ?? 0, 0, 8);
        bool ordered = false;
        var definitions = _main.NumberingDefinitionsPart?.Numbering;
        var instance = definitions?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == id);
        if (instance?.AbstractNumId?.Val?.Value is { } abstractId)
        {
            var format = definitions!.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId)
                ?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == depth)?.NumberingFormat?.Val?.Value;
            ordered = format != null && format != NumberFormatValues.Bullet && format != NumberFormatValues.None;
        }
        return (id.Value, depth, ordered);
    }

    private static TextAlign AlignOf(ParagraphProperties? properties)
    {
        var value = properties?.Justification?.Val?.Value;
        if (value == null) return TextAlign.Start;
        if (value == JustificationValues.Center) return TextAlign.Center;
        if (value == JustificationValues.Right || value == JustificationValues.End) return TextAlign.End;
        if (value == JustificationValues.Both || value == JustificationValues.Distribute) return TextAlign.Justify;
        return TextAlign.Start;
    }

    private static bool IsOn(OnOffType? toggle) => toggle != null && (toggle.Val == null || toggle.Val.Value);

    // ---------- text ----------

    private void ReadRuns(OpenXmlElement container, BlockBuilder builder, TextStyle style, string? link, List<Block> after)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case W.Run run:
                    ReadRun(run, builder, style | StyleOf(run.RunProperties), link, after);
                    break;

                case W.Hyperlink hyperlink:
                {
                    string? address = link;
                    if (hyperlink.Id?.Value is { } id)
                        address = _main.HyperlinkRelationships.FirstOrDefault(r => r.Id == id)?.Uri.OriginalString ?? link;
                    ReadRuns(hyperlink, builder, style, address, after);
                    break;
                }

                case DeletedRun or MoveFromRun:
                    break; // text a tracked change took out

                case InsertedRun or MoveToRun or SdtRun or SdtContentRun or SimpleField or CustomXmlRun:
                    ReadRuns(child, builder, style, link, after);
                    break;
            }
        }
    }

    private void ReadRun(W.Run run, BlockBuilder builder, TextStyle style, string? link, List<Block> after)
    {
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case W.Text text:
                    builder.Text(text.Text, style, link);
                    break;
                case TabChar:
                    builder.Text("\t", style, link);
                    break;
                case W.Break lineBreak:
                    if (lineBreak.Type?.Value == BreakValues.Page) builder.Add(new PageBreak());
                    else builder.Break();
                    break;
                case CarriageReturn:
                    builder.Break();
                    break;
                case NoBreakHyphen:
                    builder.Text(((char)0x2011).ToString(), style, link); // a hyphen that keeps its word on one line
                    break;
                case SymbolChar symbol:
                    // a character from a symbol font, given by its code
                    if (int.TryParse(symbol.Char?.Value, System.Globalization.NumberStyles.HexNumber, null, out int code))
                        builder.Text(SymbolText(code), style, link);
                    break;
                case FootnoteReference footnote:
                    AddNote(_main.FootnotesPart?.Footnotes?.Elements<Footnote>().FirstOrDefault(n => n.Id?.Value == footnote.Id?.Value), builder, link);
                    break;
                case EndnoteReference endnote:
                    AddNote(_main.EndnotesPart?.Endnotes?.Elements<Endnote>().FirstOrDefault(n => n.Id?.Value == endnote.Id?.Value), builder, link);
                    break;
                case W.Drawing drawing:
                    ReadDrawing(drawing, builder, after);
                    break;
                case W.Picture picture: // the older way of placing pictures and text boxes
                    foreach (var data in picture.Descendants<V.ImageData>())
                        builder.Add(LoadPicture(data.RelationshipId?.Value, data.Title?.Value ?? "", null, null));
                    foreach (var box in picture.Descendants<TextBoxContent>()) after.AddRange(ReadBlocks(box));
                    break;
                case AlternateContent alternate:
                    // the same thing twice, for newer and for older programs: the first that is there
                    var choice = (OpenXmlElement?)alternate.GetFirstChild<AlternateContentChoice>() ?? alternate.GetFirstChild<AlternateContentFallback>();
                    if (choice != null)
                    {
                        foreach (var drawing in choice.Elements<W.Drawing>()) ReadDrawing(drawing, builder, after);
                        foreach (var picture in choice.Elements<W.Picture>())
                            foreach (var box in picture.Descendants<TextBoxContent>()) after.AddRange(ReadBlocks(box));
                    }
                    break;
            }
        }
    }

    /// <summary>The common bullets and arrows of the Symbol and Wingdings fonts; anything else is left as a bullet.</summary>
    private static string SymbolText(int code) => (code & 0xFF) switch
    {
        0xB7 or 0xA7 or 0x6C or 0x6E => "•",
        0xD8 or 0xE0 => "→",
        0xFC => "✓",
        0xA8 => "☐",
        0xFE => "☑",
        _ => "•"
    };

    private void AddNote(OpenXmlElement? note, BlockBuilder builder, string? link)
    {
        if (note == null) return;
        var blocks = ReadBlocks(note);
        if (blocks.Count == 0) return;
        _notes.Add(blocks);
        builder.Text(_notes.Count.ToString(), TextStyle.Superscript, link);
    }

    private void ReadDrawing(W.Drawing drawing, BlockBuilder builder, List<Block> after)
    {
        var extent = drawing.Descendants<WP.Extent>().FirstOrDefault();
        double? width = extent?.Cx?.Value is { } cx and > 0 ? cx / 12700.0 : null;
        double? height = extent?.Cy?.Value is { } cy and > 0 ? cy / 12700.0 : null;
        var description = drawing.Descendants<WP.DocProperties>().FirstOrDefault()?.Description?.Value ?? "";

        foreach (var blip in drawing.Descendants<A.Blip>())
            builder.Add(LoadPicture(blip.Embed?.Value, description, width, height));
        foreach (var box in drawing.Descendants<TextBoxContent>())
            after.AddRange(ReadBlocks(box));
    }

    private Documents.Picture? LoadPicture(string? relationshipId, string alt, double? width, double? height)
    {
        if (string.IsNullOrEmpty(relationshipId)) return null;
        try
        {
            if (_main.GetPartById(relationshipId) is not ImagePart part) return null;
            using var stream = part.GetStream(FileMode.Open, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Documents.Picture.Load(buffer.ToArray(), alt, width, height);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or IOException or InvalidOperationException)
        {
            return null; // a picture that is linked rather than stored, or a broken reference
        }
    }

    private static readonly string[] FixedWidthFonts = ["Consolas", "Courier", "Cascadia", "Lucida Console", "Monaco", "Menlo", " Mono"];

    private TextStyle StyleOf(RunProperties? properties)
    {
        if (properties == null) return TextStyle.None;
        var style = TextStyle.None;
        if (IsOn(properties.Bold)) style |= TextStyle.Bold;
        if (IsOn(properties.Italic)) style |= TextStyle.Italic;
        if (properties.Underline?.Val?.Value is { } underline && underline != UnderlineValues.None) style |= TextStyle.Underline;
        if (IsOn(properties.Strike) || IsOn(properties.DoubleStrike)) style |= TextStyle.Strike;
        if (properties.VerticalTextAlignment?.Val?.Value is { } vertical)
        {
            if (vertical == VerticalPositionValues.Superscript) style |= TextStyle.Superscript;
            else if (vertical == VerticalPositionValues.Subscript) style |= TextStyle.Subscript;
        }
        if (properties.RunFonts?.Ascii?.Value is { } font && FixedWidthFonts.Any(name => font.Contains(name, StringComparison.OrdinalIgnoreCase)))
            style |= TextStyle.Code;

        // a character style ("Strong", "Emphasis", one of the user's own) says the rest
        foreach (var named in StyleChain(properties.RunStyle?.Val?.Value))
        {
            var run = named.StyleRunProperties;
            if (run == null) continue;
            if (IsOn(run.Bold)) style |= TextStyle.Bold;
            if (IsOn(run.Italic)) style |= TextStyle.Italic;
            if (IsOn(run.Strike)) style |= TextStyle.Strike;
            if (run.RunFonts?.Ascii?.Value is { } styleFont && FixedWidthFonts.Any(name => styleFont.Contains(name, StringComparison.OrdinalIgnoreCase)))
                style |= TextStyle.Code;
        }
        return style;
    }
}
