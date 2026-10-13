using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter.Core.Documents;

/// <summary>Writes a document as plain text: its words, with lists and tables still recognisable as such.</summary>
internal static class PlainTextWriter
{
    public static void Write(RichDocument document, string output)
    {
        var text = new StringBuilder();
        Append(text, document.Blocks, "");
        File.WriteAllText(output, text.ToString().TrimEnd('\n', ' ') + "\n", new UTF8Encoding(true));
    }

    private static void Append(StringBuilder text, IEnumerable<Block> blocks, string indent)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                    text.Append(indent).Append(Line(heading.Inlines, indent)).Append("\n\n");
                    break;
                case Paragraph paragraph:
                    text.Append(indent).Append(Line(paragraph.Inlines, indent)).Append("\n\n");
                    break;
                case ListBlock list:
                    for (int i = 0; i < list.Items.Count; i++)
                    {
                        var marker = list.Ordered ? $"{list.Start + i}. " : "• ";
                        var item = new StringBuilder();
                        Append(item, list.Items[i], indent + new string(' ', marker.Length));
                        // the first line carries the marker in place of its indent
                        var body = item.ToString().TrimEnd('\n');
                        if (body.Length >= indent.Length + marker.Length) body = body[(indent.Length + marker.Length)..];
                        text.Append(indent).Append(marker).Append(body).Append('\n');
                    }
                    text.Append('\n');
                    break;
                case CodeBlock code:
                    foreach (var line in code.Text.Split('\n')) text.Append(indent).Append(line.TrimEnd('\r')).Append('\n');
                    text.Append('\n');
                    break;
                case Quote quote:
                    Append(text, quote.Blocks, indent + "> ");
                    break;
                case Table table:
                    foreach (var row in table.Rows)
                        text.Append(indent).AppendJoin('\t', row.Select(cell => CellText(cell))).Append('\n');
                    text.Append('\n');
                    break;
                case Rule:
                    text.Append(indent).Append("────────────────────\n\n");
                    break;
                case PageBreak:
                    text.Append('\f').Append('\n');
                    break;
            }
        }
    }

    private static string Line(IEnumerable<Inline> inlines, string indent)
    {
        var line = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is LineBreak) line.Append('\n').Append(indent);
            else if (inline is TextRun run)
            {
                line.Append(run.Text);
                // an address the words do not already show
                if (run.Link != null && !run.Text.Contains(run.Link, StringComparison.OrdinalIgnoreCase) &&
                    !run.Link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    line.Append(" (").Append(run.Link).Append(')');
            }
        }
        return line.ToString();
    }

    /// <summary>What a table cell says, on one line.</summary>
    internal static string CellText(TableCell cell)
    {
        var parts = RichDocument.Flatten(cell.Blocks).Select(block => block switch
        {
            Paragraph paragraph => RichDocument.PlainText(paragraph.Inlines),
            Heading heading => RichDocument.PlainText(heading.Inlines),
            CodeBlock code => code.Text.ReplaceLineEndings(" "),
            _ => ""
        }).Where(part => part.Length > 0);
        return string.Join(" ", parts).Trim();
    }
}

/// <summary>
/// Writes a document as Markdown, the way GitHub reads it. Pictures cannot be inside a Markdown
/// file, so they are handed back with the names the text refers to them by, for the caller to
/// put beside it.
/// </summary>
internal static partial class MarkdownWriter
{
    /// <returns>The pictures to be saved, each under the relative path the Markdown names.</returns>
    public static List<(string Path, Picture Picture)> Write(RichDocument document, string output, string pictureFolder = "images")
    {
        var pictures = new List<(string, Picture)>();
        var known = new Dictionary<Picture, string>(ReferenceEqualityComparer.Instance);
        string Address(Picture picture)
        {
            if (!known.TryGetValue(picture, out var path))
            {
                path = $"{pictureFolder}/image-{known.Count + 1:D3}.{picture.Format}";
                known[picture] = path;
                pictures.Add((path, picture));
            }
            return path;
        }

        var text = new StringBuilder();
        Append(text, document.Blocks, "", Address);
        File.WriteAllText(output, BlankLines().Replace(text.ToString(), "\n\n").Trim('\n') + "\n", new UTF8Encoding(false));
        return pictures;
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    private static void Append(StringBuilder text, IEnumerable<Block> blocks, string indent, Func<Picture, string> address)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                    text.Append(indent).Append(new string('#', heading.Level)).Append(' ')
                        .Append(Inlines(heading.Inlines, indent, lineStart: false)).Append("\n\n");
                    break;

                case Paragraph paragraph:
                    text.Append(indent).Append(Inlines(paragraph.Inlines, indent, lineStart: true)).Append("\n\n");
                    break;

                case ListBlock list:
                    for (int i = 0; i < list.Items.Count; i++)
                    {
                        var marker = list.Ordered ? $"{(list.Start + i).ToString(CultureInfo.InvariantCulture)}. " : "- ";
                        var inner = indent + new string(' ', marker.Length);
                        var item = new StringBuilder();
                        Append(item, list.Items[i], inner, address);
                        var body = item.ToString().TrimEnd('\n');
                        if (body.StartsWith(inner, StringComparison.Ordinal)) body = body[inner.Length..];
                        text.Append(indent).Append(marker).Append(body).Append('\n');
                        // an item of several blocks needs air before the next, or they run together
                        if (list.Items[i].Count > 1) text.Append('\n');
                    }
                    text.Append('\n');
                    break;

                case CodeBlock code:
                {
                    // a fence longer than any run of backticks in the text itself
                    int longest = BacktickRuns().Matches(code.Text).Select(m => m.Length).DefaultIfEmpty(0).Max();
                    var fence = new string('`', Math.Max(3, longest + 1));
                    text.Append(indent).Append(fence).Append('\n');
                    foreach (var line in code.Text.Split('\n')) text.Append(indent).Append(line.TrimEnd('\r')).Append('\n');
                    text.Append(indent).Append(fence).Append("\n\n");
                    break;
                }

                case Quote quote:
                {
                    var inner = new StringBuilder();
                    Append(inner, quote.Blocks, "", address);
                    foreach (var line in inner.ToString().TrimEnd('\n').Split('\n'))
                        text.Append(indent).Append(line.Length == 0 ? ">" : "> " + line).Append('\n');
                    text.Append('\n');
                    break;
                }

                case Table table:
                    AppendTable(text, table, indent);
                    break;

                case Picture picture:
                    text.Append(indent).Append("![").Append(EscapeBrackets(picture.Alt)).Append("](").Append(address(picture)).Append(")\n\n");
                    break;

                case Rule:
                    text.Append(indent).Append("---\n\n");
                    break;

                // a page break has no meaning in Markdown
            }
        }
    }

    [GeneratedRegex("`+")]
    private static partial Regex BacktickRuns();

    private static void AppendTable(StringBuilder text, Table table, string indent)
    {
        int columns = table.ColumnCount;
        if (columns == 0) return;

        string Row(IEnumerable<string> cells) => indent + "| " + string.Join(" | ", cells) + " |\n";
        List<string> Cells(List<TableCell> row)
        {
            var cells = new List<string>();
            foreach (var cell in row)
            {
                cells.Add(CellInlines(cell));
                // Markdown tables have no merged cells: the columns it covered stay empty
                for (int extra = 1; extra < cell.ColumnSpan; extra++) cells.Add("");
            }
            while (cells.Count < columns) cells.Add("");
            return cells;
        }

        // the first row is the heading: Markdown has no table without one
        text.Append(Row(Cells(table.Rows[0])));
        text.Append(Row(Enumerable.Repeat("---", columns)));
        foreach (var row in table.Rows.Skip(1)) text.Append(Row(Cells(row)));
        text.Append('\n');
    }

    private static string CellInlines(TableCell cell)
    {
        var parts = new List<string>();
        foreach (var block in RichDocument.Flatten(cell.Blocks))
        {
            var part = block switch
            {
                Paragraph paragraph => Inlines(paragraph.Inlines, "", lineStart: false),
                Heading heading => "**" + Inlines(heading.Inlines, "", lineStart: false) + "**",
                CodeBlock code => "`" + code.Text.ReplaceLineEndings(" ").Replace("`", "'") + "`",
                _ => ""
            };
            if (part.Length > 0) parts.Add(part.Replace("\n", "<br>").Replace("|", "\\|"));
        }
        return string.Join("<br>", parts);
    }

    private static string Inlines(List<Inline> inlines, string indent, bool lineStart)
    {
        var text = new StringBuilder();
        foreach (var inline in inlines)
        {
            if (inline is LineBreak)
            {
                text.Append("  \n").Append(indent);
                continue;
            }
            if (inline is not TextRun run || run.Text.Length == 0) continue;

            // the marks have to touch the words: spaces at either end go outside them
            var body = run.Text.Trim(' ', '\t', '\n', '\r');
            if (body.Length == 0)
            {
                text.Append(' ');
                continue;
            }
            string leading = run.Text[..run.Text.IndexOf(body, StringComparison.Ordinal)];
            string trailing = run.Text[(leading.Length + body.Length)..];

            string content;
            if (run.Has(TextStyle.Code))
            {
                int longest = BacktickRuns().Matches(body).Select(m => m.Length).DefaultIfEmpty(0).Max();
                var ticks = new string('`', longest + 1);
                content = longest > 0 ? $"{ticks} {body} {ticks}" : $"`{body}`";
            }
            else
            {
                content = Escape(body, atLineStart: lineStart && text.Length == 0);
            }

            if (run.Has(TextStyle.Superscript)) content = $"<sup>{content}</sup>";
            if (run.Has(TextStyle.Subscript)) content = $"<sub>{content}</sub>";
            if (run.Has(TextStyle.Underline)) content = $"<u>{content}</u>";
            if (run.Has(TextStyle.Strike)) content = $"~~{content}~~";
            if (run.Has(TextStyle.Bold) && run.Has(TextStyle.Italic)) content = $"***{content}***";
            else if (run.Has(TextStyle.Bold)) content = $"**{content}**";
            else if (run.Has(TextStyle.Italic)) content = $"*{content}*";
            if (run.Link != null) content = $"[{content}]({run.Link.Replace(" ", "%20").Replace(")", "%29")})";

            text.Append(leading.Length > 0 ? " " : "").Append(content).Append(trailing.Length > 0 ? " " : "");
        }
        return text.ToString().Trim(' ');
    }

    private static string EscapeBrackets(string text) => text.Replace("[", "\\[").Replace("]", "\\]").ReplaceLineEndings(" ");

    [GeneratedRegex(@"^(\d+)([.)])(\s|$)")]
    private static partial Regex NumberedStart();

    /// <summary>
    /// Text with the characters that would be read as Markdown marked as plain ones. Only where
    /// they would be read so: an underscore inside a word, for one, is left alone.
    /// </summary>
    internal static string Escape(string text, bool atLineStart)
    {
        var escaped = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '\\' or '`' or '*' or '[' or ']' or '<':
                    escaped.Append('\\').Append(c);
                    break;
                case '_':
                {
                    bool inWord = i > 0 && char.IsLetterOrDigit(text[i - 1]) && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
                    escaped.Append(inWord ? "_" : "\\_");
                    break;
                }
                case '~' when i + 1 < text.Length && text[i + 1] == '~':
                    escaped.Append("\\~");
                    break;
                case '\n':
                    escaped.Append(' ');
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }

        var result = escaped.ToString();
        if (!atLineStart || result.Length == 0) return result;
        // what would turn the line into a heading, a quote or a list item
        if (result[0] is '#' or '>' || (result[0] is '+' or '-' && (result.Length == 1 || result[1] == ' '))) return "\\" + result;
        var numbered = NumberedStart().Match(result);
        return numbered.Success ? $"{numbered.Groups[1].Value}\\{result[numbered.Groups[1].Length..]}" : result;
    }
}

/// <summary>
/// Writes a document as Rich Text Format, which every word processor opens: headings, the
/// character formatting, lists, tables and pictures. (Also what a ".doc" is made of when no
/// office program is installed to write the real, binary kind — Word opens it all the same.)
/// </summary>
internal static class RtfWriter
{
    private const int PageWidthTwips = 11906, MarginTwips = 1440; // A4 with one-inch margins
    private const int TextWidthTwips = PageWidthTwips - 2 * MarginTwips;

    private static readonly int[] HeadingHalfPoints = [40, 32, 28, 26, 24, 22];

    public static void Write(RichDocument document, string output)
    {
        // the text first: the lists it turns out to have are described ahead of it
        var lists = new List<ListDefinition>();
        var body = new StringBuilder();
        Append(body, document.Blocks, indentTwips: 0, inTable: false, lists, depth: 0, listId: 0);

        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        rtf.Append(@"{\fonttbl{\f0\fswiss Calibri;}{\f1\fmodern Consolas;}}");
        rtf.Append(@"{\colortbl;\red5\green99\blue193;\red96\green96\blue96;}");
        rtf.Append(ListTables(lists));
        if (document.Title != null || document.Author != null)
        {
            rtf.Append(@"{\info");
            if (document.Title != null) rtf.Append(@"{\title ").Append(Text(document.Title)).Append('}');
            if (document.Author != null) rtf.Append(@"{\author ").Append(Text(document.Author)).Append('}');
            rtf.Append('}');
        }
        rtf.Append($@"\paperw{PageWidthTwips}\paperh16838\margl{MarginTwips}\margr{MarginTwips}\margt{MarginTwips}\margb{MarginTwips}");
        rtf.Append(@"\f0\fs22 ").Append("\r\n");
        rtf.Append(body);
        rtf.Append('}');
        File.WriteAllText(output, rtf.ToString(), Encoding.ASCII);
    }

    /// <summary>One list with the lists inside it: what each of its nine levels shows, and counts from.</summary>
    private sealed class ListDefinition
    {
        /// <summary>Numbered (true) or bulleted (false); null for a level nothing has used.</summary>
        public bool?[] Ordered { get; } = new bool?[9];
        public int[] Start { get; } = [1, 1, 1, 1, 1, 1, 1, 1, 1];
    }

    /// <summary>
    /// The description of every list in the document — one each, so that every numbered list
    /// counts from its own beginning. A program that knows lists numbers the items itself from
    /// this; one that does not shows the marker written out beside each item.
    /// </summary>
    private static string ListTables(List<ListDefinition> lists)
    {
        if (lists.Count == 0) return "";
        var table = new StringBuilder(@"{\*\listtable");
        var overrides = new StringBuilder(@"{\*\listoverridetable");
        for (int i = 0; i < lists.Count; i++)
        {
            int id = i + 1;
            table.Append($@"{{\list\listtemplateid{id}");
            for (int level = 0; level < 9; level++)
            {
                bool ordered = lists[i].Ordered[level] ?? lists[i].Ordered[0] ?? false;
                // 23 is "a bullet"; the others say how to count
                int format = ordered ? (level % 3) switch { 0 => 0, 1 => 4, _ => 2 } : 23;
                table.Append($@"{{\listlevel\levelnfc{format}\levelnfcn{format}\leveljc0\leveljcn0\levelfollow0\levelstartat{lists[i].Start[level]}");
                table.Append(ordered
                    ? $@"{{\leveltext\'02\'0{level}.;}}{{\levelnumbers\'01;}}"
                    : @"{\leveltext\'01" + Bullet + @";}{\levelnumbers;}");
                table.Append($@"\fi-360\li{360 * (level + 1)}}}");
            }
            table.Append($@"{{\listname ;}}\listid{id}}}");
            overrides.Append($@"{{\listoverride\listid{id}\listoverridecount0\ls{id}}}");
        }
        return table.Append('}').Append(overrides).Append('}').Append("\r\n").ToString();
    }

    /// <summary>A bullet, as the file's code page (Windows-1252) numbers it.</summary>
    private const string Bullet = @"\'95";

    /// <summary>An item's marker as text: 1. 2. 3., then a. b. c. one level in, then i. ii. iii.</summary>
    private static string Marker(bool ordered, int level, int number)
    {
        if (!ordered) return Bullet;
        switch (level % 3)
        {
            case 1:
                return (char)('a' + (Math.Max(number, 1) - 1) % 26) + ".";
            case 2:
            {
                var roman = new StringBuilder();
                (int Value, string Digits)[] parts = [(1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")];
                int rest = Math.Clamp(number, 1, 3999);
                foreach (var (value, digits) in parts)
                    for (; rest >= value; rest -= value) roman.Append(digits);
                return roman + ".";
            }
            default:
                return number.ToString(CultureInfo.InvariantCulture) + ".";
        }
    }

    /// <param name="lists">Collects every list that is written (see <see cref="ListTables"/>).</param>
    /// <param name="depth">How many lists these blocks are inside of.</param>
    /// <param name="listId">The number of the innermost of those lists; 0 outside any list.</param>
    private static void Append(StringBuilder rtf, IEnumerable<Block> blocks, int indentTwips, bool inTable,
        List<ListDefinition> lists, int depth, int listId)
    {
        // inside a table cell "\intbl" keeps the paragraph in its cell
        string start = @"\pard\plain\f0\fs22" + (inTable ? @"\intbl" : "") + (indentTwips > 0 ? $@"\li{indentTwips}" : "");
        string end = "\\par\r\n";

        foreach (var block in blocks)
        {
            switch (block)
            {
                case Heading heading:
                    rtf.Append(start).Append($@"\sb240\sa120\keepn\outlinelevel{heading.Level - 1}\b\fs{HeadingHalfPoints[heading.Level - 1]} ");
                    AppendInlines(rtf, heading.Inlines, heading: true);
                    rtf.Append(end);
                    break;

                case Paragraph paragraph:
                    rtf.Append(start).Append(@"\sa160").Append(paragraph.Align switch
                    {
                        TextAlign.Center => @"\qc",
                        TextAlign.End => @"\qr",
                        TextAlign.Justify => @"\qj",
                        _ => ""
                    }).Append(' ');
                    AppendInlines(rtf, paragraph.Inlines, heading: false);
                    rtf.Append(end);
                    break;

                case ListBlock list:
                {
                    int level = Math.Min(depth, 8);
                    // A list inside a list is a deeper level of that same list — unless another list has
                    // already made that level the other kind, bullets where this one counts.
                    int id = listId;
                    if (id == 0 || (lists[id - 1].Ordered[level] is { } taken && taken != list.Ordered))
                    {
                        lists.Add(new ListDefinition());
                        id = lists.Count;
                    }
                    if (lists[id - 1].Ordered[level] == null)
                    {
                        lists[id - 1].Ordered[level] = list.Ordered;
                        lists[id - 1].Start[level] = list.Start;
                    }
                    int inner = indentTwips + 360;
                    for (int i = 0; i < list.Items.Count; i++)
                    {
                        var item = list.Items[i];
                        // The first paragraph carries the marker and hangs out to the left by its width;
                        // an item that begins with something else gets an empty one to carry it.
                        var first = item.Count > 0 ? item[0] as Paragraph : null;
                        rtf.Append(@"\pard\plain\f0\fs22").Append(inTable ? @"\intbl" : "")
                            .Append($@"{{\listtext\pard\plain\f0\fs22 {Marker(list.Ordered, level, list.Start + i)}\tab}}")
                            .Append($@"\ls{id}\ilvl{level}\fi-360\li{inner}\sa80 ");
                        if (first != null) AppendInlines(rtf, first.Inlines, heading: false);
                        rtf.Append(end);
                        Append(rtf, first != null ? item.Skip(1) : item, inner, inTable, lists, depth + 1, id);
                    }
                    break;
                }

                case CodeBlock code:
                    rtf.Append(@"\pard\plain\f1\fs19").Append(inTable ? @"\intbl" : "").Append($@"\li{indentTwips + 240}\sa160 ");
                    var lines = code.Text.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) rtf.Append(@"\line ");
                        rtf.Append(Text(lines[i]));
                    }
                    rtf.Append(end);
                    break;

                case Quote quote:
                    Append(rtf, quote.Blocks, indentTwips + 480, inTable, lists, depth, listId);
                    break;

                case Table table when !inTable:
                    AppendTable(rtf, table, lists);
                    break;

                case Table nested: // RTF's nested tables are a format of their own: the cells as lines of text
                    foreach (var row in nested.Rows)
                    {
                        rtf.Append(start).Append(' ').Append(Text(string.Join("\t", row.Select(PlainTextWriter.CellText)))).Append(end);
                    }
                    break;

                case Picture picture:
                    rtf.Append(start).Append(@"\sa160 ");
                    AppendPicture(rtf, picture, (TextWidthTwips - indentTwips) / 20.0);
                    rtf.Append(end);
                    break;

                case PageBreak when !inTable:
                    rtf.Append(@"\pard\page\par").Append("\r\n");
                    break;

                case Rule:
                    rtf.Append(start).Append(@"\brdrb\brdrs\brdrw10\brsp20\sa160 ").Append(end);
                    break;
            }
        }
    }

    private static void AppendTable(StringBuilder rtf, Table table, List<ListDefinition> lists)
    {
        int columns = table.ColumnCount;
        if (columns == 0) return;
        int columnWidth = TextWidthTwips / columns;

        for (int r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            rtf.Append(@"\trowd\trgaph100").Append(r < table.HeaderRows ? @"\trhdr" : "");
            int column = 0;
            foreach (var cell in row)
            {
                column += Math.Max(cell.ColumnSpan, 1);
                rtf.Append(@"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10")
                    .Append($@"\cellx{Math.Min(column, columns) * columnWidth}");
            }
            rtf.Append("\r\n");
            foreach (var cell in row)
            {
                var content = new StringBuilder();
                var blocks = r < table.HeaderRows ? Embolden(cell.Blocks) : cell.Blocks;
                Append(content, blocks, 0, inTable: true, lists, depth: 0, listId: 0);
                var text = content.ToString();
                // the cell's last paragraph ends with the cell mark, not with a paragraph mark
                if (text.EndsWith("\\par\r\n", StringComparison.Ordinal)) text = text[..^6];
                if (text.Length == 0) text = @"\pard\plain\f0\fs22\intbl ";
                rtf.Append(text).Append(@"\cell").Append("\r\n");
            }
            rtf.Append(@"\row").Append("\r\n");
        }
        rtf.Append(@"\pard\plain\f0\fs22\sa160\par").Append("\r\n");
    }

    /// <summary>A heading row's paragraphs in bold.</summary>
    private static List<Block> Embolden(List<Block> blocks) => blocks.Select(block => block is Paragraph paragraph
        ? new Paragraph(paragraph.Inlines.Select(inline => inline is TextRun run
            ? new TextRun(run.Text, run.Style | TextStyle.Bold, run.Link)
            : inline).ToList()) { Align = paragraph.Align }
        : block).ToList();

    private static void AppendInlines(StringBuilder rtf, IEnumerable<Inline> inlines, bool heading)
    {
        foreach (var inline in inlines)
        {
            if (inline is LineBreak)
            {
                rtf.Append(@"\line ");
                continue;
            }
            if (inline is not TextRun run || run.Text.Length == 0) continue;

            var marks = new StringBuilder();
            if (run.Has(TextStyle.Bold) && !heading) marks.Append(@"\b");
            if (run.Has(TextStyle.Italic)) marks.Append(@"\i");
            if (run.Has(TextStyle.Underline) || run.Link != null) marks.Append(@"\ul");
            if (run.Has(TextStyle.Strike)) marks.Append(@"\strike");
            if (run.Has(TextStyle.Superscript)) marks.Append(@"\super");
            if (run.Has(TextStyle.Subscript)) marks.Append(@"\sub");
            if (run.Has(TextStyle.Code)) marks.Append(@"\f1");
            if (run.Link != null) marks.Append(@"\cf1");

            string text = marks.Length > 0 ? $"{{{marks} {Text(run.Text)}}}" : Text(run.Text);
            if (run.Link != null)
                text = $@"{{\field{{\*\fldinst HYPERLINK ""{Text(run.Link.Replace("\\", "\\\\").Replace("\"", "%22"))}""}}{{\fldrslt {text}}}}}";
            rtf.Append(text);
        }
    }

    private static void AppendPicture(StringBuilder rtf, Picture picture, double maxWidthPt)
    {
        // RTF holds PNG and JPEG; a GIF goes in as a PNG of its first picture
        byte[] data = picture.Data;
        string kind = picture.Format == "jpg" ? @"\jpegblip" : @"\pngblip";
        if (picture.Format == "gif")
        {
            using var image = new ImageMagick.MagickImage(picture.Data);
            data = image.ToByteArray(ImageMagick.MagickFormat.Png);
        }

        var (width, height) = picture.DisplaySize(maxWidthPt, 640);
        rtf.Append(@"{\pict").Append(kind)
            .Append($@"\picw{picture.PixelWidth}\pich{picture.PixelHeight}\picwgoal{(int)(width * 20)}\pichgoal{(int)(height * 20)} ").Append("\r\n");
        var hex = Convert.ToHexString(data);
        for (int at = 0; at < hex.Length; at += 128)
            rtf.Append(hex, at, Math.Min(128, hex.Length - at)).Append("\r\n");
        rtf.Append('}');
    }

    /// <summary>Text in RTF's own spelling: its three special characters marked, everything beyond ASCII as a number.</summary>
    internal static string Text(string text)
    {
        var escaped = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': escaped.Append(@"\\"); break;
                case '{': escaped.Append(@"\{"); break;
                case '}': escaped.Append(@"\}"); break;
                case '\t': escaped.Append(@"\tab "); break;
                case '\n': escaped.Append(@"\line "); break;
                case '\r': break;
                case '\u00A0': escaped.Append(@"\~"); break;
                default:
                    if (c is >= ' ' and < (char)128) escaped.Append(c);
                    else if (c >= 128) escaped.Append(@"\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
        return escaped.ToString();
    }
}
