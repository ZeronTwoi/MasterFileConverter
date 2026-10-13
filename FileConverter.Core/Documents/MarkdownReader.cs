using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace FileConverter.Core.Documents;

/// <summary>
/// Reads Markdown (CommonMark with tables, strikethrough and task lists, as GitHub writes it).
/// Pictures are taken from beside the file; pictures on the internet are left out.
/// </summary>
internal sealed class MarkdownReader
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .UseYamlFrontMatter()
        .Build();

    private readonly Func<string, byte[]?> _resource;

    public MarkdownReader(Func<string, byte[]?>? resource = null) => _resource = resource ?? (_ => null);

    public static RichDocument ReadFile(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return new MarkdownReader(address => HtmlReader.ReadBeside(folder, address)).Read(Converters.TextFiles.Read(path));
    }

    public RichDocument Read(string markdown)
    {
        var parsed = Markdown.Parse(markdown, Pipeline);
        var document = new RichDocument();
        document.Blocks.AddRange(ReadBlocks(parsed));
        document.Title = document.Blocks.OfType<Heading>().FirstOrDefault(h => h.Level == 1) is { } heading
            ? RichDocument.PlainText(heading.Inlines)
            : null;
        return document;
    }

    private List<Block> ReadBlocks(IEnumerable<Markdig.Syntax.Block> blocks)
    {
        var builder = new BlockBuilder();
        foreach (var block in blocks) Read(block, builder);
        builder.EndParagraph();
        return builder.Blocks;
    }

    private void Read(Markdig.Syntax.Block block, BlockBuilder builder)
    {
        switch (block)
        {
            case HeadingBlock heading:
                ReadInlines(heading.Inline, builder, TextStyle.None, null);
                builder.EndHeading(heading.Level);
                break;

            case ParagraphBlock paragraph:
                ReadInlines(paragraph.Inline, builder, TextStyle.None, null);
                builder.EndParagraph();
                break;

            case Markdig.Syntax.ListBlock list:
            {
                var result = new ListBlock(list.IsOrdered)
                {
                    Start = int.TryParse(list.OrderedStart, out int start) ? start : 1
                };
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var blocks = ReadBlocks(item);
                    if (blocks.Count > 0) result.Items.Add(blocks);
                }
                if (result.Items.Count > 0) builder.Add(result);
                break;
            }

            case Markdig.Syntax.CodeBlock code: // fenced or indented
                builder.Add(new Documents.CodeBlock(code.Lines.ToString()));
                break;

            case QuoteBlock quote:
            {
                var blocks = ReadBlocks(quote);
                if (blocks.Count > 0) builder.Add(new Quote(blocks));
                break;
            }

            case ThematicBreakBlock:
                builder.Add(new Rule());
                break;

            case HtmlBlock html:
                // HTML written straight into the Markdown is read as HTML
                builder.AddRange(new HtmlReader(_resource).Read(html.Lines.ToString()).Blocks);
                break;

            case MdTable table:
            {
                var result = new Documents.Table();
                foreach (var row in table.OfType<TableRow>())
                {
                    var cells = row.OfType<MdTableCell>()
                        .Select(cell => new Documents.TableCell(ReadBlocks(cell)) { ColumnSpan = Math.Max(cell.ColumnSpan, 1) })
                        .ToList();
                    if (cells.Count == 0) continue;
                    if (row.IsHeader && result.HeaderRows == result.Rows.Count) result.HeaderRows++;
                    result.Rows.Add(cells);
                }
                if (result.Rows.Count > 0) builder.Add(result);
                break;
            }

            case ContainerBlock container: // anything else that holds blocks: its content
                builder.AddRange(ReadBlocks(container));
                break;

            // link definitions, front matter: nothing that shows
        }
    }

    private void ReadInlines(ContainerInline? container, BlockBuilder builder, TextStyle style, string? link)
    {
        if (container == null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Text(literal.Content.ToString(), style, link);
                    break;

                case CodeInline code:
                    builder.Text(code.Content, style | TextStyle.Code, link);
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard) builder.Break();
                    else builder.Text(" ", style, link);
                    break;

                case EmphasisInline emphasis:
                    ReadInlines(emphasis, builder, style | emphasis.DelimiterChar switch
                    {
                        '*' or '_' => emphasis.DelimiterCount >= 2 ? TextStyle.Bold : TextStyle.Italic,
                        '~' => emphasis.DelimiterCount >= 2 ? TextStyle.Strike : TextStyle.Subscript,
                        '^' => TextStyle.Superscript,
                        '+' => TextStyle.Underline,
                        _ => TextStyle.None
                    }, link);
                    break;

                case LinkInline { IsImage: true } image:
                {
                    var address = image.Url ?? "";
                    bool online = address.Contains("://") && !address.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
                    var picture = online ? null : Picture.Load(_resource(address), AltText(image));
                    if (picture != null) builder.Add(picture);
                    else builder.Text(AltText(image), style, link); // what the picture stood for, at least
                    break;
                }

                case LinkInline hyperlink:
                    ReadInlines(hyperlink, builder, style, string.IsNullOrWhiteSpace(hyperlink.Url) || hyperlink.Url.StartsWith('#') ? link : hyperlink.Url);
                    break;

                case AutolinkInline auto:
                    builder.Text(auto.Url, style, auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                    break;

                case HtmlEntityInline entity:
                    builder.Text(entity.Transcoded.ToString(), style, link);
                    break;

                case HtmlInline html:
                    if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) builder.Break();
                    break;

                case TaskList task:
                    builder.Text(task.Checked ? "☑ " : "☐ ", style, link);
                    break;

                case ContainerInline nested:
                    ReadInlines(nested, builder, style, link);
                    break;
            }
        }
    }

    private static string AltText(ContainerInline image)
    {
        var text = new System.Text.StringBuilder();
        foreach (var literal in image.Descendants<LiteralInline>()) text.Append(literal.Content.ToString());
        return text.ToString();
    }
}
