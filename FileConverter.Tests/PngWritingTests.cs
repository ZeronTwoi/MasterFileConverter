using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileConverter.Core.Converters;
using ImageMagick;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// PNG files come from a faster encoder than ImageMagick's own. These check that it writes the
/// same pictures, with everything they carried, and leaves alone what ImageMagick does better.
/// </summary>
public class PngWritingTests(Samples samples) : IClassFixture<Samples>
{
    /// <summary>Saves a picture in a format that keeps exactly what it was given, to convert from.</summary>
    private string Source(string name, Action<MagickImage> prepare, MagickFormat format = MagickFormat.Tiff)
    {
        var path = Path.Combine(samples.Folder, name);
        if (File.Exists(path)) return path;
        using var image = Samples.Picture();
        prepare(image);
        image.Write(path, format);
        return path;
    }

    private static void AssertSamePixels(string expected, string actual)
    {
        using var first = new MagickImage(expected);
        using var second = new MagickImage(actual);
        Assert.Equal((first.Width, first.Height, first.HasAlpha), (second.Width, second.Height, second.HasAlpha));
        Assert.Equal(0, first.Compare(second, ErrorMetric.RootMeanSquared));
    }

    /// <summary>Every chunk of a PNG file, with its checksum verified.</summary>
    private static List<(string Type, byte[] Data)> Chunks(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        var chunks = new List<(string, byte[])>();
        for (int at = 8; at < bytes.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            var type = Encoding.ASCII.GetString(bytes, at + 4, 4);
            var covered = bytes.AsSpan(at + 4, 4 + length);
            Assert.Equal(Crc32(covered), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8 + length)));
            chunks.Add((type, bytes[(at + 8)..(at + 8 + length)]));
            at += 12 + length;
        }
        return chunks;
    }

    /// <summary>CRC-32 as .NET's own gzip writer computes it (the last eight bytes of a gzip stream are checksum and length).</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.NoCompression, leaveOpen: true))
            gzip.Write(data);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.ToArray().AsSpan((int)buffer.Length - 8));
    }

    [Fact]
    public void TheFastEncoderTakesPhotosAndLeavesTheRestToImageMagick()
    {
        using var photo = Samples.Picture();
        Assert.NotNull(PixelEncoder.TryPng(photo, 4));

        // a drawing in a handful of colours is smaller with a palette, which ImageMagick writes
        using var drawing = new MagickImage(MagickColors.SteelBlue, 200, 100);
        Assert.Null(PixelEncoder.TryPng(drawing, 4));

        using var deep = Samples.Picture();
        deep.Depth = 16;
        Assert.Null(PixelEncoder.TryPng(deep, 4));

        // metadata it has no place for must not be dropped on the way
        using var captioned = Samples.Picture();
        captioned.SetProfile(new ImageProfile("8bim", [1, 2, 3, 4]));
        Assert.Null(PixelEncoder.TryPng(captioned, 4));
    }

    [Fact]
    public async Task APhotoArrivesPixelForPixelWithItsCameraData()
    {
        var result = await samples.ConvertAsync(samples.TaggedJpg, "png");

        Assert.Equal("lossless", result.Note);
        AssertSamePixels(samples.TaggedJpg, result.OutputPath);
        using var image = new MagickImage(result.OutputPath);
        var exif = image.GetExifProfile();
        Assert.Equal("TestCam", exif?.GetValue(ExifTag.Make)?.Value);
        Assert.NotNull(exif?.GetValue(ExifTag.GPSLatitude));
    }

    [Fact]
    public async Task ColourProfileAndResolutionAreCarriedOver()
    {
        var source = Source("print.tiff", image =>
        {
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            image.Density = new Density(300, DensityUnit.PixelsPerInch);
        });
        var result = await samples.ConvertAsync(source, "png");

        AssertSamePixels(source, result.OutputPath);
        using var image = new MagickImage(result.OutputPath);
        Assert.Contains("Adobe", image.GetColorProfile()?.Description);
        // a PNG counts its dots per metre; ImageMagick reports them per centimetre
        Assert.InRange(image.Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 299.9, 300.1);

        var types = Chunks(result.OutputPath).Select(c => c.Type).ToList();
        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
        Assert.Single(types, "iCCP");
        Assert.Single(types, "pHYs");
        Assert.DoesNotContain("sRGB", types); // a profile and "plain sRGB" exclude each other
        Assert.True(types.IndexOf("iCCP") < types.IndexOf("IDAT"));
    }

    [Fact]
    public async Task APictureWithoutAProfileIsMarkedAsOrdinaryColour()
    {
        var result = await samples.ConvertAsync(samples.Jpg, "png");

        var types = Chunks(result.OutputPath).Select(c => c.Type).ToList();
        Assert.Single(types, "sRGB");
        Assert.Single(types, "gAMA");
        Assert.DoesNotContain("iCCP", types);
    }

    [Fact]
    public async Task TransparencyIsKeptExactly()
    {
        var source = Source("cutout.tiff", image =>
        {
            // the left half of the mask is black, which makes that half of the picture transparent
            image.Alpha(AlphaAction.Set);
            using var mask = new MagickImage(MagickColors.White, image.Width, image.Height);
            mask.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(0, 0, 159, 239));
            mask.Alpha(AlphaAction.Off);
            image.Composite(mask, CompositeOperator.CopyAlpha);
        });
        var result = await samples.ConvertAsync(source, "png");

        AssertSamePixels(source, result.OutputPath);
        using var image = new MagickImage(result.OutputPath);
        Assert.True(image.HasAlpha);
        Assert.Equal(0, image.GetPixels().GetPixel(40, 120).ToColor()!.A);
    }

    [Fact]
    public async Task AGrayPictureIsStoredAsGray()
    {
        var source = Source("gray.jpg", image =>
        {
            image.Grayscale();
            image.Quality = 92;
        }, MagickFormat.Jpeg);
        var result = await samples.ConvertAsync(source, "png");

        AssertSamePixels(source, result.OutputPath);
        // colour type 0: one gray channel, a third of the bytes of red, green and blue
        Assert.Equal(0, Chunks(result.OutputPath).Single(c => c.Type == "IHDR").Data[9]);
    }

    /// <summary>
    /// Not a PNG matter, but the same idea: the pages of a multi-page TIFF are compressed as files
    /// of their own, side by side, and joined. Every page has to come out as it went in.
    /// </summary>
    [Fact]
    public async Task SeveralPicturesBecomeOneTiffWithEveryPageIntact()
    {
        var transparent = Source("see-through.tiff", image =>
        {
            image.Alpha(AlphaAction.Set);
            using var mask = new MagickImage(MagickColors.White, image.Width, image.Height);
            mask.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(0, 0, 159, 239));
            mask.Alpha(AlphaAction.Off);
            image.Composite(mask, CompositeOperator.CopyAlpha);
        });
        using var smallPicture = Samples.Picture(40, 30);
        var small = Path.Combine(samples.Folder, "tiny.png");
        smallPicture.Write(small, MagickFormat.Png);
        // pages follow the file names
        string[] sources = [samples.Jpg, samples.Png, transparent, samples.TaggedJpg, small];
        var ordered = sources.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();

        var result = await samples.CombineAsync(sources, "tiff");

        using var pages = new MagickImageCollection(result);
        Assert.Equal(ordered.Length, pages.Count);
        for (int i = 0; i < ordered.Length; i++)
        {
            using var expected = new MagickImage(ordered[i]);
            Assert.Equal((expected.Width, expected.Height, expected.HasAlpha), (pages[i].Width, pages[i].Height, pages[i].HasAlpha));
            Assert.True(expected.Compare(pages[i], ErrorMetric.RootMeanSquared) == 0, $"page {i + 1} ({Path.GetFileName(ordered[i])}) differs");
        }
    }

    [Fact]
    public async Task AnAnimationsFramesAreWrittenTheSameWay()
    {
        var gif = Path.Combine(samples.Folder, "moving.gif");
        using (var frames = new MagickImageCollection())
        {
            for (int i = 0; i < 3; i++)
            {
                var frame = Samples.Picture();
                frame.Roll(i * 20, 0);
                frame.AnimationDelay = 10;
                frames.Add(frame);
            }
            frames.Write(gif, MagickFormat.Gif);
        }
        var result = await samples.ConvertAsync(gif, "png");

        var files = Directory.GetFiles(result.OutputPath, "*.png").Order().ToArray();
        Assert.Equal(3, files.Length);
        using var decoded = new MagickImageCollection(gif);
        decoded.Coalesce();
        for (int i = 0; i < files.Length; i++)
        {
            using var written = new MagickImage(files[i]);
            Assert.Equal(0, decoded[i].Compare(written, ErrorMetric.RootMeanSquared));
        }
    }
}
