using System.Globalization;

namespace FileConverter.Core.Sheets;

internal enum CellKind
{
    Empty,
    Text,
    Number,
    /// <summary>A day, or a day and a time of day.</summary>
    Date,
    Boolean
}

/// <summary>
/// One cell: what it holds, and what it shows. A spreadsheet keeps the two apart — the number
/// 0.125 may show as "12.5%" and a day count as a date — and each target takes what suits it:
/// another spreadsheet the value with its format, a PDF or a web page the text as shown.
/// </summary>
internal readonly record struct Cell(CellKind Kind, string Text, double Number = 0, DateTime Date = default, string? Format = null)
{
    public static readonly Cell Empty = new(CellKind.Empty, "");

    public bool IsEmpty => Kind == CellKind.Empty || (Kind == CellKind.Text && Text.Length == 0);

    public static Cell OfText(string? text) => string.IsNullOrEmpty(text) ? Empty : new Cell(CellKind.Text, text);

    public static Cell OfNumber(double number, string? text = null, string? format = null) =>
        new(CellKind.Number, text ?? General(number), number, default, format);

    public static Cell OfBoolean(bool value) => new(CellKind.Boolean, value ? "TRUE" : "FALSE", value ? 1 : 0);

    public static Cell OfDate(DateTime date, string? text = null, string? format = null) =>
        new(CellKind.Date, text ?? Iso(date), 0, date, format);

    /// <summary>A number as a spreadsheet shows it without a format of its own: as short as it is exact, up to 15 digits.</summary>
    public static string General(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) return "";
        if (number == Math.Floor(number) && Math.Abs(number) < 1e15) return number.ToString("0", CultureInfo.InvariantCulture);
        return number.ToString("G15", CultureInfo.InvariantCulture);
    }

    /// <summary>A date the way no country misreads it: 2026-10-13, with the time when there is one.</summary>
    public static string Iso(DateTime date) => date.TimeOfDay == TimeSpan.Zero
        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : date.ToString(date.Second == 0 ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>A block of cells shown as one.</summary>
internal readonly record struct MergedRange(int Row, int Column, int RowSpan, int ColumnSpan);

internal sealed class Sheet(string name)
{
    public string Name { get; set; } = name;

    /// <summary>The rows, each as long as its last used cell.</summary>
    public List<List<Cell>> Rows { get; } = [];

    public List<MergedRange> Merges { get; } = [];

    /// <summary>Column widths in characters, where the source says; a width of 0 or a missing one is worked out from the content.</summary>
    public List<double> ColumnWidths { get; } = [];

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(row => row.Count);

    public bool IsEmpty => Rows.All(row => row.All(cell => cell.IsEmpty));

    public Cell this[int row, int column] => row < Rows.Count && column < Rows[row].Count ? Rows[row][column] : Cell.Empty;

    /// <summary>Drops empty cells at the end of every row and empty rows at the end of the sheet.</summary>
    public void Trim()
    {
        foreach (var row in Rows)
        {
            int last = row.Count - 1;
            while (last >= 0 && row[last].IsEmpty) last--;
            if (last < row.Count - 1) row.RemoveRange(last + 1, row.Count - last - 1);
        }
        int end = Rows.Count - 1;
        while (end >= 0 && Rows[end].Count == 0) end--;
        if (end < Rows.Count - 1) Rows.RemoveRange(end + 1, Rows.Count - end - 1);
    }

    /// <summary>
    /// How wide each column is to be, in characters: the source's width where it gave one, else
    /// wide enough for what is in it (looking at the first rows only — a long sheet does not
    /// need reading twice), within sensible limits.
    /// </summary>
    public double[] FittedWidths(int sampleRows = 500, double minimum = 4, double maximum = 60)
    {
        var widths = LongestLines(sampleRows).Select(longest => longest * 1.1 + 1.5).ToArray(); // numbers and capitals run a little wide
        for (int c = 0; c < widths.Length; c++)
        {
            if (c < ColumnWidths.Count && ColumnWidths[c] > 0.5) widths[c] = ColumnWidths[c];
            widths[c] = Math.Clamp(widths[c], minimum, maximum);
        }
        return widths;
    }

    /// <summary>For every column, the number of characters in the longest line any of its cells shows.</summary>
    public int[] LongestLines(int sampleRows = 500)
    {
        var longest = new int[ColumnCount];
        for (int r = 0; r < Math.Min(Rows.Count, sampleRows); r++)
        {
            var row = Rows[r];
            for (int c = 0; c < row.Count; c++)
                foreach (var line in row[c].Text.Split('\n'))
                    longest[c] = Math.Max(longest[c], line.Length);
        }
        return longest;
    }
}

/// <summary>A spreadsheet file: one or more sheets of cells.</summary>
internal sealed class Workbook
{
    public string? Title { get; set; }
    public List<Sheet> Sheets { get; } = [];

    /// <summary>The sheets that have anything on them.</summary>
    public IEnumerable<Sheet> UsedSheets => Sheets.Where(sheet => !sheet.IsEmpty);
}
