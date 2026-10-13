using FileConverter.Core;
using ImageMagick;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>Each precise picture setting, checked on the file it produces.</summary>
public class ImageSettingsTests(Samples samples) : IClassFixture<Samples>
{
    private static (uint Width, uint Height) Dimensions(string path)
    {
        var info = new MagickImageInfo(path);
        return (info.Width, info.Height);
    }

    [Fact]
    public async Task WithoutSettingsALosslessTargetStaysExact()
    {
        var result = await samples.ConvertAsync(samples.Png, "bmp");
        Assert.Equal("lossless", result.Note);

        using var source = new MagickImage(samples.Png);
        using var converted = new MagickImage(result.OutputPath);
        Assert.Equal(0, source.Compare(converted, ErrorMetric.RootMeanSquared));
    }

    [Fact]
    public async Task JpegQualityDecidesTheFileSize()
    {
        var low = await samples.ConvertAsync(samples.Png, "jpg", (K.Quality, "30"));
        var high = await samples.ConvertAsync(samples.Png, "jpg", (K.Quality, "95"));

        Assert.True(Samples.Size(low.OutputPath) * 2 < Samples.Size(high.OutputPath),
            $"quality 30 gave {Samples.Size(low.OutputPath)} bytes, quality 95 {Samples.Size(high.OutputPath)}");
        using var image = new MagickImage(low.OutputPath);
        Assert.Equal(30u, image.Quality);
    }

    [Fact]
    public async Task AnExplicitQualityReplacesTheSizeSlider()
    {
        // at 20 % the slider alone would search for a low quality; quality 90 is taken at its word instead
        var options = new ConversionOptions { SizePercent = 20, UseGpu = false };
        var result = await samples.ConvertAsync(samples.Png, "jpg", options, (K.Quality, "90"));

        using var image = new MagickImage(result.OutputPath);
        Assert.Equal(90u, image.Quality);
    }

    [Theory]
    [InlineData("444", "1x1")]
    [InlineData("422", "2x1")]
    [InlineData("420", "2x2")]
    public async Task JpegColourDetailIsWrittenAsAsked(string setting, string expectedLumaSampling)
    {
        var result = await samples.ConvertAsync(samples.Png, "jpg", (K.Chroma, setting));

        using var image = new MagickImage(result.OutputPath);
        Assert.StartsWith(expectedLumaSampling, image.GetAttribute("jpeg:sampling-factor"));
    }

    [Fact]
    public async Task ProgressiveJpegIsProgressive()
    {
        var progressive = await samples.ConvertAsync(samples.Png, "jpg", (K.Progressive, "on"));
        var baseline = await samples.ConvertAsync(samples.Png, "jpg", (K.Progressive, "off"));

        using var first = new MagickImage(progressive.OutputPath);
        using var second = new MagickImage(baseline.OutputPath);
        Assert.Equal(Interlace.Jpeg, first.Interlace);
        Assert.Equal(Interlace.NoInterlace, second.Interlace);
    }

    [Fact]
    public async Task WebPLosslessCanBeForcedEitherWay()
    {
        // a jpg source would normally become lossy webp; forced lossless must reproduce its pixels exactly
        var lossless = await samples.ConvertAsync(samples.Jpg, "webp", (K.Lossless, "on"));
        using (var source = new MagickImage(samples.Jpg))
        using (var converted = new MagickImage(lossless.OutputPath))
            Assert.Equal(0, source.Compare(converted, ErrorMetric.RootMeanSquared));

        // a png source would normally become lossless webp; forced lossy must be smaller and not exact
        var automatic = await samples.ConvertAsync(samples.Png, "webp");
        var lossy = await samples.ConvertAsync(samples.Png, "webp", (K.Lossless, "off"), (K.Quality, "60"));
        Assert.Equal("lossless", automatic.Note);
        Assert.Null(lossy.Note);
        Assert.True(Samples.Size(lossy.OutputPath) < Samples.Size(automatic.OutputPath));
    }

    [Fact]
    public async Task PngAndTiffCompressionChangeOnlyTheSize()
    {
        var fast = await samples.ConvertAsync(samples.Jpg, "png", (K.Compression, "0"));
        var small = await samples.ConvertAsync(samples.Jpg, "png", (K.Compression, "9"));
        Assert.True(Samples.Size(small.OutputPath) < Samples.Size(fast.OutputPath));
        using (var a = new MagickImage(fast.OutputPath))
        using (var b = new MagickImage(small.OutputPath))
            Assert.Equal(0, a.Compare(b, ErrorMetric.RootMeanSquared));

        var raw = await samples.ConvertAsync(samples.Png, "tiff", (K.Compression, "none"));
        var zip = await samples.ConvertAsync(samples.Png, "tiff", (K.Compression, "zip"));
        Assert.True(Samples.Size(zip.OutputPath) < Samples.Size(raw.OutputPath));
        using var uncompressed = new MagickImage(raw.OutputPath);
        Assert.Equal(CompressionMethod.NoCompression, uncompressed.Compression);
    }

    [Theory]
    [InlineData("100", null, null, 100, 75)]   // width limit, proportions kept
    [InlineData(null, "60", null, 80, 60)]     // height limit
    [InlineData("200", "60", null, 80, 60)]    // both: the tighter one wins
    [InlineData("1000", "1000", null, 320, 240)] // limits never enlarge
    [InlineData(null, null, "50", 160, 120)]   // percentage
    [InlineData(null, null, "200", 640, 480)]  // percentage above 100 enlarges
    [InlineData("300", null, "200", 300, 225)] // …and the limits still apply afterwards
    public async Task ResizeSettingsGiveExactDimensions(string? maxWidth, string? maxHeight, string? scale, int width, int height)
    {
        var settings = new List<(string, string)>();
        if (maxWidth != null) settings.Add((K.MaxWidth, maxWidth));
        if (maxHeight != null) settings.Add((K.MaxHeight, maxHeight));
        if (scale != null) settings.Add((K.Scale, scale));

        var result = await samples.ConvertAsync(samples.Png, "jpg", settings.ToArray());
        Assert.Equal(((uint)width, (uint)height), Dimensions(result.OutputPath));
    }

    /// <summary>
    /// A JPEG that is shrunk to a quarter or less is decoded smaller to begin with. The result has
    /// to have the size, and to all appearances the pixels, it gets when decoded in full.
    /// </summary>
    [Theory]
    [InlineData("maxWidth", "200", 200, 150)]
    [InlineData("maxHeight", "90", 120, 90)]
    [InlineData("scale", "12.5", 200, 150)]
    [InlineData("maxWidth", "333", 333, 250)]
    public async Task ALargePhotoMadeSmallComesOutTheSameEitherWay(string setting, string value, int width, int height)
    {
        var large = Path.Combine(samples.Folder, "large.jpg");
        var sideways = Path.Combine(samples.Folder, "large-sideways.jpg");
        if (!File.Exists(large))
        {
            using var picture = Samples.Picture(1600, 1200);
            picture.Quality = 92;
            picture.Write(large, MagickFormat.Jpeg);
            // the same photo as a phone stores it when held upright: lying on its side, with a note to turn it
            picture.SetProfile(new ExifProfile());
            picture.Orientation = Orientation.RightTop;
            picture.Write(sideways, MagickFormat.Jpeg);
        }
        var detail = Samples.Settings((setting, value));
        Assert.NotNull(FileConverter.Core.Converters.ImageConversion.SmallerDecode(large, detail));

        var result = await samples.ConvertAsync(large, "png", (setting, value));
        using var converted = new MagickImage(result.OutputPath);
        Assert.Equal(((uint)width, (uint)height), (converted.Width, converted.Height));

        // what decoding in full and resizing gives
        using var reference = new MagickImage(large);
        FileConverter.Core.Converters.ImageConversion.ApplyResize(reference, detail);
        Assert.Equal((reference.Width, reference.Height), (converted.Width, converted.Height));
        Assert.InRange(reference.Compare(converted, ErrorMetric.RootMeanSquared), 0, 0.01);

        // turned upright, the limits apply to the picture as it is shown
        var turned = await samples.ConvertAsync(sideways, "png", (setting, value));
        using var upright = new MagickImage(sideways);
        upright.AutoOrient();
        FileConverter.Core.Converters.ImageConversion.ApplyResize(upright, detail);
        using var turnedResult = new MagickImage(turned.OutputPath);
        Assert.Equal((upright.Width, upright.Height), (turnedResult.Width, turnedResult.Height));
        Assert.InRange(upright.Compare(turnedResult, ErrorMetric.RootMeanSquared), 0, 0.01);
    }

    /// <summary>
    /// The final size is worked out in numbers, so that the picture is resampled only once. The
    /// numbers have to be the ones ImageMagick arrives at when it is asked step by step.
    /// </summary>
    [Fact]
    public void TheSizeAPictureWillGetIsWorkedOutAsImageMagickDoes()
    {
        var random = new Random(7);
        for (int round = 0; round < 80; round++)
        {
            uint width = (uint)random.Next(1, 700), height = (uint)random.Next(1, 700);
            int? percent = random.Next(3) == 0 ? random.Next(5, 300) : null;
            int? maxWidth = random.Next(2) == 0 ? random.Next(1, 800) : null, maxHeight = random.Next(2) == 0 ? random.Next(1, 800) : null;
            var values = new List<(string, string)>();
            if (percent != null) values.Add((K.Scale, percent.Value.ToString()));
            if (maxWidth != null) values.Add((K.MaxWidth, maxWidth.Value.ToString()));
            if (maxHeight != null) values.Add((K.MaxHeight, maxHeight.Value.ToString()));

            using var image = new MagickImage(MagickColors.Gray, width, height);
            if (percent is { } p and not 100) image.Resize(new Percentage(p));
            if ((maxWidth is { } w && image.Width > w) || (maxHeight is { } h && image.Height > h))
                image.Resize(new MagickGeometry((uint)(maxWidth ?? (int)image.Width), (uint)(maxHeight ?? (int)image.Height)));

            var predicted = FileConverter.Core.Converters.ImageConversion.ResizedSize(width, height, Samples.Settings(values.ToArray()))
                            ?? (width, height);
            Assert.True((image.Width, image.Height) == predicted,
                $"{width}x{height} with {string.Join(", ", values)}: {image.Width}x{image.Height}, worked out as {predicted.Item1}x{predicted.Item2}");
        }

        // a percentage with decimals is taken as written, not rounded to a whole percent first
        Assert.Equal((200u, 150u), FileConverter.Core.Converters.ImageConversion.ResizedSize(1600, 1200, Samples.Settings((K.Scale, "12.5"))));
    }

    [Fact]
    public async Task AResizedPictureIsNoLongerCalledLossless()
    {
        var result = await samples.ConvertAsync(samples.Png, "bmp", (K.MaxWidth, "100"));
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task PhotoInformationIsKeptTrimmedOrRemoved()
    {
        static (string? Make, bool HasGps) Read(string path)
        {
            using var image = new MagickImage(path);
            var exif = image.GetExifProfile();
            return (exif?.GetValue(ExifTag.Make)?.Value, exif?.GetValue(ExifTag.GPSLatitude) != null);
        }

        Assert.Equal(("TestCam", true), Read(samples.TaggedJpg));

        var kept = await samples.ConvertAsync(samples.TaggedJpg, "webp", (K.Metadata, "keep"));
        var withoutLocation = await samples.ConvertAsync(samples.TaggedJpg, "webp", (K.Metadata, "nogps"));
        var stripped = await samples.ConvertAsync(samples.TaggedJpg, "webp", (K.Metadata, "strip"));

        Assert.Equal(("TestCam", true), Read(kept.OutputPath));
        Assert.Equal(("TestCam", false), Read(withoutLocation.OutputPath));
        Assert.Equal((null, false), Read(stripped.OutputPath));
    }

    [Fact]
    public async Task TransparencyIsFilledWithTheChosenColour()
    {
        var white = await samples.ConvertAsync(samples.TransparentPng, "jpg");
        var red = await samples.ConvertAsync(samples.TransparentPng, "jpg", (K.Background, "#FF0000"));

        // (40, 120) lies in the transparent half
        static (int R, int G, int B) Pixel(string path)
        {
            using var image = new MagickImage(path);
            var color = image.GetPixels().GetPixel(40, 120).ToColor()!.ToByteArray();
            return (color[0], color[1], color[2]);
        }

        var (wr, wg, wb) = Pixel(white.OutputPath);
        Assert.True(wr > 235 && wg > 235 && wb > 235, $"expected white, got {wr},{wg},{wb}");
        var (r, g, b) = Pixel(red.OutputPath);
        Assert.True(r > 220 && g < 40 && b < 40, $"expected red, got {r},{g},{b}");
    }

    [Fact]
    public async Task ATargetSizeInKilobytesIsMet()
    {
        var unlimited = await samples.ConvertAsync(samples.Png, "jpg");
        var limited = await samples.ConvertAsync(samples.Png, "jpg", (K.TargetSizeKb, "12"));

        Assert.True(Samples.Size(unlimited.OutputPath) > 12 * 1024 * 2, "the sample is too small to test a size target");
        Assert.InRange(Samples.Size(limited.OutputPath), 1, 12 * 1024);
    }

    [Fact]
    public async Task GifColoursAreLimited()
    {
        var result = await samples.ConvertAsync(samples.Png, "gif", (K.Colors, "8"));

        using var image = new MagickImage(result.OutputPath);
        Assert.InRange((int)image.TotalColors, 2, 8);
    }
}
