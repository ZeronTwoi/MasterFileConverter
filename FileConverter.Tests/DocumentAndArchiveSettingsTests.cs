using System.IO.Compression;
using FileConverter.Core;
using FileConverter.Core.Converters;
using ImageMagick;
using QuestPDF.Fluent;
using SharpCompress.Archives;
using SharpCompress.Readers;
using UglyToad.PdfPig;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>The precise settings for PDFs, text documents and archives, checked on what they produce.</summary>
public class DocumentAndArchiveSettingsTests(Samples samples) : IClassFixture<Samples>
{
    private string ThreePagePdf()
    {
        var path = Path.Combine(samples.Folder, "three-pages.pdf");
        if (File.Exists(path)) return path;
        Document.Create(container =>
        {
            for (int number = 1; number <= 3; number++)
            {
                int n = number;
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.A5); // 419.5 × 595.3 points
                    page.Margin(30);
                    page.Content().Text($"Page {n} of the sample document");
                });
            }
        }).GeneratePdf(path);
        return path;
    }

    private string TextFile()
    {
        var path = Path.Combine(samples.Folder, "notes.txt");
        File.WriteAllText(path, "Merhaba dünya\nSecond line of the note.\n");
        return path;
    }

    // ---------- PDF → pictures ----------

    [Fact]
    public async Task PdfPagesAreRenderedAtTheChosenResolution()
    {
        var at72 = await samples.ConvertAsync(ThreePagePdf(), "png", (K.Dpi, "72"), (K.Pages, "1"));
        var at144 = await samples.ConvertAsync(ThreePagePdf(), "png", (K.Dpi, "144"), (K.Pages, "1"));

        // one page selected → one file; an A5 page is 420 × 595 points, which is its size in pixels at 72 dpi
        var small = new MagickImageInfo(at72.OutputPath);
        var large = new MagickImageInfo(at144.OutputPath);
        Assert.InRange((int)small.Width, 419, 420);
        Assert.InRange((int)small.Height, 595, 596);
        Assert.InRange((int)large.Width, 838, 840);
    }

    [Fact]
    public async Task WithoutSettingsEveryPageIsRenderedAtPrintResolution()
    {
        var result = await samples.ConvertAsync(ThreePagePdf(), "jpg");
        var files = Directory.GetFiles(result.OutputPath, "*.jpg");

        Assert.Equal(3, files.Length);
        Assert.InRange((int)new MagickImageInfo(files[0]).Width, 1746, 1750); // 419.5 pt at 300 dpi
    }

    [Fact]
    public async Task OnlyTheChosenPagesAreRendered()
    {
        var result = await samples.ConvertAsync(ThreePagePdf(), "png", (K.Pages, "2-"), (K.Dpi, "72"));
        var names = Directory.GetFiles(result.OutputPath, "*.png").Select(Path.GetFileNameWithoutExtension).Order().ToList();

        // pages keep their own numbers
        Assert.Equal(["three-pages 02", "three-pages 03"], names);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("3-1")]
    [InlineData("9")]
    public async Task APageListThatMakesNoSenseIsReportedNotGuessed(string pages)
    {
        var error = await Assert.ThrowsAsync<FriendlyException>(() =>
            samples.ConvertAsync(ThreePagePdf(), "png", (K.Pages, pages)));
        Assert.Contains("page", error.Message);
    }

    [Theory]
    [InlineData(null, 5, "0,1,2,3,4")]
    [InlineData("1-3, 5", 5, "0,1,2,4")]
    [InlineData("4-", 6, "3,4,5")]
    [InlineData("-2", 6, "0,1")]
    [InlineData("2;2;1", 6, "0,1")]
    [InlineData("3-99", 4, "2,3")]
    public void PageListsAreRead(string? text, int pageCount, string expected) =>
        Assert.Equal(expected, string.Join(",", PdfPageExport.ParsePages(text, pageCount)));

    [Fact]
    public async Task PdfPagesCanAlsoBeLimitedInSizeAndQuality()
    {
        var result = await samples.ConvertAsync(ThreePagePdf(), "jpg", (K.Pages, "1"), (K.MaxWidth, "200"), (K.Quality, "50"));

        using var image = new MagickImage(result.OutputPath);
        Assert.Equal(200u, image.Width);
        Assert.Equal(50u, image.Quality);
    }

    [Fact]
    public async Task PageImagesRecordTheResolutionTheyWereDrawnAt()
    {
        var png = await samples.ConvertAsync(ThreePagePdf(), "png", (K.Dpi, "144"), (K.Pages, "1"));
        var jpg = await samples.ConvertAsync(ThreePagePdf(), "jpg", (K.Dpi, "144"), (K.Pages, "1"));

        using var first = new MagickImage(png.OutputPath);
        using var second = new MagickImage(jpg.OutputPath);
        Assert.InRange(first.Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 143.9, 144.1);
        Assert.InRange(second.Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 143.9, 144.1);
        // black text on white paper needs no colour channels
        Assert.Equal(ColorType.Grayscale, first.ColorType);
    }

    [Fact]
    public async Task AProgressivePageIsStillProgressive()
    {
        var result = await samples.ConvertAsync(ThreePagePdf(), "jpg", (K.Pages, "2"), (K.Dpi, "72"), (K.Progressive, "on"));

        using var image = new MagickImage(result.OutputPath);
        Assert.Equal(Interlace.Jpeg, image.Interlace);
        Assert.InRange((int)image.Width, 419, 420);
    }

    /// <summary>
    /// Long documents are shared out among copies of the app. This is what one such copy does with
    /// its share: draw the pages it was given, report each, and refuse what it cannot open.
    /// </summary>
    [Fact]
    public async Task AWorkerDrawsItsShareOfThePagesAndReportsThem()
    {
        var folder = samples.NewOutputFolder();
        var request = new PdfPageExport.WorkerRequest(ThreePagePdf(),
            new PdfPageExport.PageEncoding("png", PngLevel: 4, JpegQuality: 97, Chroma: "444", Progressive: false, Optimize: true),
            [
                new PdfPageExport.PageJob(0, 420, 595, 72, Path.Combine(folder, "first.png")),
                new PdfPageExport.PageJob(2, 210, 298, 36, Path.Combine(folder, "third.png"))
            ],
            EncodeThreads: 2);
        var json = System.Text.Json.JsonSerializer.Serialize(request, PdfPageJson.Default.WorkerRequest);

        var said = new StringWriter();
        Assert.Equal(0, await PdfPageExport.RunWorker(new StringReader(json), said));

        Assert.Equal(["page 0", "page 2"], said.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order());
        Assert.Equal(420u, new MagickImageInfo(Path.Combine(folder, "first.png")).Width);
        Assert.Equal(210u, new MagickImageInfo(Path.Combine(folder, "third.png")).Width);

        var refused = new StringWriter();
        var notAPdf = request with { Input = TextFile() };
        Assert.Equal(1, await PdfPageExport.RunWorker(
            new StringReader(System.Text.Json.JsonSerializer.Serialize(notAPdf, PdfPageJson.Default.WorkerRequest)), refused));
        Assert.StartsWith("refused ", refused.ToString());
    }

    // ---------- pictures and text → PDF ----------

    [Fact]
    public async Task PicturesFitTheirPagesUnlessAPaperSizeIsChosen()
    {
        static (double Width, double Height) FirstPage(string path)
        {
            using var pdf = PdfDocument.Open(path);
            var page = pdf.GetPage(1);
            return (Math.Round(page.Width), Math.Round(page.Height));
        }

        // 320 × 240 pixels at 96 dpi are 240 × 180 points
        var fitted = await samples.ConvertAsync(samples.Png, "pdf");
        Assert.Equal((240, 180), FirstPage(fitted.OutputPath));

        // a wide picture turns the sheet to landscape
        var a4 = await samples.ConvertAsync(samples.Png, "pdf", (K.PageSize, "a4"), (K.Margin, "10"));
        Assert.Equal((842, 595), FirstPage(a4.OutputPath));

        // a margin alone grows the page around the picture: 10 mm is 28.3 points on each side
        var margin = await samples.ConvertAsync(samples.Png, "pdf", (K.Margin, "10"));
        Assert.Equal((297, 237), FirstPage(margin.OutputPath));
    }

    [Fact]
    public async Task CombinedPicturesUseThePaperSizeToo()
    {
        var result = await samples.CombineAsync([samples.Png, samples.Jpg], "pdf", (K.PageSize, "letter"));

        using var pdf = PdfDocument.Open(result);
        Assert.Equal(2, pdf.NumberOfPages);
        Assert.Equal((792, 612), (Math.Round(pdf.GetPage(2).Width), Math.Round(pdf.GetPage(2).Height)));
    }

    [Fact]
    public async Task TextBecomesAPdfOnTheChosenPaper()
    {
        var a4 = await samples.ConvertAsync(TextFile(), "pdf");
        var letter = await samples.ConvertAsync(TextFile(), "pdf", (K.PageSize, "letter"), (K.Margin, "25"), (K.FontSize, "20"));

        using var first = PdfDocument.Open(a4.OutputPath);
        using var second = PdfDocument.Open(letter.OutputPath);
        Assert.Equal((595, 842), (Math.Round(first.GetPage(1).Width), Math.Round(first.GetPage(1).Height)));
        Assert.Equal((612, 792), (Math.Round(second.GetPage(1).Width), Math.Round(second.GetPage(1).Height)));
        Assert.Contains("Merhaba", second.GetPage(1).Text);

        // larger type: the first letter sits further from the left edge (bigger margin) and is taller
        var smallLetter = first.GetPage(1).Letters.First(l => l.Value == "M");
        var bigLetter = second.GetPage(1).Letters.First(l => l.Value == "M");
        Assert.True(bigLetter.PointSize > smallLetter.PointSize * 1.5);
        Assert.True(bigLetter.Location.X > smallLetter.Location.X + 15);
    }

    [Fact]
    public async Task TheBuiltInDocumentConverterCanBeChosen()
    {
        var docx = Path.Combine(samples.Folder, "letter.docx");
        if (!File.Exists(docx))
        {
            var made = await samples.ConvertAsync(TextFile(), "docx");
            File.Copy(made.OutputPath, docx);
        }

        var result = await samples.ConvertAsync(docx, "pdf", (K.Engine, "builtin"));
        using var pdf = PdfDocument.Open(result.OutputPath);
        Assert.Contains("Merhaba", pdf.GetPage(1).Text);

        if (ToolLocator.LibreOfficePath == null)
        {
            // an engine that was asked for by name and is missing is an error, not a silent substitute
            var error = await Assert.ThrowsAsync<FriendlyException>(() =>
                samples.ConvertAsync(docx, "pdf", (K.Engine, "libreoffice")));
            Assert.Contains("LibreOffice", error.Message);
        }
    }

    // ---------- archives ----------

    private string BigTextFile()
    {
        var path = Path.Combine(samples.Folder, "log.txt");
        if (!File.Exists(path))
            File.WriteAllLines(path, Enumerable.Range(0, 20_000).Select(i => $"line {i}: the quick brown fox jumps over the lazy dog"));
        return path;
    }

    [Fact]
    public async Task TheCompressionLevelDecidesTheZipSize()
    {
        var stored = await samples.ConvertAsync(BigTextFile(), "zip", (K.Level, "0"));
        var smallest = await samples.ConvertAsync(BigTextFile(), "zip", (K.Level, "9"));

        Assert.InRange(Samples.Size(stored.OutputPath), Samples.Size(BigTextFile()), Samples.Size(BigTextFile()) + 1000);
        Assert.True(Samples.Size(smallest.OutputPath) * 5 < Samples.Size(stored.OutputPath));
    }

    [Fact]
    public async Task ASevenZipArchiveHonoursLevelPasswordAndSolid()
    {
        Assert.SkipWhen(ToolLocator.SevenZipPath == null, "7-Zip is not installed on this machine.");

        var stored = await samples.ConvertAsync(BigTextFile(), "7z", (K.Level, "0"));
        var compressed = await samples.ConvertAsync(BigTextFile(), "7z", (K.Level, "9"), (K.Solid, "off"));
        Assert.True(Samples.Size(compressed.OutputPath) * 5 < Samples.Size(stored.OutputPath));

        var locked = await samples.ConvertAsync(BigTextFile(), "7z", (K.Password, "s3cret pass"));
        // the file names are encrypted as well, so without the password not even the listing opens
        Assert.ThrowsAny<Exception>(() =>
        {
            using var closed = ArchiveFactory.Open(locked.OutputPath);
            _ = closed.Entries.Count();
        });
        using var opened = ArchiveFactory.Open(locked.OutputPath, new ReaderOptions { Password = "s3cret pass" });
        var entry = opened.Entries.Single();
        Assert.Equal("log.txt", entry.Key);
        using var content = new StreamReader(entry.OpenEntryStream());
        Assert.StartsWith("line 0:", content.ReadLine());
    }

    [Fact]
    public async Task AZipCanBePasswordProtected()
    {
        Assert.SkipWhen(!ToolLocator.SevenZipWritesZip, "password-protected zips need a full 7-Zip, which is not installed here.");

        var locked = await samples.ConvertAsync(BigTextFile(), "zip", (K.Password, "s3cret"));
        using (var zip = ZipFile.OpenRead(locked.OutputPath))
            Assert.True(zip.Entries.Single().IsEncrypted);

        // several files into one protected zip
        var combined = await samples.CombineAsync([BigTextFile(), samples.Png], "zip", (K.Password, "s3cret"));
        using var both = ZipFile.OpenRead(combined);
        Assert.Equal(2, both.Entries.Count);
        Assert.All(both.Entries, e => Assert.True(e.IsEncrypted));
    }

    [Fact]
    public async Task APasswordIsRefusedClearlyWhereItCannotBeHonoured()
    {
        Assert.SkipWhen(ToolLocator.SevenZipWritesZip, "this machine has a full 7-Zip, so a protected zip can be made.");

        var error = await Assert.ThrowsAsync<FriendlyException>(() =>
            samples.ConvertAsync(BigTextFile(), "zip", (K.Password, "s3cret")));
        Assert.Contains("7-Zip", error.Message);
    }
}
