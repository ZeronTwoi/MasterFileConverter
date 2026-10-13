using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ExcelDataReader;
using ExcelDataReader.Exceptions;
using ExcelNumberFormat;
using FileConverter.Core.Converters;
using FileConverter.Core.Documents;
using FileConverter.Core.Platform;

namespace FileConverter.Core.Sheets;

/// <summary>Reads Excel workbooks, both the current format (.xlsx) and the old binary one (.xls).</summary>
internal static class ExcelReader
{
    /// <summary>The largest sheet that is taken in whole: beyond it a conversion would only run out of memory.</summary>
    private const int MaxRows = 1_048_576, MaxColumns = 2_000;

    public static Workbook ReadFile(string path)
    {
        // the old format writes text in the code page of the system it was saved on
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding fallback;
        try { fallback = Encoding.GetEncoding(PlatformServices.Current.LegacyCodePage is > 0 and var codePage ? codePage : 1252); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { fallback = Encoding.Latin1; }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { FallbackEncoding = fallback });

            var workbook = new Workbook();
            var formats = new Dictionary<string, NumberFormat>(StringComparer.Ordinal);
            do
            {
                // a hidden sheet is not part of what the workbook shows, or prints
                if (reader.VisibleState is "hidden" or "veryhidden") continue;

                var sheet = new Sheet(reader.Name ?? $"Sheet{workbook.Sheets.Count + 1}");
                int columns = Math.Min(reader.FieldCount, MaxColumns);
                while (reader.Read() && sheet.Rows.Count < MaxRows)
                {
                    var row = new List<Cell>(columns);
                    for (int c = 0; c < columns; c++)
                        row.Add(ToCell(reader.GetValue(c), reader.GetNumberFormatString(c), formats));
                    sheet.Rows.Add(row);
                }

                for (int c = 0; c < columns; c++)
                {
                    double width;
                    try { width = reader.GetColumnWidth(c); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { break; }
                    sheet.ColumnWidths.Add(width);
                }
                foreach (var range in reader.MergeCells ?? [])
                {
                    if (range.ToRow > range.FromRow || range.ToColumn > range.FromColumn)
                        sheet.Merges.Add(new MergedRange(range.FromRow, range.FromColumn, range.ToRow - range.FromRow + 1, range.ToColumn - range.FromColumn + 1));
                }

                sheet.Trim();
                workbook.Sheets.Add(sheet);
            } while (reader.NextResult());
            return workbook;
        }
        catch (InvalidPasswordException)
        {
            throw new FriendlyException("This workbook is protected by a password. Remove the password first, then convert it.");
        }
        catch (Exception ex) when (ex is HeaderException or ExcelReaderException or InvalidDataException or EndOfStreamException)
        {
            throw new FriendlyException("This file is not an Excel workbook, or it is damaged.");
        }
    }

    /// <summary>The date formats that mean "as this country writes a date": shown here the one way every country reads alike.</summary>
    private static readonly HashSet<string> LocalDateFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "m/d/yyyy", "d/m/yyyy", "mm/dd/yyyy", "dd/mm/yyyy", "m/d/yy", "d/m/yy", "mm-dd-yy", "dd/mm/yy", "mm/dd/yy"
    };

    private static Cell ToCell(object? value, string? format, Dictionary<string, NumberFormat> formats)
    {
        switch (value)
        {
            case null or DBNull:
                return Cell.Empty;
            case string text:
                return Cell.OfText(text);
            case bool flag:
                return Cell.OfBoolean(flag);
            case DateTime date:
                return Cell.OfDate(date, format != null && !LocalDateFormats.Contains(format) ? Shown(date, format, formats) : null, format);
            case TimeSpan time:
                // a duration or a time of day, kept as the fraction of a day a spreadsheet counts it as
                return Cell.OfNumber(time.TotalDays, Shown(time, format, formats) ?? time.ToString(), format);
            case IConvertible number:
            {
                double amount;
                try { amount = number.ToDouble(CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return Cell.OfText(value.ToString()); }
                bool general = string.IsNullOrEmpty(format) || format.Equals("General", StringComparison.OrdinalIgnoreCase);
                return Cell.OfNumber(amount, general ? null : Shown(amount, format, formats), general ? null : format);
            }
            default:
                return Cell.OfText(value.ToString());
        }
    }

    /// <summary>The value as its number format shows it; null when the format cannot be applied.</summary>
    private static string? Shown(object value, string? format, Dictionary<string, NumberFormat> formats)
    {
        if (string.IsNullOrEmpty(format)) return null;
        if (!formats.TryGetValue(format, out var parsed)) formats[format] = parsed = new NumberFormat(format);
        if (!parsed.IsValid) return null;
        try
        {
            var text = parsed.Format(value, CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>Reads an OpenDocument spreadsheet (.ods, LibreOffice Calc's format).</summary>
internal static class OdsReader
{
    private const int MaxRows = 1_048_576, MaxColumns = 2_000;

    public static Workbook ReadFile(string path)
    {
        using var zip = Odf.Open(path);
        var content = Odf.ReadXml(zip, "content.xml") ?? throw new FriendlyException("This OpenDocument file is damaged.");
        var spreadsheet = content.Root?.Element(Odf.Office + "body")?.Element(Odf.Office + "spreadsheet")
            ?? throw new FriendlyException("This file is not an OpenDocument spreadsheet.");

        var workbook = new Workbook
        {
            Title = Odf.ReadXml(zip, "meta.xml")?.Descendants(Odf.Dc + "title").FirstOrDefault()?.Value is { Length: > 0 } title ? title : null
        };
        foreach (var table in spreadsheet.Elements(Odf.Table + "table"))
            workbook.Sheets.Add(ReadSheet(table, workbook.Sheets.Count + 1));
        return workbook;
    }

    private static Sheet ReadSheet(XElement table, int number)
    {
        var sheet = new Sheet(table.Attribute(Odf.Table + "name")?.Value is { Length: > 0 } name ? name : $"Sheet{number}");

        // A sheet ends in a million empty rows written as one row "repeated a million times": empty
        // rows and cells are only counted, and made real when something comes after them.
        int emptyRows = 0;
        foreach (var rowElement in Rows(table))
        {
            int repeat = Repeat(rowElement, Odf.Table + "number-rows-repeated");
            var row = ReadRow(rowElement, sheet, sheet.Rows.Count + emptyRows);
            if (row.Count == 0)
            {
                emptyRows += repeat;
                continue;
            }

            for (; emptyRows > 0 && sheet.Rows.Count < MaxRows; emptyRows--) sheet.Rows.Add([]);
            emptyRows = 0;
            for (int i = 0; i < repeat && sheet.Rows.Count < MaxRows; i++) sheet.Rows.Add(i == 0 ? row : [.. row]);
        }
        sheet.Trim();
        return sheet;
    }

    private static IEnumerable<XElement> Rows(XElement container)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == Odf.Table + "table-row") yield return element;
            else if (element.Name == Odf.Table + "table-header-rows" || element.Name == Odf.Table + "table-rows" || element.Name == Odf.Table + "table-row-group")
                foreach (var row in Rows(element)) yield return row;
        }
    }

    private static int Repeat(XElement element, XName attribute) =>
        int.TryParse(element.Attribute(attribute)?.Value, out int count) && count > 1 ? count : 1;

    private static List<Cell> ReadRow(XElement rowElement, Sheet sheet, int rowIndex)
    {
        var row = new List<Cell>();
        int emptyCells = 0;
        foreach (var element in rowElement.Elements())
        {
            bool covered = element.Name == Odf.Table + "covered-table-cell";
            if (!covered && element.Name != Odf.Table + "table-cell") continue;

            int repeat = Repeat(element, Odf.Table + "number-columns-repeated");
            var cell = covered ? Cell.Empty : ReadCell(element);
            if (cell.IsEmpty)
            {
                emptyCells += repeat;
            }
            else
            {
                for (; emptyCells > 0 && row.Count < MaxColumns; emptyCells--) row.Add(Cell.Empty);
                emptyCells = 0;

                int columnSpan = Repeat(element, Odf.Table + "number-columns-spanned"), rowSpan = Repeat(element, Odf.Table + "number-rows-spanned");
                if (columnSpan > 1 || rowSpan > 1) sheet.Merges.Add(new MergedRange(rowIndex, row.Count, rowSpan, columnSpan));
                for (int i = 0; i < repeat && row.Count < MaxColumns; i++) row.Add(cell);
            }
        }
        return row;
    }

    private static Cell ReadCell(XElement element)
    {
        // what the cell shows: its paragraphs (the note attached to a cell is not part of it)
        var lines = element.Elements(Odf.Text + "p").Select(Flatten).ToList();
        var shown = string.Join("\n", lines);

        switch (element.Attribute(Odf.Office + "value-type")?.Value)
        {
            case "float" or "percentage" or "currency"
                when double.TryParse(element.Attribute(Odf.Office + "value")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number):
                return Cell.OfNumber(number, shown.Length > 0 ? shown : null,
                    element.Attribute(Odf.Office + "value-type")!.Value == "percentage" ? "0.00%" : null);

            case "date" when DateTime.TryParse(element.Attribute(Odf.Office + "date-value")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.NoCurrentDateDefault, out var date):
                return Cell.OfDate(date, shown.Length > 0 ? shown : null);

            case "boolean":
                return Cell.OfBoolean(element.Attribute(Odf.Office + "boolean-value")?.Value == "true");

            default:
                return Cell.OfText(shown);
        }
    }

    /// <summary>The text of a paragraph in a cell, with the spaces and tabs the format writes as elements.</summary>
    private static string Flatten(XElement paragraph)
    {
        var text = new StringBuilder();
        void Walk(XElement element)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XText plain) text.Append(plain.Value);
                else if (node is XElement child)
                {
                    if (child.Name == Odf.Text + "s") text.Append(' ', Repeat(child, Odf.Text + "c"));
                    else if (child.Name == Odf.Text + "tab") text.Append('\t');
                    else if (child.Name == Odf.Text + "line-break") text.Append('\n');
                    else if (child.Name.Namespace == Odf.Text) Walk(child);
                }
            }
        }
        Walk(paragraph);
        return text.ToString();
    }
}

/// <summary>
/// Reads comma-separated text (.csv). Whether it is separated by commas, semicolons or tabs is
/// worked out from the file, and so is its encoding. What is plainly a number becomes a number.
/// </summary>
internal static partial class CsvReader
{
    public static Workbook ReadFile(string path)
    {
        var text = TextFiles.Read(path);
        char separator = FindSeparator(text);
        var sheet = new Sheet(SheetName(Path.GetFileNameWithoutExtension(path)));

        foreach (var fields in Parse(text, separator))
        {
            var row = new List<Cell>(fields.Count);
            foreach (var field in fields) row.Add(ToCell(field.Text, field.Quoted, separator));
            sheet.Rows.Add(row);
        }
        sheet.Trim();

        var workbook = new Workbook();
        workbook.Sheets.Add(sheet);
        return workbook;
    }

    /// <summary>A sheet name every spreadsheet program takes: at most 31 characters and none of \ / ? * [ ] :</summary>
    internal static string SheetName(string name)
    {
        var clean = new string(name.Select(c => c is '\\' or '/' or '?' or '*' or '[' or ']' or ':' or '\'' || char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (clean.Length > 31) clean = clean[..31].Trim();
        return clean.Length == 0 ? "Sheet1" : clean;
    }

    /// <summary>
    /// The character that divides the fields: of comma, semicolon, tab and bar, the one that
    /// divides the first lines into the same number of fields each — and into the most.
    /// </summary>
    internal static char FindSeparator(string text)
    {
        char best = ',';
        double bestScore = 0;
        foreach (char candidate in new[] { ',', ';', '\t', '|' })
        {
            var counts = Parse(text, candidate).Take(30).Select(fields => fields.Count).ToList();
            if (counts.Count == 0) continue;
            int usual = counts.GroupBy(count => count).OrderByDescending(group => group.Count()).First().Key;
            if (usual < 2) continue;
            // how many lines agree, weighted by how many fields that makes
            double score = counts.Count(count => count == usual) / (double)counts.Count * usual;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    internal static IEnumerable<List<(string Text, bool Quoted)>> Parse(string text, char separator)
    {
        var fields = new List<(string, bool)>();
        var field = new StringBuilder();
        bool quoted = false, wasQuoted = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    quoted = false;
                }
                else field.Append(c);
                i++;
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = wasQuoted = true;
                i++;
            }
            else if (c == separator)
            {
                fields.Add((field.ToString(), wasQuoted));
                field.Clear();
                wasQuoted = false;
                i++;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                fields.Add((field.ToString(), wasQuoted));
                field.Clear();
                wasQuoted = false;
                yield return fields;
                fields = [];
            }
            else
            {
                field.Append(c);
                i++;
            }
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add((field.ToString(), wasQuoted));
            yield return fields;
        }
    }

    [GeneratedRegex(@"^-?(?:0|[1-9]\d{0,14})(?:\.\d{1,15})?$")]
    private static partial Regex PointNumber();

    [GeneratedRegex(@"^-?(?:0|[1-9]\d{0,14}),\d{1,15}$")]
    private static partial Regex CommaNumber();

    /// <summary>
    /// A field as a cell. Only what can be nothing but a number becomes one: a code with leading
    /// zeros, a telephone number or anything that was put in quotes stays the text it is. Where
    /// fields are divided by semicolons or tabs, a comma in a number is its decimal point.
    /// </summary>
    internal static Cell ToCell(string field, bool quoted, char separator)
    {
        if (field.Length == 0) return Cell.Empty;
        var trimmed = field.Trim();
        if (!quoted && trimmed.Length is > 0 and <= 17)
        {
            if (PointNumber().IsMatch(trimmed) && double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain))
                return Cell.OfNumber(plain, trimmed);
            if (separator != ',' && CommaNumber().IsMatch(trimmed) &&
                double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double comma))
                return Cell.OfNumber(comma, trimmed);
        }
        return Cell.OfText(field);
    }
}
