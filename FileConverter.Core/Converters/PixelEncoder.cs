using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using SkiaSharp;

namespace FileConverter.Core.Converters;

/// <summary>
/// Writes 8-bit pixels as PNG (and rendered pages as JPEG) with Skia's encoders.
///
/// ImageMagick's PNG writer is the slow step of any conversion that ends in a PNG. Measured in
/// alternation on a 9-megapixel photo, Skia writes the same picture 1.3 to 1.5 times as fast at
/// every compression level and 1–3 % smaller; both are built on libpng, so it is the same kind
/// of file. A rendered PDF page gains more: its pixels go from PDFium straight to the encoder
/// instead of being turned into an ImageMagick picture first.
///
/// ImageMagick keeps the cases this encoder would do worse or cannot do: pictures with more
/// than 8 bits per channel, pictures of 256 colours or fewer (it stores those with a palette),
/// and pictures carrying metadata this class does not know how to put into a PNG. Every method
/// answers null for "not here", and the caller writes the file the old way.
/// </summary>
internal static class PixelEncoder
{
    /// <summary>Set once Skia's native library turns out to be missing (a server without fontconfig, say).</summary>
    private static volatile bool _unavailable;

    private enum Layout
    {
        Gray,
        Rgb,
        Rgba
    }

    // ---------- decoded pictures ----------

    /// <summary>The picture as a PNG at the given zlib level (0–9), or null when ImageMagick should write it.</summary>
    public static byte[]? TryPng(MagickImage image, int level)
    {
        if (_unavailable || image.Depth > 8) return null;
        if (image.ColorSpace is not (ColorSpace.sRGB or ColorSpace.Gray)) return null;
        // only what can be carried over: a colour profile, EXIF and XMP
        foreach (var name in image.ProfileNames)
            if (name is not ("icc" or "icm" or "exif" or "xmp")) return null;

        int width = (int)image.Width, height = (int)image.Height;
        byte[]? rgba;
        using (var pixels = image.GetPixelsUnsafe())
            rgba = pixels.ToByteArray(PixelMapping.RGBA);
        if (rgba == null || rgba.Length != (long)width * height * 4) return null;

        if (Classify(MemoryMarshal.Cast<byte, uint>(rgba), image.HasAlpha) is not { } layout) return null;

        var png = layout == Layout.Gray
            ? Encode(FirstChannel(rgba, width * height), width, height, SKColorType.Gray8, SKAlphaType.Opaque, level)
            : Encode(rgba, width, height, SKColorType.Rgba8888,
                layout == Layout.Rgb ? SKAlphaType.Opaque : SKAlphaType.Unpremul, level);
        return png == null ? null : WithChunks(png, Metadata(image, layout));
    }

    /// <summary>
    /// What the smallest faithful PNG of these pixels (R, G, B, A bytes) looks like — or null
    /// when they have so few colours that a palette is smaller still, which ImageMagick writes.
    /// A photo answers within its first few thousand pixels.
    /// </summary>
    private static Layout? Classify(ReadOnlySpan<uint> pixels, bool hasAlpha)
    {
        const int paletteSize = 256;
        Span<uint> seen = stackalloc uint[1024]; // open addressing; 0 marks an empty slot
        bool sawZero = false;
        int colours = 0;
        bool gray = true, opaque = true;

        int i = 0;
        for (; i < pixels.Length && (gray || colours <= paletteSize); i++)
        {
            uint pixel = pixels[i];
            if (pixel < 0xFF000000) opaque = false;
            if (gray && ((byte)pixel != (byte)(pixel >> 8) || (byte)pixel != (byte)(pixel >> 16))) gray = false;

            if (colours > paletteSize) continue;
            if (pixel == 0)
            {
                if (!sawZero) { sawZero = true; colours++; }
                continue;
            }
            uint slot = (pixel * 2654435761u) >> 22;
            while (seen[(int)slot] != 0 && seen[(int)slot] != pixel) slot = (slot + 1) & 1023;
            if (seen[(int)slot] == 0)
            {
                seen[(int)slot] = pixel;
                colours++;
            }
        }
        if (colours <= paletteSize) return null;

        // colourful and not gray: only transparency is still open
        if (hasAlpha && opaque)
            for (; i < pixels.Length; i++)
                if (pixels[i] < 0xFF000000) { opaque = false; break; }

        if (!hasAlpha) opaque = true;
        if (gray) return opaque ? Layout.Gray : null; // gray with transparency: ImageMagick's two-channel PNG is smaller
        return opaque ? Layout.Rgb : Layout.Rgba;
    }

    /// <summary>Every fourth byte: the gray value of pixels whose colour channels are all alike.</summary>
    private static byte[] FirstChannel(byte[] pixels, int count)
    {
        var gray = new byte[count];
        for (int i = 0; i < gray.Length; i++) gray[i] = pixels[i * 4];
        return gray;
    }

    // ---------- rendered pages ----------

    /// <summary>
    /// An opaque page (B, G, R, x bytes, as PDFium draws it) as a PNG that records its resolution.
    /// A page without colour — most text — is stored as gray.
    /// </summary>
    public static byte[]? TryPngFromBgra(byte[] bgra, int width, int height, int level, double dpi)
    {
        if (_unavailable) return null;

        var pixels = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan(0, width * height * 4));
        bool gray = true;
        foreach (uint pixel in pixels)
        {
            if ((byte)pixel == (byte)(pixel >> 8) && (byte)pixel == (byte)(pixel >> 16)) continue;
            gray = false;
            break;
        }

        var png = gray
            ? Encode(FirstChannel(bgra, width * height), width, height, SKColorType.Gray8, SKAlphaType.Opaque, level)
            : Encode(bgra, width, height, SKColorType.Bgra8888, SKAlphaType.Opaque, level);
        return png == null ? null : WithChunks(png, [.. StandardColour(), Resolution(dpi, dpi)]);
    }

    /// <summary>An opaque page (B, G, R, x bytes) as a JPEG that records its resolution.</summary>
    /// <param name="chroma">"444", "422" or "420".</param>
    public static byte[]? TryJpegFromBgra(byte[] bgra, int width, int height, int quality, string chroma, double dpi)
    {
        if (_unavailable) return null;
        var downsample = chroma switch
        {
            "420" => SKJpegEncoderDownsample.Downsample420,
            "422" => SKJpegEncoderDownsample.Downsample422,
            _ => SKJpegEncoderDownsample.Downsample444
        };
        var jpeg = Encode(bgra, width, height, SKColorType.Bgra8888, SKAlphaType.Opaque,
            pixmap => pixmap.Encode(new SKJpegEncoderOptions(Math.Clamp(quality, 1, 100), downsample, SKJpegEncoderAlphaOption.Ignore)));
        if (jpeg == null) return null;

        // the JFIF header right behind the start marker carries the density: unit 1 = dots per inch
        int dots = (int)Math.Clamp(Math.Round(dpi), 1, ushort.MaxValue);
        if (jpeg.Length > 18 && jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.AsSpan(6, 5).SequenceEqual("JFIF\0"u8))
        {
            jpeg[13] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(14), (ushort)dots);
            BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(16), (ushort)dots);
        }
        return jpeg;
    }

    // ---------- Skia ----------

    private static byte[]? Encode(byte[] pixels, int width, int height, SKColorType colour, SKAlphaType alpha, int level) =>
        Encode(pixels, width, height, colour, alpha,
            pixmap => pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, Math.Clamp(level, 0, 9))));

    private static unsafe byte[]? Encode(byte[] pixels, int width, int height, SKColorType colour, SKAlphaType alpha,
        Func<SKPixmap, SKData?> encode)
    {
        try
        {
            var info = new SKImageInfo(width, height, colour, alpha);
            if (pixels.Length < (long)info.RowBytes * height) return null;
            fixed (byte* first = pixels)
            {
                using var pixmap = new SKPixmap(info, (IntPtr)first, info.RowBytes);
                using var data = encode(pixmap);
                return data?.ToArray();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException
                                       or BadImageFormatException)
        {
            _unavailable = true; // the library is not usable on this machine: stop trying
            return null;
        }
    }

    // ---------- PNG chunks ----------

    /// <summary>Everything the picture carries besides pixels, as PNG chunks.</summary>
    private static List<byte[]> Metadata(MagickImage image, Layout layout)
    {
        var chunks = new List<byte[]>();

        var profile = image.GetColorProfile();
        bool profileFits = profile != null && (profile.ColorSpace == ColorSpace.Gray) == (layout == Layout.Gray);
        if (profileFits)
        {
            using var body = new MemoryStream();
            body.Write("ICC profile\0\0"u8); // name, terminator, compression method 0 (zlib)
            using (var zlib = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(profile!.ToByteArray());
            chunks.Add(Chunk("iCCP", body.ToArray()));
        }
        else
        {
            chunks.AddRange(StandardColour());
        }

        var density = image.Density;
        double perInch = density.Units switch
        {
            DensityUnit.PixelsPerInch => 1,
            DensityUnit.PixelsPerCentimeter => 2.54,
            _ => 0
        };
        if (perInch > 0 && density.X > 0 && density.Y > 0)
            chunks.Add(Resolution(density.X * perInch, density.Y * perInch));

        if (image.GetExifProfile()?.ToByteArray() is { Length: > 6 } exif)
        {
            // ImageMagick keeps the JPEG marker's "Exif\0\0" in front; the chunk starts at the TIFF header
            var data = exif.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8) ? exif[6..] : exif;
            chunks.Add(Chunk("eXIf", data));
        }

        if (image.GetXmpProfile()?.ToByteArray() is { Length: > 0 } xmp)
        {
            // international text: keyword, no compression, no language, no translated keyword
            byte[] head = [.. "XML:com.adobe.xmp"u8, 0, 0, 0, 0, 0];
            chunks.Add(Chunk("iTXt", [.. head, .. xmp]));
        }

        if (image.Comment is { Length: > 0 } comment)
        {
            bool latin1 = comment.All(c => c is >= ' ' and <= 'ÿ' or '\n');
            chunks.Add(latin1
                ? Chunk("tEXt", [.. "Comment\0"u8, .. Encoding.Latin1.GetBytes(comment)])
                : Chunk("iTXt", [.. "Comment"u8, 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(comment)]));
        }
        return chunks;
    }

    /// <summary>"These are ordinary sRGB pixels", said the three ways readers of different ages look for it.</summary>
    private static byte[][] StandardColour()
    {
        Span<byte> gamma = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(gamma, 45455);

        // white point and the red, green and blue primaries of sRGB, each x then y, times 100 000
        uint[] chromaticities = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];
        var colours = new byte[32];
        for (int i = 0; i < chromaticities.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(colours.AsSpan(i * 4), chromaticities[i]);

        return [Chunk("sRGB", [0]), Chunk("gAMA", gamma), Chunk("cHRM", colours)];
    }

    private static byte[] Resolution(double dotsPerInchX, double dotsPerInchY)
    {
        Span<byte> data = stackalloc byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)Math.Round(dotsPerInchX / 0.0254));
        BinaryPrimitives.WriteUInt32BigEndian(data[4..], (uint)Math.Round(dotsPerInchY / 0.0254));
        data[8] = 1; // per metre
        return Chunk("pHYs", data);
    }

    private static byte[] Chunk(string type, ReadOnlySpan<byte> data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, chunk.AsSpan(4));
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    /// <summary>Puts the chunks straight behind the header chunk — a place every one of them is allowed in.</summary>
    private static byte[] WithChunks(byte[] png, IReadOnlyList<byte[]> chunks)
    {
        const int headerEnd = 8 + 25; // signature, then IHDR: length, type, 13 bytes, checksum
        if (chunks.Count == 0 || png.Length < headerEnd || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return png;

        var result = new byte[png.Length + chunks.Sum(c => c.Length)];
        png.AsSpan(0, headerEnd).CopyTo(result);
        int at = headerEnd;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(result, at);
            at += chunk.Length;
        }
        png.AsSpan(headerEnd).CopyTo(result.AsSpan(at));
        return result;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
