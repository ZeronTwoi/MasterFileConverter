using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace FileConverter.Core.Sheets;

/// <summary>
/// Takes the tables out of a PDF. A PDF has no tables, only words at positions, so rows and
/// columns are found again from where the words stand: words on one height are a line; a wide
/// gap in a line divides two cells; and where a run of such lines leaves the same strips of the
/// page free, those strips are the borders between columns. Everything else on the page — a
/// title, a paragraph — goes into the first column, a line per row, so that nothing is lost.
///
/// Numbers become numbers when it is clear how the document writes them (1,234.50 or 1.234,50):
/// that is read from the document as a whole, and a number that could be either stays text.
/// </summary>
internal static partial class PdfTableReader
{
    private sealed record Line(double Top, double Bottom, List<Word> Words)
    {
        /// <summary>The cells the line falls into by its own gaps, each a run of words.</summary>
        public List<List<Word>> Segments { get; init; } = [];
    }

    /// <param name="sheetPerPage">A sheet for every page; otherwise all pages one below the other on one sheet.</param>
    public static Workbook ReadFile(string path, bool sheetPerPage, CancellationToken ct)
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
            var pages = new List<List<List<string>>>();
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                pages.Add(ReadPage(page));
            }
            if (pages.All(rows => rows.Count == 0))
                throw new FriendlyException("This PDF has no text to take tables from — its pages may be scans, which are pictures of text.");

            var style = NumberStyle(pages.SelectMany(rows => rows).SelectMany(row => row));
            var workbook = new Workbook { Title = string.IsNullOrWhiteSpace(pdf.Information?.Title) ? null : pdf.Information.Title.Trim() };

            Sheet? all = sheetPerPage ? null : new Sheet("Tables");
            for (int p = 0; p < pages.Count; p++)
            {
                var sheet = all ?? new Sheet($"Page {p + 1}");
                // on one sheet, an empty row marks where a page ended
                if (all != null && p > 0 && all.Rows.Count > 0 && pages[p].Count > 0) all.Rows.Add([]);
                foreach (var row in pages[p]) sheet.Rows.Add(row.Select(text => ToCell(text, style)).ToList());
                if (all == null)
                {
                    sheet.Trim();
                    workbook.Sheets.Add(sheet);
                }
            }
            if (all != null)
            {
                all.Trim();
                workbook.Sheets.Add(all);
            }
            return workbook;
        }
    }

    // ---------- a page: lines, cells, columns ----------

    private static List<List<string>> ReadPage(Page page)
    {
        List<Word> words;
        try
        {
            words = page.GetWords(NearestNeighbourWordExtractor.Instance).Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            words = page.GetWords().Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToList();
        }
        if (words.Count == 0) return [];

        var lines = ToLines(words);
        var rows = new List<List<string>>();
        int index = 0;
        while (index < lines.Count)
        {
            if (lines[index].Segments.Count < 2)
            {
                rows.Add([Text(lines[index].Words)]);
                index++;
                continue;
            }

            // A table: lines of several cells one after another. A line of one cell between two
            // of them (a cell's second line, a sub-heading) belongs to it too.
            int end = index + 1;
            while (end < lines.Count && (lines[end].Segments.Count >= 2 ||
                                         (end + 1 < lines.Count && lines[end + 1].Segments.Count >= 2)))
                end++;
            rows.AddRange(ToRows(lines.GetRange(index, end - index)));
            index = end;
        }
        return rows;
    }

    private static string Text(IEnumerable<Word> words) => string.Join(" ", words.Select(word => word.Text));

    private static List<Line> ToLines(List<Word> words)
    {
        var lines = new List<Line>();
        foreach (var word in words.OrderByDescending(word => word.BoundingBox.Centroid.Y))
        {
            var box = word.BoundingBox;
            double top = Math.Max(box.Top, box.Bottom), bottom = Math.Min(box.Top, box.Bottom), middle = (top + bottom) / 2;
            // on the line before when it sits within that line's height
            var line = lines.Count > 0 ? lines[^1] : null;
            if (line != null && middle < line.Top && middle > line.Bottom)
            {
                line.Words.Add(word);
                lines[^1] = line with { Top = Math.Max(line.Top, top), Bottom = Math.Min(line.Bottom, bottom) };
            }
            else
            {
                lines.Add(new Line(top, bottom, [word]));
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var ordered = lines[i].Words.OrderBy(word => word.BoundingBox.Left).ToList();
            lines[i] = lines[i] with { Words = ordered, Segments = ToSegments(ordered) };
        }
        return lines;
    }

    /// <summary>Divides a line where two words stand clearly further apart than words of one sentence do.</summary>
    private static List<List<Word>> ToSegments(List<Word> words)
    {
        double characters = words.Sum(word => word.Text.Length);
        double width = words.Sum(word => word.BoundingBox.Width);
        double limit = Math.Max(characters > 0 ? width / characters * 1.8 : 6, 4);

        var segments = new List<List<Word>> { new() { words[0] } };
        for (int i = 1; i < words.Count; i++)
        {
            double gap = words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right;
            if (gap > limit) segments.Add([]);
            segments[^1].Add(words[i]);
        }
        return segments;
    }

    /// <summary>
    /// The lines of one table as rows of cells. The columns are those of the table's complete
    /// rows: the lines with the most cells that at least two lines have — a heading row and a row
    /// of values, say. Their first cells are the first column, their second the second, wherever
    /// in the column each stands: headings are often set to the left and numbers to the right, with
    /// nothing above or below one another to go by. A line with fewer or more cells has each of
    /// them put into the column it overlaps.
    /// </summary>
    private static List<List<string>> ToRows(List<Line> lines)
    {
        var counts = lines.Where(line => line.Segments.Count >= 2).GroupBy(line => line.Segments.Count)
            .Select(group => (Cells: group.Key, Lines: group.Count())).ToList();
        int columns = counts.Where(count => count.Lines >= 2).Select(count => count.Cells).DefaultIfEmpty(0).Max();
        if (columns == 0) columns = counts.OrderByDescending(count => count.Lines).ThenByDescending(count => count.Cells).First().Cells;

        // where each column begins and ends across the page, from the complete rows
        var from = Enumerable.Repeat(double.MaxValue, columns).ToArray();
        var to = Enumerable.Repeat(double.MinValue, columns).ToArray();
        foreach (var line in lines.Where(line => line.Segments.Count == columns))
        {
            for (int c = 0; c < columns; c++)
            {
                from[c] = Math.Min(from[c], line.Segments[c][0].BoundingBox.Left);
                to[c] = Math.Max(to[c], line.Segments[c][^1].BoundingBox.Right);
            }
        }

        int ColumnOf(List<Word> segment)
        {
            double left = segment[0].BoundingBox.Left, right = segment[^1].BoundingBox.Right;
            int best = 0;
            double bestOverlap = double.MinValue;
            for (int c = 0; c < columns; c++)
            {
                // how much of it lies in the column; for one that lies in none, how near it is (as a negative distance)
                double overlap = Math.Min(right, to[c]) - Math.Max(left, from[c]);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = c;
                }
            }
            return best;
        }

        var rows = new List<List<string>>();
        foreach (var line in lines)
        {
            var cells = new List<Word>[columns];
            for (int s = 0; s < line.Segments.Count; s++)
            {
                int column = line.Segments.Count == columns ? s : ColumnOf(line.Segments[s]);
                (cells[column] ??= []).AddRange(line.Segments[s]);
            }
            rows.Add(cells.Select(cell => cell == null ? "" : Text(cell)).ToList());
        }
        return rows;
    }

    // ---------- numbers ----------

    internal enum Notation
    {
        /// <summary>Nothing in the document shows how it writes numbers.</summary>
        Unknown,
        /// <summary>1,234.50</summary>
        Point,
        /// <summary>1.234,50</summary>
        Comma
    }

    [GeneratedRegex(@"^-?\d{1,3}(?:,\d{3})+\.\d+$|^-?\d+\.\d{1,2}$|^-?\d+\.\d{4,}$")]
    private static partial Regex ClearlyPoint();

    [GeneratedRegex(@"^-?\d{1,3}(?:\.\d{3})+,\d+$|^-?\d+,\d{1,2}$|^-?\d+,\d{4,}$")]
    private static partial Regex ClearlyComma();

    [GeneratedRegex(@"^-?(?:0|[1-9]\d{0,14})$")]
    private static partial Regex WholeNumber();

    [GeneratedRegex(@"^-?\d{1,3}(?:[.,]\d{3})*(?:[.,]\d+)?$|^-?\d+[.,]\d+$")]
    private static partial Regex Grouped();

    /// <summary>How the document writes its numbers, going by those that can only be read one way.</summary>
    internal static Notation NumberStyle(IEnumerable<string> cells)
    {
        int point = 0, comma = 0;
        foreach (var cell in cells)
        {
            var text = cell.Trim();
            if (text.Length is 0 or > 24) continue;
            if (ClearlyPoint().IsMatch(text)) point++;
            else if (ClearlyComma().IsMatch(text)) comma++;
        }
        if (point == 0 && comma == 0) return Notation.Unknown;
        // a document that seems to do both is not to be trusted with either
        if (point > 0 && comma > 0 && Math.Min(point, comma) * 4 > Math.Max(point, comma)) return Notation.Unknown;
        return point >= comma ? Notation.Point : Notation.Comma;
    }

    internal static Cell ToCell(string text, Notation style)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return Cell.Empty;
        if (trimmed.Length > 24) return Cell.OfText(text);

        if (WholeNumber().IsMatch(trimmed) && double.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out double whole))
            return Cell.OfNumber(whole, trimmed);

        if (style != Notation.Unknown && Grouped().IsMatch(trimmed))
        {
            char decimalMark = style == Notation.Point ? '.' : ',', groupMark = style == Notation.Point ? ',' : '.';
            // more than one decimal mark, or groups that are not thousands, is not a number written this way
            int marks = trimmed.Count(c => c == decimalMark);
            var parts = trimmed.Split(decimalMark);
            bool groupsRight = !parts[0].Contains(groupMark) || parts[0].TrimStart('-').Split(groupMark).Skip(1).All(group => group.Length == 3);
            if (marks <= 1 && groupsRight && !(parts.Length > 1 && parts[1].Contains(groupMark)))
            {
                var plain = trimmed.Replace(groupMark.ToString(), "").Replace(decimalMark, '.');
                if (double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    return Cell.OfNumber(number, trimmed);
            }
        }
        return Cell.OfText(text);
    }
}
