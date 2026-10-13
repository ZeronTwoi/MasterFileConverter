using System.IO.Compression;
using System.Text;
using FileConverter.Core;
using FileConverter.Core.Settings;
using FileConverter.Core.Sheets;
using QuestPDF.Fluent;
using UglyToad.PdfPig;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>
/// Spreadsheets: what is read from each format, what is written to each, the tables found again
/// in a PDF, and the conversions between them as the app runs them — all without an office program.
/// </summary>
public class SpreadsheetTests(Samples samples) : IClassFixture<Samples>
{
    private static readonly (string Key, string Value) BuiltIn = (K.Engine, "builtin");

    private string NewFile(string name) => Path.Combine(samples.NewOutputFolder(), name);

    /// <summary>Text, numbers plain and formatted, a date, a date with a time, yes/no, a merged heading and a second sheet.</summary>
    private static Workbook Figures()
    {
        var workbook = new Workbook { Title = "Figures" };
        var sales = new Sheet("Sales 2026");
        sales.Rows.Add([Cell.OfText("Quarterly sales")]);
        sales.Rows.Add([Cell.OfText("Item"), Cell.OfText("Count"), Cell.OfText("Share"), Cell.OfText("When"), Cell.OfText("Paid")]);
        sales.Rows.Add([Cell.OfText("Çay, \"black\""), Cell.OfNumber(12.5), Cell.OfNumber(0.125, "12.50%", "0.00%"),
            Cell.OfDate(new DateTime(2026, 10, 13)), Cell.OfBoolean(true)]);
        sales.Rows.Add([Cell.OfText("Two\nlines"), Cell.OfNumber(-3), Cell.Empty, Cell.OfDate(new DateTime(2026, 1, 2, 15, 4, 5)), Cell.OfBoolean(false)]);
        sales.Merges.Add(new MergedRange(0, 0, 1, 5));
        workbook.Sheets.Add(sales);

        var notes = new Sheet("Notes");
        notes.Rows.Add([Cell.OfText("0012"), Cell.OfText("a code, not a number")]);
        workbook.Sheets.Add(notes);
        return workbook;
    }

    private static void AssertFigures(Workbook read)
    {
        Assert.Equal(["Sales 2026", "Notes"], read.Sheets.Select(sheet => sheet.Name));
        var sales = read.Sheets[0];
        Assert.Equal("Quarterly sales", sales[0, 0].Text);
        Assert.Equal("Çay, \"black\"", sales[2, 0].Text);
        Assert.Equal((CellKind.Number, 12.5), (sales[2, 1].Kind, sales[2, 1].Number));
        // a number with a format: the value, and what it shows
        Assert.Equal((CellKind.Number, 0.125), (sales[2, 2].Kind, sales[2, 2].Number));
        Assert.Equal("12.50%", sales[2, 2].Text);
        Assert.Equal((CellKind.Date, new DateTime(2026, 10, 13)), (sales[2, 3].Kind, sales[2, 3].Date));
        Assert.Equal((CellKind.Boolean, 1.0), (sales[2, 4].Kind, sales[2, 4].Number));
        Assert.Equal("Two\nlines", sales[3, 0].Text);
        Assert.Equal(-3, sales[3, 1].Number);
        Assert.True(sales[3, 2].IsEmpty);
        Assert.Equal(new DateTime(2026, 1, 2, 15, 4, 5), sales[3, 3].Date);
        Assert.Equal(new MergedRange(0, 0, 1, 5), Assert.Single(sales.Merges));
        // text that looks like a number is still text
        Assert.Equal((CellKind.Text, "0012"), (read.Sheets[1][0, 0].Kind, read.Sheets[1][0, 0].Text));
    }

    // ---------- Excel and OpenDocument ----------

    [Fact]
    public void AnExcelWorkbookKeepsValuesFormatsDatesAndMergedCells()
    {
        var path = NewFile("figures.xlsx");
        XlsxWriter.Write(Figures(), path);

        AssertFigures(ExcelReader.ReadFile(path));
        // what a validator would say of the file
        using var file = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(path, false);
        var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(file, TestContext.Current.CancellationToken).Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Take(5)));
    }

    [Fact]
    public void AnOpenDocumentSpreadsheetKeepsThemToo()
    {
        var path = NewFile("figures.ods");
        OdsWriter.Write(Figures(), path);

        AssertFigures(OdsReader.ReadFile(path));
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal(zip.Entries[0].Length, zip.Entries[0].CompressedLength);
    }

    [Fact]
    public void TheMillionEmptyRowsASpreadsheetEndsWithAreNotMadeReal()
    {
        // what LibreOffice writes below and beside the used cells: one row, one cell, "repeated"
        var path = NewFile("sparse.ods");
        using (var package = new FileConverter.Core.Documents.OdfPackage(path, "application/vnd.oasis.opendocument.spreadsheet"))
        {
            package.Add("content.xml", Encoding.UTF8.GetBytes("""
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                    xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
                  <office:body><office:spreadsheet><table:table table:name="Data">
                    <table:table-row>
                      <table:table-cell office:value-type="string"><text:p>first</text:p></table:table-cell>
                      <table:table-cell table:number-columns-repeated="3"/>
                      <table:table-cell office:value-type="float" office:value="7"><text:p>7</text:p></table:table-cell>
                      <table:table-cell table:number-columns-repeated="16379"/>
                    </table:table-row>
                    <table:table-row table:number-rows-repeated="4"><table:table-cell table:number-columns-repeated="16384"/></table:table-row>
                    <table:table-row>
                      <table:table-cell office:value-type="string"><text:p>last</text:p></table:table-cell>
                      <table:table-cell table:number-columns-repeated="16383"/>
                    </table:table-row>
                    <table:table-row table:number-rows-repeated="1048570"><table:table-cell table:number-columns-repeated="16384"/></table:table-row>
                  </table:table></office:spreadsheet></office:body>
                </office:document-content>
                """), "text/xml");
        }

        var sheet = OdsReader.ReadFile(path).Sheets.Single();
        Assert.Equal(6, sheet.Rows.Count);
        Assert.Equal(5, sheet.ColumnCount);
        Assert.Equal(7, sheet[0, 4].Number);
        Assert.Equal("last", sheet[5, 0].Text);
        Assert.True(sheet[3, 0].IsEmpty);
    }

    [Theory]
    [InlineData("A plain name", "A plain name")]
    [InlineData("Q1/Q2: [draft]?", "Q1 Q2   draft")]
    [InlineData("A name that is far too long for a sheet", "A name that is far too long for")]
    [InlineData("  ", "Sheet1")]
    public void SheetNamesAreMadeIntoNamesExcelTakes(string name, string expected) =>
        Assert.Equal(expected, CsvReader.SheetName(name));

    [Fact]
    public void TwoSheetsOfTheSameNameAreToldApart()
    {
        var names = XlsxWriter.UniqueNames([new Sheet("Data"), new Sheet("data"), new Sheet("Data")]);
        Assert.Equal(["Data", "data (2)", "Data (3)"], names);
    }

    [Theory]
    [InlineData(0, 0, "A1")]
    [InlineData(6, 25, "Z7")]
    [InlineData(0, 26, "AA1")]
    [InlineData(99, 701, "ZZ100")]
    [InlineData(0, 702, "AAA1")]
    public void CellsAreNamedAsSpreadsheetsNameThem(int row, int column, string expected) =>
        Assert.Equal(expected, XlsxWriter.Reference(row, column));

    // ---------- CSV ----------

    [Theory]
    [InlineData("a,b,c\n1,2,3\n", ',')]
    [InlineData("a;b;c\n1,5;2;3\n", ';')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("name;note\n\"Smith, J\";\"x, y, z\"\n", ';')]
    [InlineData("just one column\nof text, with a comma here\nand nothing else\n", ',')]
    public void TheSeparatorIsWorkedOutFromTheFile(string text, char expected) =>
        Assert.Equal(expected, CsvReader.FindSeparator(text));

    [Fact]
    public void CsvIsReadWithQuotesLineBreaksAndNumbers()
    {
        var path = NewFile("data.csv");
        File.WriteAllText(path, "name,amount,code,note\r\n\"Smith, \"\"Jo\"\"\",12.5,007,\"two\nlines\"\r\nTotal,-3,,\"42\"\r\n");

        var sheet = CsvReader.ReadFile(path).Sheets.Single();
        Assert.Equal("data", sheet.Name);
        Assert.Equal("Smith, \"Jo\"", sheet[1, 0].Text);
        Assert.Equal((CellKind.Number, 12.5), (sheet[1, 1].Kind, sheet[1, 1].Number));
        Assert.Equal((CellKind.Text, "007"), (sheet[1, 2].Kind, sheet[1, 2].Text)); // leading zeros: a code
        Assert.Equal("two\nlines", sheet[1, 3].Text);
        Assert.Equal(-3, sheet[2, 1].Number);
        Assert.Equal(CellKind.Text, sheet[2, 3].Kind); // in quotes: meant as text
        Assert.Equal(3, sheet.Rows.Count);
    }

    [Fact]
    public void WhereSemicolonsSeparateACommaIsTheDecimalPoint()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = NewFile("turkish.csv");
        // as Excel saves it in Türkiye: semicolons, decimal commas, the older code page
        File.WriteAllBytes(path, Encoding.GetEncoding(1254).GetBytes("Ürün;Fiyat\r\nÇay;12,5\r\nŞeker;1.250,00\r\n"));

        var sheet = CsvReader.ReadFile(path).Sheets.Single();
        // (the letters are right when the system's own older code page is 1254; the numbers always)
        Assert.Equal((CellKind.Number, 12.5), (sheet[1, 1].Kind, sheet[1, 1].Number));
        Assert.Equal(CellKind.Text, sheet[2, 1].Kind); // grouped thousands are left as they are written
        Assert.Equal(2, sheet.ColumnCount);
    }

    [Fact]
    public void CsvIsWrittenAsTheStandardSaysAndAsExcelReadsIt()
    {
        var path = NewFile("out.csv");
        var workbook = new Workbook();
        var sheet = new Sheet("One");
        sheet.Rows.Add([Cell.OfText("plain"), Cell.OfText("with, comma"), Cell.OfText("with \"quotes\""), Cell.OfText("two\nlines")]);
        sheet.Rows.Add([Cell.OfNumber(1234.5), Cell.OfNumber(0.125, "12.50%", "0.00%"), Cell.OfDate(new DateTime(2026, 10, 13)), Cell.OfBoolean(true)]);
        sheet.Rows.Add([Cell.OfText("short row")]);
        workbook.Sheets.Add(sheet);

        Assert.Equal(path, CsvWriter.Write(workbook, path, new ConversionOptions()));
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // the mark by which Excel knows the file is UTF-8
        Assert.Equal(
            "plain,\"with, comma\",\"with \"\"quotes\"\"\",\"two\nlines\"\r\n" +
            "1234.5,12.50%,2026-10-13,TRUE\r\n" +
            "short row,,,\r\n",
            Encoding.UTF8.GetString(bytes[3..]));

        // with semicolons a comma needs no quotes
        var options = new ConversionOptions().WithDetail(Samples.Settings((K.Separator, "semicolon")));
        CsvWriter.Write(workbook, path, options);
        Assert.StartsWith("plain;with, comma;", Encoding.UTF8.GetString(File.ReadAllBytes(path)[3..]));
    }

    // ---------- tables out of a PDF ----------

    private string InvoicePdf(bool commaDecimals)
    {
        var path = Path.Combine(samples.Folder, commaDecimals ? "fatura.pdf" : "invoice.pdf");
        if (File.Exists(path)) return path;

        (string Item, string Quantity, string Price, string Total)[] lines = commaDecimals
            ? [("Çay bardağı", "12", "37,50", "450,00"), ("Demlik, çelik", "1", "1.250,00", "1.250,00"), ("Kargo", "1", "89,90", "89,90")]
            : [("Tea glass", "12", "37.50", "450.00"), ("Teapot, steel", "1", "1,250.00", "1,250.00"), ("Shipping", "1", "89.90", "89.90")];

        Document.Create(container => container.Page(page =>
        {
            page.Size(QuestPDF.Helpers.PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(style => style.FontSize(10));
            page.Content().Column(column =>
            {
                column.Spacing(10);
                column.Item().Text(commaDecimals ? "Fatura No 2026-0042" : "Invoice No 2026-0042").FontSize(16).Bold();
                column.Item().Text("Thank you for your order. The goods listed below were sent on the day the payment arrived.");
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(4);
                        columns.RelativeColumn(1);
                        columns.RelativeColumn(2);
                        columns.RelativeColumn(2);
                    });
                    foreach (var heading in new[] { "Item", "Qty", "Price", "Total" })
                        table.Cell().BorderBottom(1).Padding(3).Text(heading).Bold();
                    foreach (var line in lines)
                    {
                        table.Cell().Padding(3).Text(line.Item);
                        table.Cell().Padding(3).AlignRight().Text(line.Quantity);
                        table.Cell().Padding(3).AlignRight().Text(line.Price);
                        table.Cell().Padding(3).AlignRight().Text(line.Total);
                    }
                });
            });
        })).GeneratePdf(path);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATableInAPdfBecomesRowsAndColumnsAgain(bool commaDecimals)
    {
        var sheet = PdfTableReader.ReadFile(InvoicePdf(commaDecimals), sheetPerPage: true, TestContext.Current.CancellationToken).Sheets.Single();
        Assert.Equal("Page 1", sheet.Name);

        // what is not a table stands in the first column, a line per row
        Assert.EndsWith("No 2026-0042", sheet[0, 0].Text);
        Assert.StartsWith("Thank you for your order.", sheet[1, 0].Text);
        Assert.Single(sheet.Rows[1]);

        int header = sheet.Rows.FindIndex(row => row.Count > 0 && row[0].Text == "Item");
        Assert.True(header > 1);
        Assert.Equal(["Item", "Qty", "Price", "Total"], sheet.Rows[header].Select(cell => cell.Text));

        // every cell in its column, the numbers as numbers — also the one with a thousands mark, and
        // the item whose name has a comma in it
        var teapot = sheet.Rows[header + 2];
        Assert.Equal(commaDecimals ? "Demlik, çelik" : "Teapot, steel", teapot[0].Text);
        Assert.Equal((CellKind.Number, 1.0), (teapot[1].Kind, teapot[1].Number));
        Assert.Equal((CellKind.Number, 1250.0), (teapot[2].Kind, teapot[2].Number));
        Assert.Equal(37.5, sheet.Rows[header + 1][2].Number);
        Assert.Equal(89.9, sheet.Rows[header + 3][3].Number);
        Assert.Equal(4, sheet.ColumnCount);
    }

    [Theory]
    [InlineData("1,234.50", "12.5", PdfTableReader.Notation.Point, 1234.5)]
    [InlineData("1.234,50", "12,5", PdfTableReader.Notation.Comma, 1234.5)]
    [InlineData("1.234", "12,50", PdfTableReader.Notation.Comma, 1234.0)]
    [InlineData("1.234", "12.50", PdfTableReader.Notation.Point, 1.234)]
    public void HowADocumentWritesItsNumbersIsReadFromTheDocument(string cell, string other, object expected, double value)
    {
        var style = PdfTableReader.NumberStyle([cell, other, "some text", "2026"]);
        Assert.Equal((PdfTableReader.Notation)expected, style);
        Assert.Equal(value, PdfTableReader.ToCell(cell, style).Number);
    }

    [Theory]
    [InlineData("1.234")]      // twelve hundred, or one and a bit
    [InlineData("1,234")]
    [InlineData("12.50 TL")]
    [InlineData("0012")]
    [InlineData("13.10.2026")]
    [InlineData("1,2,3")]
    public void WhatCouldBeReadTwoWaysStaysText(string cell)
    {
        // nothing else in the document says how it writes numbers
        Assert.Equal(CellKind.Text, PdfTableReader.ToCell(cell, PdfTableReader.NumberStyle([cell, "words"])).Kind);
        // and what is no number is none whatever the document's way
        if (cell is "12.50 TL" or "0012" or "13.10.2026" or "1,2,3")
            Assert.Equal(CellKind.Text, PdfTableReader.ToCell(cell, PdfTableReader.Notation.Point).Kind);
    }

    [Fact]
    public void WholeNumbersAreNumbersWhateverElseIsThere() =>
        Assert.Equal((CellKind.Number, 2026.0), (PdfTableReader.ToCell("2026", PdfTableReader.Notation.Unknown).Kind, PdfTableReader.ToCell("2026", PdfTableReader.Notation.Unknown).Number));

    // ---------- conversions as the app runs them ----------

    private string FiguresFile(string extension)
    {
        var path = Path.Combine(samples.Folder, "figures." + extension);
        if (File.Exists(path)) return path;
        if (extension == "xlsx") XlsxWriter.Write(Figures(), path);
        else OdsWriter.Write(Figures(), path);
        return path;
    }

    [Theory]
    [InlineData("xlsx", "ods")]
    [InlineData("ods", "xlsx")]
    public async Task SpreadsheetsAreConvertedIntoOneAnotherWithoutAnOfficeProgram(string from, string to)
    {
        var result = await samples.ConvertAsync(FiguresFile(from), to, BuiltIn);
        AssertFigures(to == "xlsx" ? ExcelReader.ReadFile(result.OutputPath) : OdsReader.ReadFile(result.OutputPath));
    }

    [Fact]
    public async Task AWorkbookOfSeveralSheetsBecomesACsvFileForEach()
    {
        var result = await samples.ConvertAsync(FiguresFile("xlsx"), "csv");

        Assert.True(Directory.Exists(result.OutputPath));
        Assert.EndsWith("figures sheets", result.OutputPath);
        Assert.Equal(["Notes.csv", "Sales 2026.csv"], Directory.GetFiles(result.OutputPath).Select(Path.GetFileName).Order());
        Assert.Contains("\"Çay, \"\"black\"\"\",12.5,12.50%,2026-10-13,TRUE", File.ReadAllText(Path.Combine(result.OutputPath, "Sales 2026.csv")));
    }

    [Fact]
    public async Task CsvBecomesAWorkbookWithNumbersAsNumbers()
    {
        var csv = NewFile("prices.csv");
        File.WriteAllText(csv, "Ürün;Fiyat\r\nÇay;12,5\r\n", new UTF8Encoding(true));

        var result = await samples.ConvertAsync(csv, "xlsx");
        var sheet = ExcelReader.ReadFile(result.OutputPath).Sheets.Single();
        Assert.Equal("prices", sheet.Name);
        Assert.Equal("Çay", sheet[1, 0].Text);
        Assert.Equal((CellKind.Number, 12.5), (sheet[1, 1].Kind, sheet[1, 1].Number));
    }

    [Fact]
    public async Task ASheetBecomesAPdfWithEveryCellOnIt()
    {
        var result = await samples.ConvertAsync(FiguresFile("xlsx"), "pdf", BuiltIn);

        using var pdf = PdfDocument.Open(result.OutputPath);
        Assert.Equal(2, pdf.NumberOfPages); // a sheet starts on a new page
        var first = pdf.GetPage(1);
        Assert.Equal((595, 842), (Math.Round(first.Width), Math.Round(first.Height)));
        foreach (var expected in new[] { "Sales 2026", "Quarterly sales", "12.5", "12.50%", "2026-10-13", "TRUE" })
            Assert.Contains(expected, string.Join(" ", first.GetWords().Select(w => w.Text)));
        Assert.Contains("0012", pdf.GetPage(2).Text);

        // a number stands to the right of its column, text to the left
        var words = first.GetWords().ToList();
        var count = words.First(w => w.Text == "Count").BoundingBox;
        var twelve = words.First(w => w.Text == "12.5").BoundingBox;
        Assert.True(twelve.Left > count.Left);
    }

    [Fact]
    public async Task ASheetTooWideForThePaperIsTurnedAndThenContinuedOnFurtherPages()
    {
        string Wide(int columns)
        {
            var csv = NewFile($"wide-{columns}.csv");
            File.WriteAllLines(csv,
            [
                string.Join(",", Enumerable.Range(1, columns).Select(i => $"Column {i:D2}")),
                string.Join(",", Enumerable.Range(1, columns).Select(i => $"value {i:D2}"))
            ]);
            return csv;
        }

        using (var turned = PdfDocument.Open((await samples.ConvertAsync(Wide(14), "pdf")).OutputPath))
        {
            Assert.Equal(1, turned.NumberOfPages);
            Assert.True(turned.GetPage(1).Width > turned.GetPage(1).Height);
        }

        using var continued = PdfDocument.Open((await samples.ConvertAsync(Wide(40), "pdf")).OutputPath);
        Assert.True(continued.NumberOfPages > 1);
        var all = string.Join(" ", continued.GetPages().Select(page => page.Text));
        Assert.All(Enumerable.Range(1, 40), i => Assert.Contains($"Column {i:D2}", all));
        // no column on two pages
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(all, "Column 20"));
    }

    [Fact]
    public async Task APdfsTablesBecomeAWorkbookASheetPerPageOrAllOnOne()
    {
        var perPage = ExcelReader.ReadFile((await samples.ConvertAsync(InvoicePdf(false), "xlsx")).OutputPath);
        Assert.Equal("Page 1", perPage.Sheets.Single().Name);
        Assert.Contains(perPage.Sheets[0].Rows, row => row.Count == 4 && row[0].Text == "Shipping" && row[3].Number == 89.9);

        var one = ExcelReader.ReadFile((await samples.ConvertAsync(InvoicePdf(false), "xlsx", (K.Sheets, "one"))).OutputPath);
        Assert.Equal("Tables", one.Sheets.Single().Name);

        // a CSV file is one table: one file, unless a file per page is asked for
        var csv = await samples.ConvertAsync(InvoicePdf(true), "csv", (K.Separator, "semicolon"));
        Assert.True(File.Exists(csv.OutputPath));
        Assert.Contains("Demlik, çelik;1;1.250,00;1.250,00", File.ReadAllText(csv.OutputPath));
    }

    [Fact]
    public async Task ASheetBecomesAWebPageWithItsMergedCells()
    {
        var html = File.ReadAllText((await samples.ConvertAsync(FiguresFile("ods"), "html")).OutputPath);
        Assert.Contains("<h2>Sales 2026</h2>", html);
        Assert.Contains("<td colspan=\"5\">Quarterly sales</td>", html);
        Assert.Contains("<td class=\"n\">12.50%</td>", html);
        Assert.Contains("<h2>Notes</h2>", html);
    }

    // ---------- what is offered ----------

    [Fact]
    public void SpreadsheetsAreOfferedTheirConversionsAndTheirSettings()
    {
        Assert.Equal(["pdf", "ods", "csv", "html", "png", "jpg", "zip", "7z"], FormatRegistry.GetTargets("xlsx", false));
        Assert.Equal(["pdf", "xlsx", "ods", "html", "png", "jpg", "zip", "7z"], FormatRegistry.GetTargets("csv", false));
        Assert.Contains("xlsx", FormatRegistry.GetTargets("xls", false));
        Assert.DoesNotContain("xls", FormatRegistry.GetTargets("xlsx", false)); // the old format is read, not written
        Assert.Contains("xlsx", FormatRegistry.GetTargets("pdf", false));

        static List<string> Keys(string source, string target) => SettingCatalog.For(source, target).Select(d => d.Key).ToList();
        Assert.Equal([K.Engine], Keys("xlsx", "pdf"));
        Assert.Equal([K.PageSize, K.Margin, K.FontSize], Keys("csv", "pdf"));
        Assert.Equal([K.Separator], Keys("xlsx", "csv"));
        Assert.Equal([K.Sheets], Keys("pdf", "xlsx"));
        Assert.Equal([K.Sheets, K.Separator], Keys("pdf", "csv"));
        Assert.Equal([K.Engine], Keys("ods", "xlsx"));
        Assert.Empty(Keys("csv", "xlsx"));
    }
}
