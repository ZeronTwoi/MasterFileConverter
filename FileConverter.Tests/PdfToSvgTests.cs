using System.Text;
using System.Text.RegularExpressions;
using FileConverter.Core;
using FileConverter.Core.Pdf;
using ImageMagick;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>
/// PDF → SVG. The measure of every case is the same: the SVG, drawn, has to look like the page
/// PDFium draws — and be a drawing, not a picture of one.
/// </summary>
public class PdfToSvgTests(Samples samples) : IClassFixture<Samples>
{
    private string NewFile(string name) => Path.Combine(samples.NewOutputFolder(), name);

    /// <summary>Text in two sizes and three styles, shapes with fills, outlines and dashes, a photo and a cut-out picture.</summary>
    private string MixedPage()
    {
        var path = Path.Combine(samples.Folder, "mixed.pdf");
        if (File.Exists(path)) return path;
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A5);
            page.Margin(30);
            page.DefaultTextStyle(style => style.FontSize(11));
            page.Content().Column(column =>
            {
                column.Spacing(10);
                column.Item().Text("Çay ve şeker").FontSize(24).Bold().FontColor(Colors.Blue.Darken2);
                column.Item().Text(text =>
                {
                    text.Span("Plain, ");
                    text.Span("bold, ").Bold();
                    text.Span("italic ").Italic();
                    text.Span("and underlined").Underline();
                    text.Span(" words: fi fl ffi — 12.5 % of 250 g.");
                });
                column.Item().Height(50).Row(row =>
                {
                    row.RelativeItem().Background(Colors.Red.Medium);
                    row.ConstantItem(10);
                    row.RelativeItem().Border(3).BorderColor(Colors.Green.Darken1);
                    row.ConstantItem(10);
                    row.RelativeItem().Background(Colors.Grey.Lighten2).Padding(6).Background(Colors.Orange.Medium);
                });
                column.Item().LineHorizontal(2).LineColor(Colors.Black);
                column.Item().Row(row =>
                {
                    row.ConstantItem(140).Image(samples.Jpg);
                    row.ConstantItem(12);
                    row.ConstantItem(140).Image(samples.TransparentPng);
                });
                column.Item().Rotate(-8).Text("slanted line of text").FontSize(14);
            });
        })).GeneratePdf(path);
        return path;
    }

    /// <summary>
    /// How far the two pictures are apart, 0 to 1: the root of the mean squared difference, after
    /// both were drawn at the same size on white and blurred a little — the two renderers do not
    /// smooth an edge the same way, which is not a difference anyone sees.
    /// </summary>
    private static double Difference(string svg, string pdf, int page = 0, double tolerance = 0)
    {
        using var renderer = new PdfRenderer(pdf);
        var (width, height) = renderer.GetPageSize(page);
        int pixelWidth = (int)Math.Round(width * 2), pixelHeight = (int)Math.Round(height * 2);

        using var expected = new MagickImage();
        using (var drawn = renderer.Render(page, pixelWidth, pixelHeight))
        {
            expected.ReadPixels(drawn.Bgra, 0, (uint)(pixelWidth * pixelHeight * 4),
                new PixelReadSettings((uint)pixelWidth, (uint)pixelHeight, StorageType.Char, PixelMapping.BGRA));
        }
        expected.Alpha(AlphaAction.Off);

        using var actual = new MagickImage(svg, new MagickReadSettings { Format = MagickFormat.Svg, Density = new Density(144), BackgroundColor = MagickColors.White });
        actual.BackgroundColor = MagickColors.White;
        actual.Alpha(AlphaAction.Remove);
        actual.Resize(new MagickGeometry((uint)pixelWidth, (uint)pixelHeight) { IgnoreAspectRatio = true });

        foreach (var image in new[] { expected, actual })
        {
            image.ColorSpace = ColorSpace.sRGB;
            image.Depth = 8;
            image.Blur(0, 1.2);
        }
        return expected.Compare(actual, ErrorMetric.RootMeanSquared);
    }

    // ---------- a drawing, and the same to look at ----------

    [Fact]
    public async Task APageBecomesADrawingThatLooksLikeThePage()
    {
        var result = await samples.ConvertAsync(MixedPage(), "svg");
        Assert.Equal("vector", result.Note);
        var svg = File.ReadAllText(result.OutputPath);

        // the page's size, in points (A5: 419.5 × 595.3)
        var size = Regex.Match(svg, "<svg [^>]*width=\"([0-9.]+)pt\" height=\"([0-9.]+)pt\" viewBox=\"0 0 ([0-9.]+) ([0-9.]+)\"");
        Assert.True(size.Success, svg[..Math.Min(svg.Length, 300)]);
        Assert.InRange(double.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 419, 420);
        Assert.InRange(double.Parse(size.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 595, 596);
        Assert.Equal(size.Groups[1].Value, size.Groups[3].Value);

        // letters as outlines: each shape written once, placed as often as it occurs
        int shapes = Regex.Matches(svg, "<path id=\"g").Count, placed = Regex.Matches(svg, "<use xlink:href=\"#g").Count;
        Assert.True(shapes > 20, $"{shapes} letter shapes");
        Assert.True(placed > shapes * 1.3, $"{placed} letters placed, of {shapes} shapes");
        Assert.DoesNotContain("<text", svg);

        // shapes as shapes, with their colours
        Assert.Contains("fill=\"#f44336\"", svg);     // the red block
        Assert.Contains("stroke=\"#000000\"", svg);   // the line

        // the two pictures as pictures, the cut-out one with what shows through it
        Assert.Equal(2, Regex.Matches(svg, "<image id=").Count);
        Assert.Contains("data:image/png;base64,", svg);

        Assert.InRange(Difference(result.OutputPath, MixedPage()), 0, 0.035);
    }

    [Fact]
    public async Task APhotoInAPdfGoesIntoTheDrawingAsTheVeryFileItIs()
    {
        // a JPEG put into a PDF by the app lies in it untouched
        var pdf = (await samples.ConvertAsync(samples.Jpg, "pdf")).OutputPath;
        var result = await samples.ConvertAsync(pdf, "svg");

        var data = Regex.Match(File.ReadAllText(result.OutputPath), "data:image/jpeg;base64,([A-Za-z0-9+/=]+)");
        Assert.True(data.Success, "the photo was not passed on as a JPEG");
        // not its pixels compressed a second time: the same bytes as the file that went in
        Assert.Equal(File.ReadAllBytes(samples.Jpg), Convert.FromBase64String(data.Groups[1].Value));
        Assert.InRange(Difference(result.OutputPath, pdf), 0, 0.02);
    }

    [Fact]
    public async Task AGradientIsDrawnAsAPictureInItsPlaceAndEverythingElseStaysADrawing()
    {
        var pdf = NewFile("gradient.pdf");
        Document.Create(container => container.Page(page =>
        {
            page.Size(new PageSize(300, 200));
            page.Margin(20);
            page.Content().Column(column =>
            {
                column.Spacing(10);
                column.Item().Height(80).BackgroundLinearGradient(0, [Colors.Blue.Medium, Colors.Yellow.Medium]);
                column.Item().Text("A caption under it").FontSize(14);
                column.Item().Height(20).Background(Colors.White).Border(1).BorderColor(Colors.Black);
            });
        })).GeneratePdf(pdf);

        var result = await samples.ConvertAsync(pdf, "svg");
        var svg = File.ReadAllText(result.OutputPath);

        // one picture — the gradient — and no more: the white box, which PDFium reports in the colour
        // it reports a pattern in, was looked at and found to be white
        Assert.Single(Regex.Matches(svg, "<image x="));
        Assert.Contains("<use xlink:href=\"#g", svg);
        Assert.InRange(Difference(result.OutputPath, pdf), 0, 0.03);
    }

    [Fact]
    public async Task EveryPageBecomesAFileAndTheChosenOnesOnly()
    {
        var pdf = NewFile("three.pdf");
        Document.Create(container =>
        {
            for (int number = 1; number <= 3; number++)
            {
                int n = number;
                container.Page(page =>
                {
                    page.Size(PageSizes.A6);
                    page.Margin(20);
                    page.Content().Text($"Page {n}").FontSize(30);
                });
            }
        }).GeneratePdf(pdf);

        var all = await samples.ConvertAsync(pdf, "svg");
        Assert.EndsWith("three pages", all.OutputPath);
        Assert.Equal(["three 01.svg", "three 02.svg", "three 03.svg"], Directory.GetFiles(all.OutputPath).Select(Path.GetFileName).Order());
        Assert.InRange(Difference(Path.Combine(all.OutputPath, "three 02.svg"), pdf, page: 1), 0, 0.03);

        var one = await samples.ConvertAsync(pdf, "svg", (K.Pages, "3"));
        Assert.True(File.Exists(one.OutputPath));
        Assert.InRange(Difference(one.OutputPath, pdf, page: 2), 0, 0.03);
    }

    // ---------- pages that are turned, or show only a part ----------

    /// <summary>
    /// A copy of the PDF whose first page says something more about itself. (The text is put into
    /// the page's description as it stands in the file; PDF readers find their way in a file whose
    /// table of contents no longer fits, which this makes of it.)
    /// </summary>
    private string WithPageEntry(string pdf, string entry, string name)
    {
        var bytes = File.ReadAllBytes(pdf);
        var text = Encoding.Latin1.GetString(bytes);
        int at = text.IndexOf("/Type /Page ", StringComparison.Ordinal);
        if (at < 0) at = text.IndexOf("/Type/Page", StringComparison.Ordinal);
        Assert.True(at >= 0, "the page's description was not found as text");
        var path = NewFile(name);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text.Insert(at, entry + " ")));
        return path;
    }

    /// <summary>A page whose every corner looks different, written by the app itself: a picture as a PDF.</summary>
    private async Task<string> PicturePage()
    {
        var path = Path.Combine(samples.Folder, "corners.pdf");
        if (File.Exists(path)) return path;
        var made = await samples.ConvertAsync(samples.Png, "pdf");
        File.Copy(made.OutputPath, path);
        return path;
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task APageThatIsTurnedIsDrawnTurned(int degrees)
    {
        var pdf = WithPageEntry(await PicturePage(), $"/Rotate {degrees}", $"turned-{degrees}.pdf");
        var result = await samples.ConvertAsync(pdf, "svg");

        // a quarter turn makes the page as wide as it was high: 240 × 180 points before
        var svg = File.ReadAllText(result.OutputPath);
        Assert.Contains(degrees == 180 ? "viewBox=\"0 0 240 180\"" : "viewBox=\"0 0 180 240\"", svg);
        Assert.InRange(Difference(result.OutputPath, pdf), 0, 0.02);
    }

    [Fact]
    public async Task APageThatShowsOnlyAPartOfItselfIsDrawnAsThatPart()
    {
        var pdf = WithPageEntry(await PicturePage(), "/CropBox [40 30 200 150]", "cropped.pdf");
        var result = await samples.ConvertAsync(pdf, "svg");

        Assert.Contains("viewBox=\"0 0 160 120\"", File.ReadAllText(result.OutputPath));
        Assert.InRange(Difference(result.OutputPath, pdf), 0, 0.02);
    }

    // ---------- what other programs write ----------

    [Fact]
    public async Task APdfWrittenByAnOfficeProgramBecomesADrawingToo()
    {
        Assert.SkipUnless(Core.Platform.PlatformServices.Current.OfficeAutomation?.CanConvert("docx", "pdf") == true ||
                          ToolLocator.LibreOfficePath != null, "No office program is installed on this machine.");

        // a document with a heading, a list, a table and a picture, laid out by Word or LibreOffice
        var document = new Core.Documents.RichDocument();
        document.Blocks.Add(new Core.Documents.Heading(1, [new Core.Documents.TextRun("Çay nasıl demlenir")]));
        document.Blocks.Add(new Core.Documents.Paragraph("Su kaynar, demlik ısınır, çay on dakika bekler."));
        var table = new Core.Documents.Table { HeaderRows = 1 };
        table.Rows.Add([new Core.Documents.TableCell([new Core.Documents.Paragraph("Çay")]), new Core.Documents.TableCell([new Core.Documents.Paragraph("Dakika")])]);
        table.Rows.Add([new Core.Documents.TableCell([new Core.Documents.Paragraph("Siyah")]), new Core.Documents.TableCell([new Core.Documents.Paragraph("10")])]);
        document.Blocks.Add(table);
        using (var image = Samples.Picture(160, 120))
            document.Blocks.Add(Core.Documents.Picture.Load(image.ToByteArray(MagickFormat.Png))!);
        var docx = NewFile("letter.docx");
        Core.Documents.DocxWriter.Write(document, docx);

        var pdf = (await samples.ConvertAsync(docx, "pdf")).OutputPath;
        var result = await samples.ConvertAsync(pdf, "svg");

        Assert.Contains("<use xlink:href=\"#g", File.ReadAllText(result.OutputPath));
        Assert.InRange(Difference(result.OutputPath, pdf), 0, 0.03);
    }

    // ---------- what is offered ----------

    [Fact]
    public void ADrawingIsOfferedForAPdfAndOnlyForAPdf()
    {
        Assert.Contains("svg", FormatRegistry.GetTargets("pdf", false));
        Assert.DoesNotContain("svg", FormatRegistry.GetTargets("docx", false));
        Assert.DoesNotContain("svg", FormatRegistry.GetTargets("png", false));
        Assert.Equal([K.Pages], Core.Settings.SettingCatalog.For("pdf", "svg").Select(d => d.Key));
    }
}
