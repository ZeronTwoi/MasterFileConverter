using FileConverter.Core.Pdf;
using ImageMagick;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;
using Xunit;
using ColorSpace = UglyToad.PdfPig.Graphics.Colors.ColorSpace;

namespace FileConverter.Tests;

/// <summary>
/// Pictures → PDF. The pictures are put into the document as the data they already are, so the
/// checks are of two kinds: the data really is untouched, and a PDF reader really shows the picture.
/// </summary>
public class ImagePdfTests(Samples samples) : IClassFixture<Samples>
{
    private string Source(string name, MagickFormat format, Action<MagickImage> prepare)
    {
        var path = Path.Combine(samples.Folder, name);
        if (File.Exists(path)) return path;
        using var image = Samples.Picture();
        prepare(image);
        image.Write(path, format);
        return path;
    }

    /// <summary>The first page as PDFium draws it, at the given size in pixels.</summary>
    private static MagickImage Drawn(string pdf, int width, int height)
    {
        using var renderer = new PdfRenderer(pdf);
        using var page = renderer.Render(0, width, height);
        var image = new MagickImage();
        image.ReadPixels(page.Bgra, 0, (uint)(width * height * 4),
            new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaAction.Off);
        return image;
    }

    private static IPdfImage OnlyImage(PdfDocument pdf) => pdf.GetPage(1).GetImages().Single();

    /// <summary>
    /// A JPEG that says how it has to be turned (EXIF orientation 1–8). ImageMagick writes the
    /// tag from the picture's own orientation, so both are set.
    /// </summary>
    private string Turned(int orientation) => Source($"turned-{orientation}.jpg", MagickFormat.Jpeg, image =>
    {
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)orientation);
        image.SetProfile(exif);
        image.Orientation = (Orientation)orientation;
        image.Quality = 92;
    });

    private static string Name(IPdfImage image, string key) =>
        image.ImageDictionary.TryGet(NameToken.Create(key), out var token) ? token.ToString() ?? "" : "";

    [Fact]
    public async Task AJpegGoesInByteForByte()
    {
        var result = await samples.ConvertAsync(samples.TaggedJpg, "pdf");

        using var pdf = PdfDocument.Open(result.OutputPath);
        var image = OnlyImage(pdf);
        Assert.Equal(File.ReadAllBytes(samples.TaggedJpg), image.RawBytes.ToArray());
        Assert.Contains("DCTDecode", Name(image, "Filter"));

        using var expected = new MagickImage(samples.TaggedJpg);
        using var drawn = Drawn(result.OutputPath, 320, 240);
        Assert.InRange(expected.Compare(drawn, ErrorMetric.RootMeanSquared), 0, 0.02);
    }

    [Fact]
    public async Task APngGoesInWithoutBeingCompressedAgain()
    {
        var result = await samples.ConvertAsync(samples.Png, "pdf");

        // the same compressed rows as in the file, plus a few hundred bytes of PDF around them
        Assert.InRange(Samples.Size(result.OutputPath), Samples.Size(samples.Png) - 200, Samples.Size(samples.Png) + 1500);

        using var pdf = PdfDocument.Open(result.OutputPath);
        Assert.True(OnlyImage(pdf).TryGetPng(out var png), "the reader could not unpack the stored picture");
        using var expected = new MagickImage(samples.Png);
        using var stored = new MagickImage(png!);
        Assert.Equal(0, expected.Compare(stored, ErrorMetric.RootMeanSquared));

        using var drawn = Drawn(result.OutputPath, 320, 240);
        Assert.InRange(expected.Compare(drawn, ErrorMetric.RootMeanSquared), 0, 0.01);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task APhotoStoredTurnedIsShownUprightWithoutBeingReEncoded(int orientation)
    {
        var source = Turned(orientation);
        Assert.Equal((Orientation)orientation, new MagickImageInfo(source).Orientation);
        var result = await samples.ConvertAsync(source, "pdf");

        using var upright = new MagickImage(source);
        upright.AutoOrient();
        // 5 to 8 lie on their side: upright they are taller than wide
        Assert.Equal(orientation >= 5 ? (240u, 320u) : (320u, 240u), (upright.Width, upright.Height));
        using var pdf = PdfDocument.Open(result.OutputPath);
        // 96 pixels per inch: three quarters of the pixel size in points
        Assert.Equal((upright.Width * 0.75, upright.Height * 0.75), (Math.Round(pdf.GetPage(1).Width, 1), Math.Round(pdf.GetPage(1).Height, 1)));
        Assert.Equal(File.ReadAllBytes(source), OnlyImage(pdf).RawBytes.ToArray());

        using var drawn = Drawn(result.OutputPath, (int)upright.Width, (int)upright.Height);
        Assert.InRange(upright.Compare(drawn, ErrorMetric.RootMeanSquared), 0, 0.03);
    }

    [Fact]
    public async Task TransparentAreasShowThePaper()
    {
        var result = await samples.ConvertAsync(samples.TransparentPng, "pdf");

        using var drawn = Drawn(result.OutputPath, 320, 240);
        // (40, 120) lies in the transparent half, (250, 120) in the picture
        var paper = drawn.GetPixels().GetPixel(40, 120).ToColor()!.ToByteArray();
        Assert.True(paper[0] > 250 && paper[1] > 250 && paper[2] > 250, $"expected white paper, got {paper[0]},{paper[1]},{paper[2]}");

        using var source = new MagickImage(samples.TransparentPng);
        var expected = source.GetPixels().GetPixel(250, 120).ToColor()!.ToByteArray();
        var shown = drawn.GetPixels().GetPixel(250, 120).ToColor()!.ToByteArray();
        Assert.InRange(shown[0] - expected[0], -2, 2);
        Assert.InRange(shown[2] - expected[2], -2, 2);
    }

    [Fact]
    public async Task APaletteDrawingKeepsItsPalette()
    {
        var source = Source("drawing.png", MagickFormat.Png8, image => image.Quantize(new QuantizeSettings { Colors = 16, DitherMethod = DitherMethod.No }));
        var result = await samples.ConvertAsync(source, "pdf");

        using var pdf = PdfDocument.Open(result.OutputPath);
        Assert.Equal(ColorSpace.Indexed, OnlyImage(pdf).ColorSpaceDetails?.Type);
        Assert.InRange(Samples.Size(result.OutputPath), 1, Samples.Size(source) + 1500);

        using var expected = new MagickImage(source);
        using var drawn = Drawn(result.OutputPath, 320, 240);
        Assert.InRange(expected.Compare(drawn, ErrorMetric.RootMeanSquared), 0, 0.01);
    }

    [Fact]
    public async Task GrayAndProfiledPicturesKeepTheirColourDescription()
    {
        var gray = Source("gray.jpg", MagickFormat.Jpeg, image => image.Grayscale());
        var wide = Source("adobe.jpg", MagickFormat.Jpeg, image => image.SetProfile(ColorProfiles.AdobeRGB1998));

        var first = await samples.ConvertAsync(gray, "pdf");
        var second = await samples.ConvertAsync(wide, "pdf");

        using var grayPdf = PdfDocument.Open(first.OutputPath);
        using var widePdf = PdfDocument.Open(second.OutputPath);
        Assert.Equal(ColorSpace.DeviceGray, OnlyImage(grayPdf).ColorSpaceDetails?.Type);
        Assert.Equal(ColorSpace.ICCBased, OnlyImage(widePdf).ColorSpaceDetails?.Type);
    }

    [Fact]
    public async Task FormatsAPdfCannotHoldAreConvertedOnce()
    {
        // a lossy source becomes a JPEG, a lossless one stays exact
        var webp = Source("lossy.webp", MagickFormat.WebP, image => image.Quality = 80);
        var tiff = Source("exact.tiff", MagickFormat.Tiff, _ => { });
        // interlaced rows are not in the order a PDF needs: this PNG is rewritten too
        var interlaced = Source("interlaced.png", MagickFormat.Png, image => image.Settings.Interlace = Interlace.Png);

        var fromWebp = await samples.ConvertAsync(webp, "pdf");
        var fromTiff = await samples.ConvertAsync(tiff, "pdf");
        var fromInterlaced = await samples.ConvertAsync(interlaced, "pdf");

        using (var pdf = PdfDocument.Open(fromWebp.OutputPath))
            Assert.Contains("DCTDecode", Name(OnlyImage(pdf), "Filter"));

        foreach (var (source, produced) in new[] { (tiff, fromTiff.OutputPath), (interlaced, fromInterlaced.OutputPath) })
        {
            using var pdf = PdfDocument.Open(produced);
            Assert.Contains("FlateDecode", Name(OnlyImage(pdf), "Filter"));
            Assert.True(OnlyImage(pdf).TryGetPng(out var png));
            using var expected = new MagickImage(source);
            using var stored = new MagickImage(png!);
            Assert.Equal(0, expected.Compare(stored, ErrorMetric.RootMeanSquared));
        }
    }

    [Fact]
    public async Task ManyPicturesBecomeOnePageEach()
    {
        var result = await samples.CombineAsync([samples.Png, samples.Jpg, samples.TransparentPng, Turned(6)], "pdf");

        using var pdf = PdfDocument.Open(result);
        Assert.Equal(4, pdf.NumberOfPages);
        // pages follow the file names: cutout.png, photo.jpg, photo.png, turned-6.jpg
        var sizes = pdf.GetPages().Select(page => $"{Math.Round(page.Width)}x{Math.Round(page.Height)}");
        Assert.Equal("240x180 240x180 240x180 180x240", string.Join(' ', sizes));
        using var renderer = new PdfRenderer(result);
        Assert.Equal(4, renderer.PageCount);
    }
}
