using FileConverter.Core;
using FileConverter.Core.Converters;
using FileConverter.Core.Settings;
using ImageMagick;
using QuestPDF.Fluent;
using UglyToad.PdfPig;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>
/// Files of several pages and files without pixels: a TIFF of several pages into a PDF and into
/// pictures, a PDF into one TIFF, and drawings (SVG) into pixels or — in a PDF — kept as drawings.
/// </summary>
public class PagesAndDrawingsTests(Samples samples) : IClassFixture<Samples>
{
    private string NewFile(string name) => Path.Combine(samples.NewOutputFolder(), name);

    /// <summary>Three pages of different sizes, as a scanner's software might save them.</summary>
    private string ThreePageTiff()
    {
        var path = Path.Combine(samples.Folder, "scan.tiff");
        if (File.Exists(path)) return path;
        using var pages = new MagickImageCollection();
        foreach (var (width, height) in new[] { (300u, 200u), (200u, 300u), (320u, 240u) })
            pages.Add(Samples.Picture(width, height));
        pages.Write(path, MagickFormat.Tiff);
        return path;
    }

    private string Drawing(string attributes = "width=\"200\" height=\"100\" viewBox=\"0 0 200 100\"")
    {
        var path = NewFile("drawing.svg");
        File.WriteAllText(path, $"""
            <svg xmlns="http://www.w3.org/2000/svg" {attributes}>
              <rect x="0" y="0" width="100" height="100" fill="#ff0000"/>
              <text x="110" y="60" font-family="sans-serif" font-size="20" fill="#000000">Çay</text>
            </svg>
            """);
        return path;
    }

    // ---------- a TIFF of several pages ----------

    [Fact]
    public async Task EveryPageOfATiffBecomesAPageOfThePdf()
    {
        var result = await samples.ConvertAsync(ThreePageTiff(), "pdf");

        using var pdf = PdfDocument.Open(result.OutputPath);
        Assert.Equal(3, pdf.NumberOfPages);
        // each page as large as its picture: 96 pixels to the inch
        Assert.Equal((225, 150), (Math.Round(pdf.GetPage(1).Width), Math.Round(pdf.GetPage(1).Height)));
        Assert.Equal((150, 225), (Math.Round(pdf.GetPage(2).Width), Math.Round(pdf.GetPage(2).Height)));
    }

    [Fact]
    public async Task EveryPageOfATiffBecomesAPictureOfItsOwn()
    {
        var result = await samples.ConvertAsync(ThreePageTiff(), "png");

        Assert.True(Directory.Exists(result.OutputPath));
        Assert.EndsWith("scan pages", result.OutputPath);
        var files = Directory.GetFiles(result.OutputPath, "*.png").Order().ToList();
        Assert.Equal(["scan 01.png", "scan 02.png", "scan 03.png"], files.Select(Path.GetFileName));
        // every page whole and of its own size — not what an animation's frames would be made into
        Assert.Equal((200u, 300u), (new MagickImageInfo(files[1]).Width, new MagickImageInfo(files[1]).Height));
    }

    [Fact]
    public async Task ATiffOfSeveralPagesAndAPhotoAreCombinedPageByPage()
    {
        var pdf = await samples.CombineAsync([ThreePageTiff(), samples.Jpg], "pdf");
        using var read = PdfDocument.Open(pdf);
        Assert.Equal(4, read.NumberOfPages);

        var smaller = await samples.CombineAsync([ThreePageTiff(), samples.Jpg], "pdf",
            new ConversionOptions { SizePercent = 50 });
        using var readSmaller = PdfDocument.Open(smaller);
        Assert.Equal(4, readSmaller.NumberOfPages);

        var tiff = await samples.CombineAsync([ThreePageTiff(), samples.Png], "tiff");
        Assert.Equal(4, MagickImageInfo.ReadCollection(tiff).Count());
    }

    // ---------- a PDF into one TIFF ----------

    private string TwoPagePdf()
    {
        var path = Path.Combine(samples.Folder, "letter-two-pages.pdf");
        if (File.Exists(path)) return path;
        using var photo = Samples.Picture();
        var bytes = photo.ToByteArray(MagickFormat.Png);
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A5);
                page.Margin(30);
                page.Content().Text("Only black text on white paper.");
            });
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A5);
                page.Margin(30);
                page.Content().Image(bytes);
            });
        }).GeneratePdf(path);
        return path;
    }

    [Fact]
    public async Task APdfBecomesOneTiffOfAsManyPages()
    {
        var result = await samples.ConvertAsync(TwoPagePdf(), "tiff", (K.Dpi, "100"));

        Assert.True(File.Exists(result.OutputPath));
        using var pages = new MagickImageCollection(result.OutputPath);
        Assert.Equal(2, pages.Count);
        // 419.5 × 595.3 points at 100 dots per inch
        Assert.Equal(583u, pages[0].Width);
        Assert.InRange(pages[0].Height, 826u, 827u);
        Assert.InRange(pages[0].Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 99.9, 100.1);
        // the page of text is stored without colour, the page with a photo with it; neither loses anything
        Assert.Equal(ColorSpace.Gray, pages[0].ColorSpace);
        Assert.NotEqual(ColorSpace.Gray, pages[1].ColorSpace);
        Assert.Contains(pages[0].Compression, new[] { CompressionMethod.LZW, CompressionMethod.Zip });
    }

    [Fact]
    public async Task OnlyTheChosenPagesGoIntoTheTiff()
    {
        var result = await samples.ConvertAsync(TwoPagePdf(), "tiff", (K.Pages, "2"), (K.Dpi, "72"), (K.Compression, "none"));

        using var pages = new MagickImageCollection(result.OutputPath);
        Assert.Single(pages);
        Assert.Equal(CompressionMethod.NoCompression, pages[0].Compression);
    }

    // ---------- drawings ----------

    [Fact]
    public async Task ADrawingIsDrawnAtPrintResolutionWithNothingBehindIt()
    {
        var result = await samples.ConvertAsync(Drawing(), "png");

        using var image = new MagickImage(result.OutputPath);
        // 200 × 100 units of 1/96 inch at 300 dots per inch
        Assert.Equal(625u, image.Width);
        Assert.InRange(image.Height, 312u, 313u);
        Assert.InRange(image.Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 299.9, 300.1);
        var pixels = image.GetPixels();
        Assert.Equal("#FF0000", pixels.GetPixel(150, 150).ToColor()!.ToHexString());   // the square, fully there
        Assert.Equal(0, pixels.GetPixel(600, 20).ToColor()!.A);                         // nothing: transparent
        // an edge drawn at this size is sharp: a few pixels either side of it are pure red, and pure nothing
        Assert.Equal("#FF0000", pixels.GetPixel(309, 150).ToColor()!.ToHexString());
        Assert.Equal(0, pixels.GetPixel(316, 20).ToColor()!.A);
    }

    [Theory]
    [InlineData("96", 200u)]
    [InlineData("600", 1250u)]
    public async Task ADrawingsResolutionCanBeChosen(string dpi, uint width)
    {
        var result = await samples.ConvertAsync(Drawing(), "png", (K.Dpi, dpi));
        Assert.Equal(width, new MagickImageInfo(result.OutputPath).Width);
    }

    [Fact]
    public async Task ADrawingBecomesAJpegOnWhiteAndCanBeHeldToAWidth()
    {
        var result = await samples.ConvertAsync(Drawing(), "jpg", (K.MaxWidth, "100"));

        using var image = new MagickImage(result.OutputPath);
        Assert.Equal((100u, 50u), (image.Width, image.Height));
        var corner = image.GetPixels().GetPixel(95, 5).ToColor()!;
        Assert.True(corner.R > 60000 && corner.G > 60000 && corner.B > 60000); // white where the drawing had nothing
    }

    [Fact]
    public async Task ADrawingStaysADrawingInAPdf()
    {
        var result = await samples.ConvertAsync(Drawing(), "pdf");
        Assert.Equal("vector", result.Note);

        using var pdf = PdfDocument.Open(result.OutputPath);
        var page = pdf.GetPage(1);
        // a page as large as the drawing: 200 × 100 units of 1/96 inch
        Assert.Equal((150, 75), (Math.Round(page.Width), Math.Round(page.Height)));
        Assert.Empty(page.GetImages());          // no picture of it …
        Assert.Contains("Çay", page.Text);       // … but its text as text
        Assert.NotEmpty(page.Paths);             // and its square as a shape
    }

    [Fact]
    public async Task ADrawingIsFittedOntoAChosenSheet()
    {
        var result = await samples.ConvertAsync(Drawing(), "pdf", (K.PageSize, "a4"), (K.Margin, "10"));

        using var pdf = PdfDocument.Open(result.OutputPath);
        var page = pdf.GetPage(1);
        Assert.Equal((842, 595), (Math.Round(page.Width), Math.Round(page.Height))); // a wide drawing: the sheet lies on its side
    }

    [Theory]
    [InlineData("width=\"200\" height=\"100\"", 150, 75)]
    [InlineData("width=\"2in\" height=\"25.4mm\"", 144, 72)]
    [InlineData("viewBox=\"0 0 400 200\"", 300, 150)]
    [InlineData("width=\"100\" viewBox=\"0 0 400 200\"", 75, 37.5)]
    [InlineData("width=\"100%\" height=\"100%\" viewBox=\"0,0,80,40\"", 60, 30)]
    [InlineData("", 225, 112.5)]
    public void ADrawingsSizeIsTheOneItSaysItHas(string attributes, double width, double height) =>
        Assert.Equal((width, height), DrawingPdf.NaturalSize($"<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" {attributes}/>"));

    [Fact]
    public async Task WhatIsNotADrawingIsSaidNotToBe()
    {
        var path = NewFile("not.svg");
        File.WriteAllText(path, "<html><body>no drawing here</body></html>");

        var error = await Assert.ThrowsAsync<FriendlyException>(() => samples.ConvertAsync(path, "pdf"));
        Assert.Contains("SVG", error.Message);
    }

    [Fact]
    public async Task ADrawingAmongPicturesGetsAPageOfItsOwnSize()
    {
        var pdf = await samples.CombineAsync([Drawing(), samples.Png], "pdf");

        using var read = PdfDocument.Open(pdf);
        Assert.Equal(2, read.NumberOfPages);
        // the drawing's page is as large as the drawing, although it was drawn at three times the pixels
        var sizes = read.GetPages().Select(page => (Math.Round(page.Width), Math.Round(page.Height))).ToList();
        Assert.Contains((150, 75), sizes);
        Assert.Contains((240, 180), sizes);
    }

    // ---------- drawings only LibreOffice reads ----------

    [Theory]
    [InlineData("pub", "Publisher")]
    [InlineData("odg", "OpenDocument drawings")]
    public async Task WithoutLibreOfficeAPublisherFileOrDrawingSaysWhatItNeeds(string extension, string named)
    {
        Assert.SkipWhen(ToolLocator.LibreOfficePath != null, "LibreOffice is installed on this machine.");
        var path = NewFile("flyer." + extension);
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 0]);

        var error = await Assert.ThrowsAsync<FriendlyException>(() => samples.ConvertAsync(path, "pdf"));
        Assert.Contains(named, error.Message);
        Assert.Contains("LibreOffice", error.Message);
    }

    // ---------- what is offered ----------

    [Fact]
    public void PagesAndDrawingsAreOfferedTheirConversionsAndTheirSettings()
    {
        Assert.Equal(["jpg", "png", "gif", "webp", "bmp", "tiff", "pdf", "zip", "7z"], FormatRegistry.GetTargets("svg", false));
        Assert.Equal(FileCategory.Image, FormatRegistry.GetCategory("svg"));
        Assert.Contains("tiff", FormatRegistry.GetTargets("pdf", false));
        Assert.DoesNotContain("tiff", FormatRegistry.GetTargets("docx", false));
        Assert.Equal(["pdf", "png", "jpg", "zip", "7z"], FormatRegistry.GetTargets("pub", false));
        Assert.Equal(["pdf", "png", "jpg", "zip", "7z"], FormatRegistry.GetTargets("odg", false));
        // a drawing is no source for a picture format that would have to be made of its own pixels
        Assert.DoesNotContain("svg", FormatRegistry.GetTargets("png", false));

        static List<string> Keys(string source, string target) => SettingCatalog.For(source, target).Select(d => d.Key).ToList();
        Assert.Equal(K.Dpi, Keys("svg", "png")[0]);
        Assert.DoesNotContain(K.Scale, Keys("svg", "png"));    // its size is its resolution
        Assert.DoesNotContain(K.Metadata, Keys("svg", "jpg")); // a drawing carries no camera data
        Assert.Equal([K.PageSize, K.Margin], Keys("svg", "pdf"));
        Assert.Contains(K.Compression, Keys("pdf", "tiff"));
        Assert.Contains(K.Pages, Keys("pdf", "tiff"));
        Assert.Empty(Keys("pub", "pdf"));
    }
}
