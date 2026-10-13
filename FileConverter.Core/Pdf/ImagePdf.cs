using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FileConverter.Core.Pdf;

/// <summary>
/// One picture in the form a PDF stores it. A PDF holds JPEG data as it is, and PNG data as it
/// is too (the same compressed rows, with the PNG row filters named as the "predictor") — so
/// both kinds of file go into a document without being decoded, let alone compressed again.
/// </summary>
internal sealed record PdfPicture
{
    /// <summary>A run of bytes inside a file.</summary>
    public readonly record struct Segment(long Offset, long Length);

    /// <summary>Stored size in pixels (before <see cref="Orientation"/> is applied).</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>EXIF orientation, 1–8: how the stored picture has to be turned to stand upright.</summary>
    public int Orientation { get; init; } = 1;

    /// <summary>"DCTDecode" (JPEG) or "FlateDecode" (PNG rows).</summary>
    public required string Filter { get; init; }

    /// <summary>1 = gray or palette entries, 3 = red, green, blue.</summary>
    public required int Channels { get; init; }
    public required int BitsPerChannel { get; init; }

    /// <summary>Red, green and blue bytes of a palette; null for pictures that store their colours directly.</summary>
    public byte[]? Palette { get; init; }

    /// <summary>The colour profile, zlib-compressed, when the picture carries one that fits its channels.</summary>
    public byte[]? CompressedProfile { get; init; }

    /// <summary>The file the picture data lies in, and where.</summary>
    public required string Path { get; init; }
    public required IReadOnlyList<Segment> Data { get; init; }

    /// <summary>The transparency of each pixel as a gray picture of the same size, if any pixel is not opaque.</summary>
    public PdfPicture? Transparency { get; init; }

    public long DataLength => Data.Sum(segment => segment.Length);

    /// <summary>Size as shown: a picture lying on its side is as wide as it is stored tall.</summary>
    public (int Width, int Height) DisplaySize => Orientation >= 5 ? (Height, Width) : (Width, Height);

    // ---------- JPEG ----------

    /// <summary>
    /// Reads a JPEG's headers. Returns null for the kinds a PDF cannot show from the file as it
    /// is: four-colour (print) JPEGs, 12-bit ones, and the rare arithmetic-coded or lossless variants.
    /// </summary>
    public static PdfPicture? TryJpeg(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        if (file.Read(head[..2]) != 2 || head[0] != 0xFF || head[1] != 0xD8) return null;

        int width = 0, height = 0, channels = 0, orientation = 1;
        var profileParts = new SortedDictionary<int, byte[]>();
        Span<byte> frame = stackalloc byte[6];

        while (true)
        {
            int marker = NextMarker(file);
            if (marker < 0) return null;
            if (marker is 0xD9 or 0xDA) break;                      // end of image, or the picture data begins
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) continue;    // markers without a length
            if (file.Read(head[..2]) != 2) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(head) - 2;
            if (length < 0) return null;
            long next = file.Position + length;

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (file.Read(frame) != 6 || frame[0] != 8) return null; // 8 bits per channel only
                height = BinaryPrimitives.ReadUInt16BigEndian(frame[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(frame[3..]);
                channels = frame[5];
            }
            else if (marker is >= 0xC3 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return null; // lossless, hierarchical or arithmetic coding
            }
            else if (marker == 0xE1 && length is >= 14 and <= 65533)
            {
                var data = new byte[length];
                file.ReadExactly(data);
                if (data.AsSpan(0, 6).SequenceEqual("Exif\0\0"u8)) orientation = ExifOrientation(data.AsSpan(6)) ?? orientation;
            }
            else if (marker == 0xE2 && length > 14)
            {
                var data = new byte[length];
                file.ReadExactly(data);
                // a profile too large for one segment is split over several, numbered from 1
                if (data.AsSpan(0, 12).SequenceEqual("ICC_PROFILE\0"u8)) profileParts[data[12]] = data[14..];
            }
            file.Position = next;
        }

        if (width == 0 || height == 0 || channels is not (1 or 3)) return null;

        byte[]? profile = profileParts.Count > 0 ? profileParts.Values.SelectMany(part => part).ToArray() : null;
        return new PdfPicture
        {
            Width = width,
            Height = height,
            Orientation = orientation,
            Filter = "DCTDecode",
            Channels = channels,
            BitsPerChannel = 8,
            CompressedProfile = ProfileFits(profile, channels) ? Deflate(profile!) : null,
            Path = path,
            Data = [new Segment(0, file.Length)]
        };
    }

    private static int NextMarker(Stream file)
    {
        int value = file.ReadByte();
        while (value != 0xFF && value >= 0) value = file.ReadByte(); // stray bytes between segments are skipped
        while (value == 0xFF) value = file.ReadByte();               // markers may be padded with 0xFF
        return value;
    }

    /// <summary>The orientation tag of an EXIF block (which is a small TIFF file), if it has one.</summary>
    private static int? ExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 14) return null;
        bool little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        if (!little && !(tiff[0] == (byte)'M' && tiff[1] == (byte)'M')) return null;

        ushort Short(ReadOnlySpan<byte> at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(at) : BinaryPrimitives.ReadUInt16BigEndian(at);
        uint Long(ReadOnlySpan<byte> at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(at) : BinaryPrimitives.ReadUInt32BigEndian(at);

        long directory = Long(tiff[4..]);
        if (directory + 2 > tiff.Length) return null;
        int entries = Short(tiff[(int)directory..]);
        for (int i = 0; i < entries; i++)
        {
            long at = directory + 2 + i * 12L;
            if (at + 12 > tiff.Length) return null;
            var entry = tiff.Slice((int)at, 12);
            if (Short(entry) != 0x0112) continue;
            int value = Short(entry[8..]);
            return value is >= 1 and <= 8 ? value : null;
        }
        return null;
    }

    // ---------- PNG ----------

    /// <summary>
    /// Reads a PNG's chunks. Returns null for the kinds whose data a PDF cannot take as it is:
    /// interlaced files, and pictures with transparency (a PDF keeps that in a picture of its own).
    /// </summary>
    public static PdfPicture? TryPng(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[8];
        if (file.Read(head) != 8 || !head.SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return null;

        int width = 0, height = 0, bits = 0, colourType = -1;
        byte[]? palette = null, profile = null;
        bool standardColour = false;
        uint? gamma = null;
        var data = new List<Segment>();
        Span<byte> header = stackalloc byte[13];

        while (file.Read(head) == 8)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = Encoding.ASCII.GetString(head[4..]);
            long next = file.Position + length + 4; // the chunk's bytes, then its checksum
            if (next > file.Length) return null;

            switch (type)
            {
                case "IHDR":
                    if (length != 13 || file.Read(header) != 13) return null;
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                    bits = header[8];
                    colourType = header[9];
                    if (header[12] != 0) return null; // interlaced: the rows are not stored in order
                    break;
                case "PLTE" when length is > 0 and <= 768:
                    palette = new byte[length];
                    file.ReadExactly(palette);
                    break;
                case "tRNS":
                    return null; // transparency
                case "sRGB":
                    standardColour = true;
                    break;
                case "gAMA" when length == 4:
                    file.ReadExactly(head[..4]);
                    gamma = BinaryPrimitives.ReadUInt32BigEndian(head);
                    break;
                case "iCCP" when length is > 3 and < 16 << 20:
                {
                    var chunk = new byte[length];
                    file.ReadExactly(chunk);
                    int nameEnd = Array.IndexOf(chunk, (byte)0);
                    // name, a zero, the compression method (0 = zlib), then the compressed profile
                    if (nameEnd >= 0 && nameEnd + 2 < chunk.Length && chunk[nameEnd + 1] == 0) profile = chunk[(nameEnd + 2)..];
                    break;
                }
                case "IDAT":
                    data.Add(new Segment(file.Position, length));
                    break;
            }
            file.Position = next;
            if (type == "IEND") break;
        }

        // gray, colour, or palette — the three without a transparency channel
        if (width <= 0 || height <= 0 || data.Count == 0 || colourType is not (0 or 2 or 3)) return null;
        if (colourType == 3 && palette == null) return null;
        // a gamma other than the ordinary one would need the pixels recalculated
        if (gamma is { } value && !standardColour && profile == null && Math.Abs(value - 45455.0) > 1000) return null;

        int channels = colourType == 2 ? 3 : 1;
        int profileChannels = colourType == 0 ? 1 : 3; // a palette's entries are red, green, blue
        if (profile != null && !ProfileFits(Inflate(profile, 128), profileChannels)) profile = null;

        return new PdfPicture
        {
            Width = width,
            Height = height,
            Filter = "FlateDecode",
            Channels = channels,
            BitsPerChannel = bits,
            Palette = colourType == 3 ? palette : null,
            CompressedProfile = profile,
            Path = path,
            Data = data
        };
    }

    // ---------- colour profiles ----------

    /// <summary>Is this an ICC profile for the given number of channels? (Bytes 16–19 name its colour space.)</summary>
    private static bool ProfileFits(byte[]? profile, int channels)
    {
        if (profile is not { Length: >= 128 }) return false;
        var space = profile.AsSpan(16, 4);
        return channels == 1 ? space.SequenceEqual("GRAY"u8) : space.SequenceEqual("RGB "u8);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return buffer.ToArray();
    }

    /// <summary>The first <paramref name="count"/> bytes of zlib-compressed data, or null when it cannot be read.</summary>
    private static byte[]? Inflate(byte[] compressed, int count)
    {
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var result = new byte[count];
            return zlib.ReadAtLeast(result, count, throwOnEndOfStream: false) == count ? result : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}

/// <summary>A page of an image PDF: its size, and where on it the picture sits (all in points, origin bottom left).</summary>
internal sealed record PdfPicturePage(PdfPicture Picture, double PageWidth, double PageHeight,
    double X, double Y, double Width, double Height);

/// <summary>
/// Writes a PDF whose pages each show one picture — the whole file format this takes is a few
/// dozen lines of text around the picture data, which is copied in from where it lies.
/// </summary>
internal static class ImagePdfWriter
{
    public static void Write(string output, IReadOnlyList<PdfPicturePage> pages, string producer,
        Action<int>? pageWritten, CancellationToken ct)
    {
        using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var offsets = new List<long> { 0 }; // object numbers start at 1

        void Text(string text) => file.Write(Encoding.Latin1.GetBytes(text));

        int Begin()
        {
            offsets.Add(file.Position);
            Text($"{offsets.Count - 1} 0 obj\n");
            return offsets.Count - 1;
        }

        int Stream(string dictionary, ReadOnlySpan<byte> data)
        {
            int number = Begin();
            Text($"<< {dictionary} /Length {data.Length} >>\nstream\n");
            file.Write(data);
            Text("\nendstream\nendobj\n");
            return number;
        }

        Text("%PDF-1.7\n");
        file.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']); // tells transfer programs this is not text

        // 1 = the catalogue, 2 = the page list (written last, when the pages are known), then the pages' objects
        offsets.Add(0);
        offsets.Add(0);
        var pageNumbers = new List<int>();
        var buffer = new byte[1 << 20];

        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[i];
            int transparency = page.Picture.Transparency is { } mask ? Picture(mask, softMask: 0) : 0;
            int picture = Picture(page.Picture, transparency);

            int contents = Stream("", Encoding.ASCII.GetBytes($"q {Placement(page)} cm /Im0 Do Q"));

            pageNumbers.Add(Begin());
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(page.PageWidth)} {Number(page.PageHeight)}] " +
                 $"/Resources << /XObject << /Im0 {picture} 0 R >> >> /Contents {contents} 0 R >>\nendobj\n");
            pageWritten?.Invoke(i + 1);
        }

        offsets[1] = file.Position;
        Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = file.Position;
        Text($"2 0 obj\n<< /Type /Pages /Count {pageNumbers.Count} /Kids [{string.Join(' ', pageNumbers.Select(n => $"{n} 0 R"))}] >>\nendobj\n");

        int info = Begin();
        var now = DateTimeOffset.Now;
        string zone = $"{(now.Offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(now.Offset.Hours):00}'{Math.Abs(now.Offset.Minutes):00}'";
        Text($"<< /Producer ({Escape(producer)}) /CreationDate (D:{now:yyyyMMddHHmmss}{zone}) >>\nendobj\n");

        // the table of where every object starts: ten digits, a generation number, "n", two end-of-line bytes
        long table = file.Position;
        Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Text($"{offset:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count} /Root 1 0 R /Info {info} 0 R >>\nstartxref\n{table}\n%%EOF\n");

        int Picture(PdfPicture picture, int softMask)
        {
            // the colour profile, when there is one, is an object of its own that the picture points to
            string device = picture.Channels == 1 && picture.Palette == null ? "/DeviceGray" : "/DeviceRGB";
            string colour = device;
            if (picture.CompressedProfile is { } profile)
            {
                int profileNumber = Stream($"/N {(device == "/DeviceGray" ? 1 : 3)} /Alternate {device} /Filter /FlateDecode", profile);
                colour = $"[/ICCBased {profileNumber} 0 R]";
            }
            if (picture.Palette is { } palette)
                colour = $"[/Indexed {colour} {palette.Length / 3 - 1} <{Convert.ToHexString(palette)}>]";

            var dictionary = new StringBuilder();
            dictionary.Append($"/Type /XObject /Subtype /Image /Width {picture.Width} /Height {picture.Height} ");
            dictionary.Append($"/ColorSpace {colour} /BitsPerComponent {picture.BitsPerChannel} /Filter /{picture.Filter} ");
            if (picture.Filter == "FlateDecode")
                dictionary.Append($"/DecodeParms << /Predictor 15 /Colors {picture.Channels} " +
                                  $"/BitsPerComponent {picture.BitsPerChannel} /Columns {picture.Width} >> ");
            if (softMask != 0) dictionary.Append($"/SMask {softMask} 0 R ");

            int number = Begin();
            Text($"<< {dictionary}/Length {picture.DataLength} >>\nstream\n");
            using (var source = new FileStream(picture.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            {
                foreach (var segment in picture.Data)
                {
                    source.Position = segment.Offset;
                    long left = segment.Length;
                    while (left > 0)
                    {
                        int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read <= 0) throw new EndOfStreamException($"{System.IO.Path.GetFileName(picture.Path)} ended early.");
                        file.Write(buffer, 0, read);
                        left -= read;
                    }
                }
            }
            Text("\nendstream\nendobj\n");
            return number;
        }
    }

    /// <summary>
    /// The matrix that puts a picture — for a PDF a unit square, its top-left corner at (0, 1) —
    /// onto its rectangle of the page, turned the way its orientation says.
    /// </summary>
    private static string Placement(PdfPicturePage page)
    {
        double x = page.X, y = page.Y, w = page.Width, h = page.Height;
        double[] m = page.Picture.Orientation switch
        {
            2 => [-w, 0, 0, h, x + w, y],          // mirrored left to right
            3 => [-w, 0, 0, -h, x + w, y + h],     // upside down
            4 => [w, 0, 0, -h, x, y + h],          // mirrored top to bottom
            5 => [0, -h, -w, 0, x + w, y + h],     // mirrored along the diagonal
            6 => [0, -h, w, 0, x, y + h],          // lying on its left side: turn clockwise
            7 => [0, h, w, 0, x, y],               // mirrored along the other diagonal
            8 => [0, h, -w, 0, x + w, y],          // lying on its right side: turn anticlockwise
            _ => [w, 0, 0, h, x, y]
        };
        return string.Join(' ', m.Select(Number));
    }

    private static string Number(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string text) =>
        new(text.Where(c => c is >= ' ' and <= '~').SelectMany(c => c is '(' or ')' or '\\' ? new[] { '\\', c } : [c]).ToArray());
}
