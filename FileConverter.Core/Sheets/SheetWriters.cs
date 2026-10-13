using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FileConverter.Core.Converters;
using FileConverter.Core.Documents;
using FileConverter.Core.Platform;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Colors = QuestPDF.Helpers.Colors;
using K = FileConverter.Core.Settings.SettingKeys;
using QDocument = QuestPDF.Fluent.Document;
using XSheet = DocumentFormat.OpenXml.Spreadsheet.Sheet;

namespace FileConverter.Core.Sheets;

/// <summary>
/// Writes an Excel workbook (.xlsx): numbers as numbers, dates as dates, each with the format
/// it had, merged cells merged, columns as wide as their content. The cells are written straight
/// to the file as they come, so a sheet of a million rows needs no more memory than a small one.
/// </summary>
internal static class XlsxWriter
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private const string IsoDate = "yyyy-mm-dd", IsoDateTime = "yyyy-mm-dd hh:mm:ss";

    public static void Write(Workbook workbook, string output)
    {
        var sheets = workbook.UsedSheets.ToList();
        if (sheets.Count == 0) sheets = [workbook.Sheets.FirstOrDefault() ?? new Sheet("Sheet1")];

        using var file = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        var book = file.AddWorkbookPart();
        var list = new DocumentFormat.OpenXml.Spreadsheet.Sheets();
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var formats = new Dictionary<string, int>(StringComparer.Ordinal); // number format → its cell style

        var names = UniqueNames(sheets);
        for (int i = 0; i < sheets.Count; i++)
        {
            var part = book.AddNewPart<WorksheetPart>();
            WriteSheet(part, sheets[i], strings, formats);
            list.Append(new XSheet { Name = names[i], SheetId = (uint)(i + 1), Id = book.GetIdOfPart(part) });
        }

        WriteStrings(book.AddNewPart<SharedStringTablePart>(), strings);
        book.AddNewPart<WorkbookStylesPart>().Stylesheet = Styles(formats);
        book.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook(list);
        book.Workbook.Save();
        if (workbook.Title != null) file.PackageProperties.Title = workbook.Title;
    }

    /// <summary>Sheet names as Excel takes them — short enough, without its forbidden characters, no two alike.</summary>
    internal static List<string> UniqueNames(List<Sheet> sheets)
    {
        var names = new List<string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheet in sheets)
        {
            var name = CsvReader.SheetName(sheet.Name);
            for (int n = 2; !taken.Add(name); n++)
            {
                var suffix = $" ({n})";
                var stem = CsvReader.SheetName(sheet.Name);
                name = (stem.Length + suffix.Length > 31 ? stem[..(31 - suffix.Length)] : stem) + suffix;
            }
            names.Add(name);
        }
        return names;
    }

    private static void WriteSheet(WorksheetPart part, Sheet sheet, Dictionary<string, int> strings, Dictionary<string, int> formats)
    {
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        string ns = Main.NamespaceName;

        xml.WriteStartElement("worksheet", ns);
        var widths = sheet.FittedWidths();
        if (widths.Length > 0)
        {
            xml.WriteStartElement("cols", ns);
            for (int c = 0; c < widths.Length; c++)
            {
                xml.WriteStartElement("col", ns);
                xml.WriteAttributeString("min", (c + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("max", (c + 1).ToString(CultureInfo.InvariantCulture));
                xml.WriteAttributeString("width", widths[c].ToString("0.##", CultureInfo.InvariantCulture));
                xml.WriteAttributeString("customWidth", "1");
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }

        xml.WriteStartElement("sheetData", ns);
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            if (row.Count == 0) continue;
            xml.WriteStartElement("row", ns);
            xml.WriteAttributeString("r", (r + 1).ToString(CultureInfo.InvariantCulture));
            for (int c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                if (cell.IsEmpty) continue;
                xml.WriteStartElement("c", ns);
                xml.WriteAttributeString("r", Reference(r, c));
                switch (cell.Kind)
                {
                    case CellKind.Number:
                        if (cell.Format != null) xml.WriteAttributeString("s", Style(cell.Format, formats));
                        xml.WriteElementString("v", ns, cell.Number.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case CellKind.Date:
                        // a date is the number of days since the end of 1899, shown through a date format
                        xml.WriteAttributeString("s", Style(cell.Format ?? (cell.Date.TimeOfDay == TimeSpan.Zero ? IsoDate : IsoDateTime), formats));
                        xml.WriteElementString("v", ns, cell.Date.ToOADate().ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case CellKind.Boolean:
                        xml.WriteAttributeString("t", "b");
                        xml.WriteElementString("v", ns, cell.Number != 0 ? "1" : "0");
                        break;
                    default:
                    {
                        var text = XhtmlWriter.Clean(cell.Text.Length > 32767 ? cell.Text[..32767] : cell.Text);
                        if (!strings.TryGetValue(text, out int index)) strings[text] = index = strings.Count;
                        xml.WriteAttributeString("t", "s");
                        // a cell of several lines shows them only when it is told to wrap
                        if (text.Contains('\n')) xml.WriteAttributeString("s", Style("@wrap", formats));
                        xml.WriteElementString("v", ns, index.ToString(CultureInfo.InvariantCulture));
                        break;
                    }
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();

        var merges = sheet.Merges.Where(m => m.RowSpan > 1 || m.ColumnSpan > 1).ToList();
        if (merges.Count > 0)
        {
            xml.WriteStartElement("mergeCells", ns);
            xml.WriteAttributeString("count", merges.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var merge in merges)
            {
                xml.WriteStartElement("mergeCell", ns);
                xml.WriteAttributeString("ref", Reference(merge.Row, merge.Column) + ":" + Reference(merge.Row + merge.RowSpan - 1, merge.Column + merge.ColumnSpan - 1));
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static string Style(string format, Dictionary<string, int> formats)
    {
        // style 0 is "nothing special"; every format in use gets the next number
        if (!formats.TryGetValue(format, out int index)) formats[format] = index = formats.Count + 1;
        return index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"A1" for row 0, column 0; "AB7" for row 6, column 27.</summary>
    internal static string Reference(int row, int column)
    {
        var letters = new StringBuilder();
        for (int n = column + 1; n > 0; n = (n - 1) / 26) letters.Insert(0, (char)('A' + (n - 1) % 26));
        return letters.Append(row + 1).ToString();
    }

    private static void WriteStrings(SharedStringTablePart part, Dictionary<string, int> strings)
    {
        using var stream = part.GetStream(FileMode.Create, FileAccess.Write);
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        string ns = Main.NamespaceName;
        xml.WriteStartElement("sst", ns);
        xml.WriteAttributeString("count", strings.Count.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("uniqueCount", strings.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var text in strings.OrderBy(pair => pair.Value).Select(pair => pair.Key))
        {
            xml.WriteStartElement("si", ns);
            xml.WriteStartElement("t", ns);
            xml.WriteAttributeString("xml", "space", null, "preserve");
            xml.WriteString(text);
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
    }

    private static Stylesheet Styles(Dictionary<string, int> formats)
    {
        var numberFormats = new NumberingFormats();
        var cellFormats = new CellFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 });
        uint nextId = 164; // the first number a workbook's own formats may have
        foreach (var format in formats.OrderBy(pair => pair.Value).Select(pair => pair.Key))
        {
            if (format == "@wrap")
            {
                cellFormats.Append(new CellFormat(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top })
                    { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, ApplyAlignment = true });
                continue;
            }
            numberFormats.Append(new NumberingFormat { NumberFormatId = nextId, FormatCode = format });
            cellFormats.Append(new CellFormat { NumberFormatId = nextId, FontId = 0, FillId = 0, BorderId = 0, ApplyNumberFormat = true });
            nextId++;
        }
        numberFormats.Count = (uint)numberFormats.ChildElements.Count;
        cellFormats.Count = (uint)cellFormats.ChildElements.Count;

        var styles = new Stylesheet();
        if (numberFormats.HasChildren) styles.Append(numberFormats);
        styles.Append(
            new DocumentFormat.OpenXml.Spreadsheet.Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" })) { Count = 1 },
            // the two fills every workbook has to begin with
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
            new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 },
            new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
            cellFormats);
        return styles;
    }
}

/// <summary>Writes an OpenDocument spreadsheet (.ods), with numbers, dates and merged cells as such.</summary>
internal static class OdsWriter
{
    public static void Write(Workbook workbook, string output)
    {
        var sheets = workbook.UsedSheets.ToList();
        if (sheets.Count == 0) sheets = [workbook.Sheets.FirstOrDefault() ?? new Sheet("Sheet1")];
        var names = XlsxWriter.UniqueNames(sheets);

        using var package = new OdfPackage(output, "application/vnd.oasis.opendocument.spreadsheet");
        var automatic = new XElement(Odf.Office + "automatic-styles", DataStyles());
        var spreadsheet = new XElement(Odf.Office + "spreadsheet");

        for (int s = 0; s < sheets.Count; s++)
        {
            var sheet = sheets[s];
            var table = new XElement(Odf.Table + "table", new XAttribute(Odf.Table + "name", names[s]));

            var widths = sheet.FittedWidths();
            for (int c = 0; c < widths.Length; c++)
            {
                string style = $"co{s + 1}_{c + 1}";
                // a character of the usual font is about a twelfth of an inch wide
                automatic.Add(new XElement(Odf.Style + "style", new XAttribute(Odf.Style + "name", style), new XAttribute(Odf.Style + "family", "table-column"),
                    new XElement(Odf.Style + "table-column-properties", new XAttribute(Odf.Style + "column-width", OdfPackage.Length(widths[c] * 6.2 + 4)))));
                table.Add(new XElement(Odf.Table + "table-column", new XAttribute(Odf.Table + "style-name", style)));
            }
            if (widths.Length == 0) table.Add(new XElement(Odf.Table + "table-column"));

            var merges = sheet.Merges.ToDictionary(m => (m.Row, m.Column));
            var covered = new HashSet<(int, int)>();
            foreach (var merge in sheet.Merges)
                for (int r = 0; r < merge.RowSpan; r++)
                for (int c = 0; c < merge.ColumnSpan; c++)
                    if (r > 0 || c > 0) covered.Add((merge.Row + r, merge.Column + c));

            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                var row = new XElement(Odf.Table + "table-row");
                var cells = sheet.Rows[r];
                for (int c = 0; c < cells.Count; c++)
                {
                    if (covered.Contains((r, c)))
                    {
                        row.Add(new XElement(Odf.Table + "covered-table-cell"));
                        continue;
                    }
                    var element = ToCell(cells[c]);
                    if (merges.TryGetValue((r, c), out var merge))
                    {
                        element.Add(new XAttribute(Odf.Table + "number-columns-spanned", merge.ColumnSpan),
                            new XAttribute(Odf.Table + "number-rows-spanned", merge.RowSpan));
                    }
                    row.Add(element);
                }
                if (!row.HasElements) row.Add(new XElement(Odf.Table + "table-cell"));
                table.Add(row);
            }
            if (sheet.Rows.Count == 0) table.Add(new XElement(Odf.Table + "table-row", new XElement(Odf.Table + "table-cell")));
            spreadsheet.Add(table);
        }

        package.AddXml("content.xml", new XDocument(OdfPackage.Root("document-content", automatic, new XElement(Odf.Office + "body", spreadsheet))));
        package.AddXml("styles.xml", new XDocument(OdfPackage.Root("document-styles")));
        package.AddMeta(workbook.Title, null);
    }

    private static XElement ToCell(Cell cell)
    {
        var element = new XElement(Odf.Table + "table-cell");
        if (cell.IsEmpty) return element;

        switch (cell.Kind)
        {
            case CellKind.Number:
                bool percent = cell.Format?.Contains('%') == true;
                element.Add(new XAttribute(Odf.Office + "value-type", percent ? "percentage" : "float"),
                    new XAttribute(Odf.Office + "value", cell.Number.ToString("R", CultureInfo.InvariantCulture)));
                if (percent) element.Add(new XAttribute(Odf.Table + "style-name", "ce_percent"));
                break;
            case CellKind.Date:
                bool day = cell.Date.TimeOfDay == TimeSpan.Zero;
                element.Add(new XAttribute(Odf.Office + "value-type", "date"),
                    new XAttribute(Odf.Office + "date-value", cell.Date.ToString(day ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                    new XAttribute(Odf.Table + "style-name", day ? "ce_date" : "ce_datetime"));
                break;
            case CellKind.Boolean:
                element.Add(new XAttribute(Odf.Office + "value-type", "boolean"), new XAttribute(Odf.Office + "boolean-value", cell.Number != 0 ? "true" : "false"));
                break;
            default:
                element.Add(new XAttribute(Odf.Office + "value-type", "string"));
                break;
        }
        // what the cell shows, a paragraph per line
        foreach (var line in cell.Text.Split('\n'))
            element.Add(new XElement(Odf.Text + "p", OdtWriter.Spaced(line.TrimEnd('\r'))));
        return element;
    }

    /// <summary>How dates and percentages are shown; the cells that hold them name these.</summary>
    private static object[] DataStyles()
    {
        // "long": four digits for the year, two for everything else
        XElement Part(string name) => new(Odf.Number + name, new XAttribute(Odf.Number + "style", "long"));
        XElement Text(string text) => new(Odf.Number + "text", text);

        var date = new XElement(Odf.Number + "date-style", new XAttribute(Odf.Style + "name", "N_date"),
            Part("year"), Text("-"), Part("month"), Text("-"), Part("day"));
        var dateTime = new XElement(Odf.Number + "date-style", new XAttribute(Odf.Style + "name", "N_datetime"),
            Part("year"), Text("-"), Part("month"), Text("-"), Part("day"), Text(" "),
            Part("hours"), Text(":"), Part("minutes"), Text(":"), Part("seconds"));
        var percent = new XElement(Odf.Number + "percentage-style", new XAttribute(Odf.Style + "name", "N_percent"),
            new XElement(Odf.Number + "number", new XAttribute(Odf.Number + "decimal-places", 2), new XAttribute(Odf.Number + "min-decimal-places", 0),
                new XAttribute(Odf.Number + "min-integer-digits", 1)),
            Text("%"));

        XElement CellStyle(string name, string data) => new(Odf.Style + "style", new XAttribute(Odf.Style + "name", name),
            new XAttribute(Odf.Style + "family", "table-cell"), new XAttribute(Odf.Style + "data-style-name", data));

        return [date, dateTime, percent, CellStyle("ce_date", "N_date"), CellStyle("ce_datetime", "N_datetime"), CellStyle("ce_percent", "N_percent")];
    }
}

/// <summary>
/// Writes comma-separated text (.csv) as the standard describes it (RFC 4180), in UTF-8 with the
/// mark Excel needs to read accented letters right. A CSV file is one table: a workbook of
/// several sheets becomes a file per sheet.
/// </summary>
internal static class CsvWriter
{
    /// <returns>The file written, or the folder when the workbook had several sheets.</returns>
    public static string Write(Workbook workbook, string output, ConversionOptions options)
    {
        char separator = options.Detail.Text(K.Separator) switch
        {
            "semicolon" => ';',
            "tab" => '\t',
            _ => ','
        };

        var sheets = workbook.UsedSheets.ToList();
        if (sheets.Count <= 1)
        {
            WriteSheet(sheets.FirstOrDefault() ?? new Sheet("Sheet1"), output, separator);
            return output;
        }

        var folder = FrameExtraction.CreateOutputFolder(output, "sheets");
        try
        {
            var names = XlsxWriter.UniqueNames(sheets);
            for (int i = 0; i < sheets.Count; i++)
                WriteSheet(sheets[i], Path.Combine(folder, names[i] + ".csv"), separator);
            return folder;
        }
        catch
        {
            FrameExtraction.DeleteFolderQuiet(folder);
            throw;
        }
    }

    private static void WriteSheet(Sheet sheet, string path, char separator)
    {
        using var file = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16);
        int columns = sheet.ColumnCount;
        foreach (var row in sheet.Rows)
        {
            for (int c = 0; c < columns; c++)
            {
                if (c > 0) file.Write(separator);
                if (c < row.Count) file.Write(Field(row[c], separator));
            }
            file.Write("\r\n");
        }
    }

    /// <summary>A cell as a field: a number as programs read it back, a date unmistakably, text in quotes where it needs them.</summary>
    internal static string Field(Cell cell, char separator)
    {
        string text = cell.Kind switch
        {
            // a plain number is written so that it reads back as the same number; a formatted one (12.5%) as it shows
            CellKind.Number when cell.Format == null => separator == ';' || separator == '\t' ? cell.Text : Cell.General(cell.Number),
            _ => cell.Text
        };
        bool quote = text.Contains(separator) || text.Contains('"') || text.Contains('\n') || text.Contains('\r') ||
                     (text.Length > 0 && (text[0] == ' ' || text[^1] == ' '));
        return quote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}

/// <summary>Writes the sheets as a web page: one table each, in a file that needs nothing beside it.</summary>
internal static class SheetHtmlWriter
{
    private static readonly XNamespace Xhtml = XhtmlWriter.Xhtml;

    private const string Css = """
        body { font-family: "Segoe UI", Helvetica, Arial, sans-serif; margin: 1.5em; color: #1a1a1a; }
        h2 { font-size: 1.1em; margin: 1.5em 0 0.5em; }
        table { border-collapse: collapse; font-size: 0.9em; }
        td { border: 1px solid #b0b0b0; padding: 0.2em 0.6em; vertical-align: top; white-space: pre-wrap; }
        td.n { text-align: right; font-variant-numeric: tabular-nums; }
        """;

    public static void Write(Workbook workbook, string output, string fallbackTitle)
    {
        var sheets = workbook.UsedSheets.ToList();
        var names = XlsxWriter.UniqueNames(sheets);
        var body = new XElement(Xhtml + "body");

        for (int s = 0; s < sheets.Count; s++)
        {
            var sheet = sheets[s];
            if (sheets.Count > 1) body.Add(new XElement(Xhtml + "h2", names[s]));

            var merges = sheet.Merges.ToDictionary(m => (m.Row, m.Column));
            var covered = new HashSet<(int, int)>();
            foreach (var merge in sheet.Merges)
                for (int r = 0; r < merge.RowSpan; r++)
                for (int c = 0; c < merge.ColumnSpan; c++)
                    if (r > 0 || c > 0) covered.Add((merge.Row + r, merge.Column + c));

            int columns = sheet.ColumnCount;
            var rows = new XElement(Xhtml + "tbody");
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                var row = new XElement(Xhtml + "tr");
                for (int c = 0; c < columns; c++)
                {
                    if (covered.Contains((r, c))) continue;
                    var cell = sheet[r, c];
                    var element = new XElement(Xhtml + "td", XhtmlWriter.Clean(cell.Text));
                    if (cell.Kind is CellKind.Number or CellKind.Date) element.Add(new XAttribute("class", "n"));
                    if (merges.TryGetValue((r, c), out var merge))
                    {
                        if (merge.ColumnSpan > 1) element.Add(new XAttribute("colspan", merge.ColumnSpan));
                        if (merge.RowSpan > 1) element.Add(new XAttribute("rowspan", merge.RowSpan));
                    }
                    row.Add(element);
                }
                rows.Add(row);
            }
            body.Add(new XElement(Xhtml + "table", rows));
        }

        var html = new XElement(Xhtml + "html", new XAttribute("lang", "en"),
            new XElement(Xhtml + "head",
                new XElement(Xhtml + "meta", new XAttribute("charset", "utf-8")),
                new XElement(Xhtml + "meta", new XAttribute("name", "generator"), new XAttribute("content", AppInfo.DisplayName)),
                new XElement(Xhtml + "title", workbook.Title ?? fallbackTitle),
                new XElement(Xhtml + "style", "\n" + Css)),
            body);

        using var file = new StreamWriter(output, false, new UTF8Encoding(false));
        file.Write("<!DOCTYPE html>\n");
        file.Write(XhtmlWriter.Serialize(html, asXml: false));
        file.Write('\n');
    }
}

/// <summary>
/// Lays the sheets out as a PDF: a grid of cells on pages, numbers to the right, every column as
/// wide as its content. A sheet too wide for the paper is turned to landscape, and one too wide
/// even for that continues with its remaining columns on further pages — the way a spreadsheet
/// program prints it. This is the built-in way; Excel or LibreOffice, where installed, print the
/// workbook with its own fonts, colours and page setup instead.
/// </summary>
internal static class SheetPdfWriter
{
    public static void Write(Workbook workbook, string output, ConversionOptions options, string fallbackTitle)
    {
        var detail = options.Detail;
        var paper = MergeConversion.PaperSize(detail.Text(K.PageSize)) ?? PageSizes.A4;
        float margin = (float)(Math.Clamp(detail.Number(K.Margin) ?? 12, 0, 100) * 72.0 / 25.4);
        float fontSize = (float)Math.Clamp(detail.Number(K.FontSize) ?? 9, 4, 72);
        var sheets = workbook.UsedSheets.ToList();
        var names = XlsxWriter.UniqueNames(sheets);

        try
        {
            Generate(maxCellCharacters: int.MaxValue);
        }
        catch (QuestPDF.Drawing.Exceptions.DocumentLayoutException)
        {
            // a single cell with more text than a page holds cannot be divided: it is cut short
            Generate(maxCellCharacters: 1500);
        }

        void Generate(int maxCellCharacters) => QDocument.Create(container =>
        {
            if (sheets.Count == 0)
            {
                container.Page(page =>
                {
                    page.Size(paper);
                    page.Margin(margin);
                    page.Content().Text(" ");
                });
                return;
            }

            for (int s = 0; s < sheets.Count; s++)
            {
                var sheet = sheets[s];
                string? heading = sheets.Count > 1 ? names[s] : null;

                // Widths in points, from the longest line in each column: a character of this font is a little
                // over half its size wide. (A line that turns out wider than that is wrapped inside its cell.)
                var widths = sheet.LongestLines().Select(characters => (float)(Math.Clamp(characters, 2, 48) * fontSize * 0.55 + 8)).ToArray();
                float portrait = paper.Width - 2 * margin, landscape = paper.Height - 2 * margin;
                bool turn = widths.Sum() > portrait;
                float available = turn ? landscape : portrait;
                for (int c = 0; c < widths.Length; c++) widths[c] = Math.Min(widths[c], available);

                // the columns that go on a page side by side
                var groups = new List<(int From, int To)>();
                int first = 0;
                float used = 0;
                for (int c = 0; c < widths.Length; c++)
                {
                    if (c > first && used + widths[c] > available + 0.5f)
                    {
                        groups.Add((first, c));
                        first = c;
                        used = 0;
                    }
                    used += widths[c];
                }
                groups.Add((first, widths.Length));

                foreach (var (from, to) in groups)
                {
                    container.Page(page =>
                    {
                        page.Size(turn ? paper.Landscape() : paper.Portrait());
                        page.Margin(margin);
                        page.DefaultTextStyle(style => style.FontSize(fontSize).LineHeight(1.2f)
                            .FontFamily(["Lato", .. PlatformServices.Current.PdfFallbackFonts]));

                        if (heading != null)
                            page.Header().PaddingBottom(6).Text(groups.Count > 1 ? $"{heading}  ·  columns {XlsxWriter.Reference(0, from)[..^1]}–{XlsxWriter.Reference(0, to - 1)[..^1]}" : heading)
                                .FontSize(fontSize + 2).Bold();

                        page.Content().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                for (int c = from; c < to; c++) columns.ConstantColumn(widths[c]);
                            });

                            for (int r = 0; r < sheet.Rows.Count; r++)
                            {
                                for (int c = from; c < to; c++)
                                {
                                    var cell = sheet[r, c];
                                    var text = cell.Text.Replace("\t", "    ").Replace("\r", "");
                                    if (text.Length > maxCellCharacters) text = text[..maxCellCharacters] + "…";
                                    var place = table.Cell().Row((uint)(r + 1)).Column((uint)(c - from + 1))
                                        .Border(0.4f).BorderColor(Colors.Grey.Lighten1).PaddingHorizontal(3).PaddingVertical(1.5f);
                                    if (cell.Kind is CellKind.Number or CellKind.Date) place = place.AlignRight();
                                    place.Text(text.Length == 0 ? " " : text);
                                }
                            }
                        });

                        page.Footer().AlignCenter().Text(text =>
                        {
                            text.DefaultTextStyle(style => style.FontSize(7).FontColor(Colors.Grey.Medium));
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                    });
                }
            }
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = workbook.Title ?? fallbackTitle,
            Creator = $"{AppInfo.DisplayName} {AppInfo.Version}",
            Producer = $"{AppInfo.DisplayName} {AppInfo.Version}"
        })
        .GeneratePdf(output);
    }
}
