using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace FileConverter.Core.Documents;

/// <summary>
/// Reads the text of a PDF back into a document. A PDF only says where each letter sits on the
/// page, so what was a paragraph or a heading has to be worked out again: letters are grouped
/// into words, lines and blocks by their positions; a short block in clearly larger type than
/// the body text is a heading; bold and italic come from the fonts; pictures are put between
/// the blocks they lie between. Page numbers and the lines repeated at the top and bottom of
/// every page are left out, and a paragraph that runs on over a page end is joined again.
///
/// What cannot be recovered this way: tables (their cells come out as lines of text), columns
/// that the block grouping does not tell apart, and anything in a scanned page, which is a
/// picture of text rather than text.
/// </summary>
internal static partial class PdfTextReader
{
    /// <summary>One block of text on a page, before it is known whether it is a heading.</summary>
    private sealed record Piece(int Page, double Top, double Bottom, double PageHeight, List<List<Span>> Lines, Picture? Picture)
    {
        public double Size { get; init; }
        public string Text => string.Join(" ", Lines.Select(line => string.Concat(line.Select(span => span.Text))));
    }

    private sealed record Span(string Text, TextStyle Style);

    public static RichDocument ReadFile(string path, CancellationToken ct)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new FriendlyException(ex.GetType().Name.Contains("Encrypt") || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                ? "This PDF is password-protected. Remove the password first, then convert it."
                : "This file is not a PDF, or it is damaged.");
        }

        using (pdf)
        {
            var document = new RichDocument
            {
                Title = NullIfEmpty(pdf.Information?.Title),
                Author = NullIfEmpty(pdf.Information?.Author)
            };

            var pieces = new List<Piece>();
            var sizes = new Dictionary<double, int>(); // how many letters there are of each size of type
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                pieces.AddRange(ReadPage(page, sizes));
            }

            double bodySize = sizes.Count == 0 ? 11 : sizes.MaxBy(pair => pair.Value).Key;
            RemoveRunningLines(pieces, pdf.NumberOfPages);
            Build(document, pieces, bodySize);
            return document;
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ---------- one page ----------

    private static List<Piece> ReadPage(Page page, Dictionary<double, int> sizes)
    {
        var pieces = new List<Piece>();
        var letters = page.Letters;

        if (letters.Count > 0)
        {
            IReadOnlyList<TextBlock> blocks;
            try
            {
                var words = NearestNeighbourWordExtractor.Instance.GetWords(letters).ToList();
                blocks = UnsupervisedReadingOrderDetector.Instance.Get(DocstrumBoundingBoxes.Instance.GetBlocks(words)).ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // a page the layout analysis cannot make sense of: its words as they come, in lines
                blocks = DefaultPageSegmenter.Instance.GetBlocks(page.GetWords().ToList());
            }

            foreach (var block in blocks)
            {
                // The lines of a block, each with the size of type it is mostly set in.
                var lines = new List<(List<Span> Spans, double Size, double Top, double Bottom)>();
                foreach (var line in block.TextLines)
                {
                    var spans = new List<Span>();
                    var lineSizes = new Dictionary<double, int>();
                    foreach (var word in line.Words)
                    {
                        if (string.IsNullOrWhiteSpace(word.Text)) continue;
                        var style = StyleOf(word);
                        foreach (var letter in word.Letters)
                        {
                            double size = Math.Round(letter.PointSize * 2) / 2;
                            if (size <= 0) continue;
                            sizes[size] = sizes.GetValueOrDefault(size) + 1;
                            lineSizes[size] = lineSizes.GetValueOrDefault(size) + 1;
                        }
                        if (spans.Count > 0) spans.Add(new Span(" ", spans[^1].Style & style));
                        spans.Add(new Span(Tidy(word.Text), style));
                    }
                    if (spans.Count == 0) continue;
                    var box = line.BoundingBox;
                    lines.Add((spans, lineSizes.Count == 0 ? 0 : lineSizes.MaxBy(pair => pair.Value).Key,
                        Math.Max(box.Top, box.Bottom), Math.Min(box.Top, box.Bottom)));
                }

                // The grouping goes by distances alone, so a heading comes out joined to the paragraph
                // under it, and a page number to the text it stands close below. A block is divided
                // again where the size of type changes, where the lines stand clearly further apart
                // than lines of one paragraph do, and around a page number at the page's edge.
                bool IsPageNumber((List<Span> Spans, double Size, double Top, double Bottom) line) =>
                    (line.Bottom > page.Height * 0.9 || line.Top < page.Height * 0.1) &&
                    PageNumber().IsMatch(string.Concat(line.Spans.Select(span => span.Text)));

                int start = 0;
                for (int i = 1; i <= lines.Count; i++)
                {
                    bool ends = i == lines.Count;
                    if (!ends)
                    {
                        double larger = Math.Max(lines[i].Size, lines[i - 1].Size), smaller = Math.Min(lines[i].Size, lines[i - 1].Size);
                        double gap = lines[i - 1].Bottom - lines[i].Top;
                        ends = larger > smaller * 1.12 || gap > larger * 0.9 || IsPageNumber(lines[i]) || IsPageNumber(lines[i - 1]);
                    }
                    if (!ends) continue;

                    var part = lines.GetRange(start, i - start);
                    pieces.Add(new Piece(page.Number, part.Max(l => l.Top), part.Min(l => l.Bottom), page.Height,
                        part.Select(l => l.Spans).ToList(), null)
                    {
                        Size = part.GroupBy(l => l.Size).MaxBy(group => group.Sum(l => l.Spans.Sum(span => span.Text.Length)))!.Key
                    });
                    start = i;
                }
            }
        }

        foreach (var picture in ReadPictures(page))
        {
            // after the last block that begins above it; first when it tops the page
            int after = pieces.FindLastIndex(piece => piece.Picture == null && piece.Top >= picture.Top);
            pieces.Insert(after + 1, picture);
        }
        return pieces;
    }

    /// <summary>Letters a PDF may carry that would only be in the way: ligatures as one character, stray control characters.</summary>
    private static string Tidy(string text)
    {
        // the ligatures ff, fi, fl, ffi and ffl each as one character (U+FB00 to U+FB04), and the soft hyphen (U+00AD)
        if (!text.Any(c => c < ' ' || c is >= (char)0xFB00 and <= (char)0xFB04 or (char)0xAD)) return text;
        var tidy = new StringBuilder(text.Length + 2);
        foreach (char c in text)
        {
            tidy.Append(c switch
            {
                (char)0xFB00 => "ff",
                (char)0xFB01 => "fi",
                (char)0xFB02 => "fl",
                (char)0xFB03 => "ffi",
                (char)0xFB04 => "ffl",
                (char)0xAD => "",
                < ' ' => "",
                _ => c.ToString()
            });
        }
        return tidy.ToString();
    }

    private static TextStyle StyleOf(Word word)
    {
        var letter = word.Letters.Count > 0 ? word.Letters[word.Letters.Count / 2] : null;
        if (letter == null) return TextStyle.None;

        var style = TextStyle.None;
        var name = letter.FontName ?? "";
        if (letter.FontDetails?.IsBold == true || name.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Black", StringComparison.OrdinalIgnoreCase) || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase))
            style |= TextStyle.Bold;
        if (letter.FontDetails?.IsItalic == true || name.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Oblique", StringComparison.OrdinalIgnoreCase))
            style |= TextStyle.Italic;
        if (name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Consolas", StringComparison.OrdinalIgnoreCase))
            style |= TextStyle.Code;
        return style;
    }

    private static IEnumerable<Piece> ReadPictures(Page page)
    {
        IEnumerable<IPdfImage> images;
        try
        {
            images = page.GetImages().ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            yield break;
        }

        foreach (var image in images)
        {
            var bounds = image.BoundingBox;
            // rules and dots drawn as tiny pictures, and masks, are not pictures to keep
            if (image.IsImageMask || bounds.Width < 12 || bounds.Height < 12 || image.WidthInSamples < 8 || image.HeightInSamples < 8) continue;

            Picture? picture = null;
            try
            {
                if (image.TryGetPng(out var png)) picture = Picture.Load(png, "", Math.Abs(bounds.Width), Math.Abs(bounds.Height));
                // a JPEG is stored in the PDF as the JPEG file it was
                picture ??= Picture.Load(image.RawBytes.ToArray(), "", Math.Abs(bounds.Width), Math.Abs(bounds.Height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // a kind of picture data that cannot be read here
            }
            if (picture == null) continue;

            yield return new Piece(page.Number, Math.Max(bounds.Top, bounds.Bottom), Math.Min(bounds.Top, bounds.Bottom), page.Height, [], picture);
        }
    }

    // ---------- what is repeated on every page ----------

    [GeneratedRegex(@"^\W*(?:page|seite|sayfa|pagina|página|p\.)?\W*\d{1,4}(?:\s*(?:/|of|von|de|sur)\s*\d{1,4})?\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>
    /// Takes out page numbers, and the lines that stand at the same height on most pages (a
    /// running title, a footer) — only from the top and bottom tenth of a page, and only one-liners.
    /// </summary>
    private static void RemoveRunningLines(List<Piece> pieces, int pageCount)
    {
        bool AtEdge(Piece piece) => piece.Picture == null && piece.Lines.Count == 1 &&
                                    (piece.Bottom > piece.PageHeight * 0.9 || piece.Top < piece.PageHeight * 0.1);

        // a page whose only text is at its edge has no body for that to be the header of
        var pagesWithBody = pieces.Where(piece => piece.Picture == null && !AtEdge(piece)).Select(piece => piece.Page).ToHashSet();
        var edge = pieces.Where(piece => AtEdge(piece) && pagesWithBody.Contains(piece.Page)).ToList();
        var repeated = new HashSet<Piece>();
        if (pageCount >= 3)
        {
            foreach (var group in edge.GroupBy(piece => (Digits().Replace(piece.Text, "#").Trim(), Math.Round(piece.Top / 4))))
            {
                if (group.Select(piece => piece.Page).Distinct().Count() >= Math.Max(3, pageCount / 2))
                    foreach (var piece in group) repeated.Add(piece);
            }
        }
        pieces.RemoveAll(piece => repeated.Contains(piece) || (edge.Contains(piece) && PageNumber().IsMatch(piece.Text)));
    }

    // ---------- from blocks to a document ----------

    private static readonly char[] Bullets = ['•', '◦', '▪', '■', '●', '○', '‣', '⁃', '∙', '·'];

    private static void Build(RichDocument document, List<Piece> pieces, double bodySize)
    {
        // The sizes of type used for headings, largest first: they become levels 1, 2, 3.
        var headingSizes = pieces.Where(piece => IsHeading(piece, bodySize)).Select(piece => piece.Size).Distinct()
            .OrderByDescending(size => size).ToList();

        int page = pieces.Count > 0 ? pieces[0].Page : 1;
        Paragraph? open = null; // the last paragraph of the page before, if it may run on
        foreach (var piece in pieces)
        {
            bool newPage = piece.Page != page;
            page = piece.Page;

            if (piece.Picture != null)
            {
                if (newPage) document.Blocks.Add(new PageBreak());
                document.Blocks.Add(piece.Picture);
                open = null;
                continue;
            }

            if (IsHeading(piece, bodySize))
            {
                if (newPage) document.Blocks.Add(new PageBreak());
                int level = Math.Min(headingSizes.IndexOf(piece.Size) + 1, 3);
                // the larger type already says "heading": bold on top of it would only be carried along
                document.Blocks.Add(new Heading(level, Inlines(piece.Lines, drop: TextStyle.Bold)));
                open = null;
                continue;
            }

            var items = SplitAtBullets(piece.Lines);
            if (items != null)
            {
                if (newPage) document.Blocks.Add(new PageBreak());
                var list = new ListBlock(ordered: false);
                foreach (var item in items) list.Items.Add([new Paragraph(Inlines(item))]);
                document.Blocks.Add(list);
                open = null;
                continue;
            }

            var inlines = Inlines(piece.Lines);
            if (inlines.Count == 0) continue;
            var first = inlines.OfType<TextRun>().First().Text;

            // a sentence cut in two by the end of a page is one paragraph again
            if (newPage && open != null && first.Length > 0 && char.IsLower(first[0]))
            {
                open.Inlines.Add(new TextRun(" "));
                open.Inlines.AddRange(inlines);
            }
            else
            {
                if (newPage) document.Blocks.Add(new PageBreak());
                open = new Paragraph(inlines);
                document.Blocks.Add(open);
            }

            var last = RichDocument.PlainText(open.Inlines).TrimEnd();
            if (last.Length == 0 || ".!?:;\"”»)]".Contains(last[^1])) open = null;
        }

        document.Title ??= document.Blocks.OfType<Heading>().FirstOrDefault(h => h.Level == 1) is { } heading
            ? RichDocument.PlainText(heading.Inlines)
            : null;
    }

    /// <summary>Short, and set clearly larger than the text around it.</summary>
    private static bool IsHeading(Piece piece, double bodySize) =>
        piece.Picture == null && piece.Lines.Count <= 3 && piece.Size >= bodySize * 1.18 && piece.Text.Length is > 0 and <= 200;

    /// <summary>The lines of a block as its bulleted items, or null when it is not a list.</summary>
    private static List<List<List<Span>>>? SplitAtBullets(List<List<Span>> lines)
    {
        static bool StartsItem(List<Span> line) => line.Count > 0 && line[0].Text.Length > 0 && Bullets.Contains(line[0].Text[0]);
        if (!StartsItem(lines[0])) return null;

        var items = new List<List<List<Span>>>();
        foreach (var line in lines)
        {
            if (StartsItem(line))
            {
                // without the bullet itself, and the space after it
                var rest = new List<Span>(line);
                rest[0] = rest[0] with { Text = rest[0].Text[1..] };
                while (rest.Count > 0 && string.IsNullOrWhiteSpace(rest[0].Text)) rest.RemoveAt(0);
                items.Add([rest]);
            }
            else
            {
                items[^1].Add(line);
            }
        }
        items.RemoveAll(item => item.All(line => line.Count == 0));
        return items.Count == 0 ? null : items;
    }

    /// <summary>Lines joined into running text: a word divided at the end of a line is put together again.</summary>
    private static List<Inline> Inlines(List<List<Span>> lines, TextStyle drop = TextStyle.None)
    {
        var builder = new BlockBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Count == 0) continue;

            bool divided = false;
            if (i + 1 < lines.Count && lines[i + 1].Count > 0)
            {
                var end = line[^1].Text;
                var next = lines[i + 1][0].Text;
                divided = end.Length >= 3 && end[^1] == '-' && char.IsLetter(end[^2]) && next.Length > 0 && char.IsLower(next[0]);
            }

            for (int s = 0; s < line.Count; s++)
            {
                var text = line[s].Text;
                if (divided && s == line.Count - 1) text = text[..^1];
                builder.Text(text, line[s].Style & ~drop);
            }
            if (!divided && i + 1 < lines.Count) builder.Text(" ", line[^1].Style & ~drop);
        }
        return builder.TakeInlines();
    }
}
