using FileConverter.Core.Settings;
using ImageMagick;
using ImageMagick.Formats;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Converters;

/// <summary>Process-wide ImageMagick configuration, applied once before first use.</summary>
internal static class ImageEngine
{
    static ImageEngine()
    {
        // A batch already runs one image per core (see WorkGates), so ImageMagick's own worker
        // threads stay few; they only matter for resizes and colour transforms anyway.
        ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount / 4, 1, 6);
        // OpenCL speeds up none of the codecs and costs a slow device benchmark on first use.
        OpenCL.IsEnabled = false;
    }

    public static void EnsureInitialized() { }
}

/// <summary>
/// Image conversions via ImageMagick (Magick.NET, 16 bits per channel so deep-colour sources
/// are not truncated). At 100 % every target gets the most faithful encoding it supports:
/// lossless formats stay pixel-exact, lossy ones use near-maximum quality with full-resolution
/// colour. Below 100 %, lossy targets hit the size slider with a binary search over encoder
/// quality; lossless targets fall back to max compression and, if still over target, gentle
/// downscaling. Animated GIFs keep their animation for gif/webp targets.
///
/// Precise settings (quality, colour detail, size limits, metadata …) take the place of these
/// automatic choices one by one; whatever is not set keeps behaving as described.
/// </summary>
public static class ImageConversion
{
    /// <summary>
    /// JPEG quality at the 100 % slider position (with 4:4:4 chroma). Measured on 4K photos:
    /// 92 → 53 dB PSNR, 97 → 60 dB at 1.8× the size; 100 adds 2.5 dB for another 1.8×.
    /// </summary>
    internal const int JpegTopQuality = 97;

    /// <summary>Lossy WebP quality at the 100 % slider position (used when the source is itself lossy).</summary>
    private const uint WebPTopQuality = 96;

    public static Task<ConversionResult> ConvertAsync(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Convert(input, output, options, progress, ct), ct);

    private static ConversionResult Convert(string input, string output, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        ImageEngine.EnsureInitialized();

        var dst = FormatRegistry.GetExtension(output);
        long targetBytes = TargetBytes(new FileInfo(input).Length, dst, options);

        progress?.Report(0.05);

        var src = FormatRegistry.GetExtension(input);
        if (dst == "pdf")
        {
            if (src == "svg")
            {
                // a drawing stays a drawing: lines and letters in the PDF, sharp at any size
                DrawingPdf.Write(input, output, options);
                progress?.Report(1.0);
                return new ConversionResult(output, "vector");
            }
            // single image → one-page PDF (several images with the same target merge upstream)
            MergeConversion.ImagesToPdf([input], output, options, progress, ct);
            return new ConversionResult(output);
        }

        bool animated = src is "gif" or "webp" && IsAnimated(input);
        // a TIFF of several pages (a scan, a fax): every page is a picture of its own
        bool paged = src is "tif" or "tiff" && PageCount(input) > 1;
        bool exact = false;

        if (animated && dst is "gif" or "webp")
        {
            exact = ConvertAnimated(input, output, src, dst, targetBytes, options, progress, ct);
        }
        else if (animated || paged)
        {
            // animated source → still format: every frame becomes a numbered image; so does every page of a TIFF
            var folder = FrameExtraction.AnimatedImageToFrames(input, output, src, dst, options, progress, ct, pages: paged);
            progress?.Report(1.0);
            return new ConversionResult(folder);
        }
        else
        {
            exact = ConvertStill(input, output, src, dst, targetBytes, options, progress, ct);
        }

        progress?.Report(1.0);
        return new ConversionResult(output, exact ? "lossless" : null);
    }

    /// <summary>
    /// The size the result should fit into, or <see cref="long.MaxValue"/> for "no size target":
    /// an exact size in kilobytes when one is set, otherwise the slider — unless an explicit
    /// quality was chosen for a lossy target, which is then taken at its word.
    /// </summary>
    private static long TargetBytes(long sourceBytes, string dst, ConversionOptions options)
    {
        var detail = options.Detail;
        if (dst is "jpg" or "webp")
        {
            if (detail.Number(K.TargetSizeKb) is { } kilobytes and > 0) return (long)(kilobytes * 1024);
            if (detail.Has(K.Quality) || detail.Flag(K.Lossless) == true) return long.MaxValue;
        }
        return options.HasSizeTarget ? (long)(sourceBytes * options.SizePercent / 100.0) : long.MaxValue;
    }

    private static bool IsAnimated(string input) => PageCount(input) > 1;

    /// <summary>How many pictures the file holds: the frames of an animation, the pages of a TIFF.</summary>
    internal static int PageCount(string input)
    {
        try
        {
            // header-only scan: counts the frames without decoding any pixels
            return Math.Max(MagickImageInfo.ReadCollection(input).Count(), 1);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>Formats that store pixels exactly — re-saving them losslessly is worth it. (A drawing has no pixels to lose.)</summary>
    internal static bool IsLosslessSource(string src) => src is "png" or "bmp" or "gif" or "tif" or "tiff" or "ico" or "svg";

    // ---------- still images ----------

    private static bool ConvertStill(string input, string output, string src, string dst, long targetBytes,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var detail = options.Detail;
        using var image = LoadToResize(input, src, detail, out var goal);
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.25);

        bool sizeTargeted = targetBytes != long.MaxValue;
        bool untouched = Normalize(image, dst, StripMetadata(detail, sizeTargeted), detail);
        if (goal is { } size ? ResizeTo(image, size, detail) : ApplyResize(image, detail)) untouched = false;
        bool exact = ApplyEncoderSettings(image, dst, IsLosslessSource(src), options, sizeTargeted) && untouched;

        var format = ToFormat(dst);
        if (dst == "gif" && detail.Int(K.Colors, 2, 256) is { } palette)
            image.Quantize(new QuantizeSettings { Colors = (uint)palette });

        if (!sizeTargeted)
        {
            Save(image, output, dst);
            return exact;
        }

        if (dst is "jpg" or "webp")
        {
            var best = FitQuality(image, format, targetBytes, ct);
            File.WriteAllBytes(output, best);
        }
        else if (dst is "gif")
        {
            if (!detail.Has(K.Colors))
            {
                uint colors = targetBytes < new FileInfo(input).Length / 2 ? 64u : 128u;
                image.Quantize(new QuantizeSettings { Colors = colors });
            }
            WriteWithDownscale(image, output, dst, targetBytes, ct);
        }
        else
        {
            // png/bmp/tiff: lossless — scale down if the picture is too big as it is
            WriteWithDownscale(image, output, dst, targetBytes, ct,
                squeezePng: dst == "png" && !detail.Has(K.Compression));
        }
        return false;
    }

    /// <param name="frame">Which page of a TIFF of several pages.</param>
    /// <param name="detail">For a drawing: the resolution to draw it at, when one is set.</param>
    internal static MagickImage LoadStill(string input, string src, int frame = 0, DetailSettings? detail = null)
    {
        if (src == "svg") return LoadDrawing(input, detail);
        if (frame > 0) return new MagickImage(input, new MagickReadSettings { FrameIndex = (uint)frame, FrameCount = 1 });
        if (src != "ico") return new MagickImage(input);

        // An .ico holds the same picture at several sizes, smallest usually first — take the best one.
        using var entries = new MagickImageCollection(input);
        var largest = entries.OrderByDescending(e => (long)e.Width * e.Height).ThenByDescending(e => e.Depth).First();
        return (MagickImage)largest.Clone();
    }

    /// <summary>
    /// The resolution a drawing (SVG) is turned into pixels at unless one is set: print resolution,
    /// as for the pages of a PDF. A drawing says its size in units of 1/96 inch, so at 96 it comes
    /// out as many pixels as it says, and at 300 a little over three times as wide and high.
    /// </summary>
    internal const double DrawingDpi = 300;

    /// <summary>The most pixels a drawing is given, whatever resolution was asked for.</summary>
    private const double MaxDrawingPixels = 100_000_000;

    /// <summary>
    /// Draws an SVG at the chosen resolution — drawn at that size, not drawn small and enlarged, so
    /// every line is sharp. What the drawing leaves empty stays transparent.
    /// </summary>
    private static MagickImage LoadDrawing(string input, DetailSettings? detail)
    {
        double dpi = detail?.Int(K.Dpi, 36, 1200) ?? DrawingDpi;
        try
        {
            // its size as it says it, at 96 to the inch
            var header = new MagickImageInfo(input, new MagickReadSettings { Format = MagickFormat.Svg });
            double pixels = (double)header.Width * header.Height * (dpi / 96) * (dpi / 96);
            if (pixels > MaxDrawingPixels) dpi *= Math.Sqrt(MaxDrawingPixels / pixels);
        }
        catch (MagickException)
        {
            // the drawing itself says what is wrong with the file
        }

        var image = new MagickImage(input, new MagickReadSettings
        {
            Format = MagickFormat.Svg,
            Density = new Density(dpi),
            BackgroundColor = MagickColors.Transparent
        });
        image.Density = new Density(dpi, DensityUnit.PixelsPerInch);
        return image;
    }

    /// <summary>
    /// Loads a picture that the size settings may be about to shrink. A JPEG can be decoded at
    /// a half, a quarter or an eighth of its size for a fraction of the work, and the resize that
    /// follows then has that much less to do. That is done when the picture ends up at a quarter
    /// of its width or less — the decoder is never asked for less than twice the final size, so
    /// the resize still starts from more than enough pixels — and <paramref name="goal"/> is then
    /// the size it has to be resized to. Measured on eight 9-megapixel photos, the result differs
    /// from decoding in full by 49 to 66 dB, far below what can be seen.
    /// </summary>
    private static MagickImage LoadToResize(string input, string src, DetailSettings detail, out (uint Width, uint Height)? goal)
    {
        goal = null;
        if (src is "jpg" or "jpeg" && SmallerDecode(input, detail) is { } smaller)
        {
            var settings = new MagickReadSettings();
            settings.SetDefine(MagickFormat.Jpeg, "size", $"{smaller.AtLeast.Width}x{smaller.AtLeast.Height}");
            var image = new MagickImage(input, settings);
            goal = smaller.Goal;
            return image;
        }
        return LoadStill(input, src, detail: detail);
    }

    /// <summary>
    /// For a JPEG that the settings make four times smaller or more: the size the decoder has to
    /// deliver at least (as stored), and the size the picture finally gets (as shown). Null when
    /// it is decoded in full — also when its sides are not multiples of eight, where the smaller
    /// decode would stretch it by a fraction of a pixel.
    /// </summary>
    internal static ((uint Width, uint Height) AtLeast, (uint Width, uint Height) Goal)? SmallerDecode(string input, DetailSettings detail)
    {
        if (!detail.Has(K.Scale) && !detail.Has(K.MaxWidth) && !detail.Has(K.MaxHeight)) return null;

        MagickImageInfo header;
        try
        {
            header = new MagickImageInfo(input);
        }
        catch (MagickException)
        {
            return null; // the full decode reports what is wrong with the file
        }
        if (header.Width == 0 || header.Height == 0 || header.Width % 8 != 0 || header.Height % 8 != 0) return null;

        bool sideways = header.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom;
        uint shownWidth = sideways ? header.Height : header.Width, shownHeight = sideways ? header.Width : header.Height;
        if (ResizedSize(shownWidth, shownHeight, detail) is not { } goal) return null;

        double factor = Math.Max((double)goal.Width / shownWidth, (double)goal.Height / shownHeight);
        if (factor > 0.25) return null;
        return (((uint)Math.Ceiling(header.Width * factor * 2), (uint)Math.Ceiling(header.Height * factor * 2)), goal);
    }

    /// <summary>
    /// The size the settings give a picture of this size, or null when they leave it as it is:
    /// first the percentage, then the fit into the maximum width and height (which never
    /// enlarges), each rounded as ImageMagick rounds. Worked out in numbers so that the picture
    /// itself is resampled once, straight to the result.
    /// </summary>
    internal static (uint Width, uint Height)? ResizedSize(uint width, uint height, DetailSettings detail)
    {
        double w = width, h = height;
        bool resized = false;
        if (detail.Number(K.Scale) is { } percent && Math.Abs(percent - 100) > 0.01)
        {
            percent = Math.Clamp(percent, 1, 400);
            w = Math.Max(Math.Floor(w * percent / 100 + 0.5), 1);
            h = Math.Max(Math.Floor(h * percent / 100 + 0.5), 1);
            resized = true;
        }

        int? maxWidth = detail.Int(K.MaxWidth, 1, 65535), maxHeight = detail.Int(K.MaxHeight, 1, 65535);
        if ((maxWidth is { } widest && w > widest) || (maxHeight is { } tallest && h > tallest))
        {
            double fit = Math.Min((maxWidth ?? w) / w, (maxHeight ?? h) / h);
            (w, h) = (Math.Max(Math.Floor(fit * w + 0.5), 1), Math.Max(Math.Floor(fit * h + 0.5), 1));
            resized = true;
        }
        return resized ? ((uint)w, (uint)h) : null;
    }

    /// <summary>
    /// Resamples the picture to exactly this size. Shrinking uses ImageMagick's own choice, the
    /// Lanczos filter; a percentage (the one setting that can enlarge) asks for it by name, since
    /// for enlarging the default would be a softer one.
    /// </summary>
    private static bool ResizeTo(MagickImage image, (uint Width, uint Height) size, DetailSettings detail)
    {
        if (detail.Has(K.Scale)) image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry(size.Width, size.Height) { IgnoreAspectRatio = true });
        return true;
    }

    /// <summary>
    /// Is the photo information dropped? The user's choice when there is one; otherwise only
    /// when a smaller file was asked for, where metadata costs bytes.
    /// </summary>
    internal static bool StripMetadata(DetailSettings detail, bool sizeTargeted) => detail.Text(K.Metadata) switch
    {
        "strip" => true,
        "keep" or "nogps" => false,
        _ => sizeTargeted
    };

    /// <summary>
    /// Makes the pixels mean the same thing in the target format as they did in the source:
    /// bakes in EXIF rotation, converts colour the target can't describe, flattens transparency
    /// the target can't hold. Returns false when that changed pixel values.
    /// </summary>
    internal static bool Normalize(MagickImage image, string dst, bool strip, DetailSettings? detail = null)
    {
        bool untouched = true;

        // Most viewers ignore the orientation tag outside JPEG, so turn the pixels themselves.
        image.AutoOrient();

        bool carriesProfile = dst is "jpg" or "png" or "webp" or "tiff";
        if (image.ColorSpace == ColorSpace.CMYK && (strip || dst is not ("jpg" or "tiff")))
        {
            // print colours → screen colours through ICC profiles, not a naive channel flip
            if (!image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB))
                image.ColorSpace = ColorSpace.sRGB;
            untouched = false;
        }
        else if ((strip || !carriesProfile) && image.GetColorProfile() is { } profile && !IsSrgb(profile))
        {
            // The wide-gamut profile (Display P3 phone photos, Adobe RGB) is about to be dropped;
            // without converting first the colours would come out dull.
            image.TransformColorSpace(ColorProfiles.SRGB);
            untouched = false;
        }

        // JPEG/BMP have no alpha channel — composite onto white (or the chosen colour) instead of black artifacts.
        if (dst is "jpg" or "bmp" && image.HasAlpha)
        {
            image.BackgroundColor = BackgroundColor(detail);
            image.Alpha(AlphaAction.Remove);
            untouched = false;
        }

        if (strip) image.Strip();
        else if (detail?.Is(K.Metadata, "nogps") == true) RemoveLocation(image);
        return untouched;
    }

    private static bool IsSrgb(IColorProfile profile) =>
        profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) == true;

    private static MagickColor BackgroundColor(DetailSettings? detail)
    {
        if (detail?.Text(K.Background) is { } text)
        {
            try
            {
                return new MagickColor(text);
            }
            catch (MagickException)
            {
                // not a colour ImageMagick knows — fall back to white rather than fail the conversion
            }
            catch (ArgumentException)
            {
            }
        }
        return MagickColors.White;
    }

    /// <summary>Drops the GPS block from the EXIF data, and the XMP packet, which can repeat the position.</summary>
    private static void RemoveLocation(MagickImage image)
    {
        if (image.GetExifProfile() is { } exif)
        {
            foreach (var value in exif.Values.Where(v => v.Tag.ToString().StartsWith("GPS", StringComparison.OrdinalIgnoreCase)).ToList())
                exif.RemoveValue(value.Tag);
            image.SetProfile(exif);
        }
        if (image.GetXmpProfile() != null) image.RemoveProfile("xmp");
    }

    /// <summary>
    /// Scales by a percentage and/or down into a maximum width and height (proportions kept,
    /// never enlarged by the limits). Returns true when the picture was resized.
    /// </summary>
    internal static bool ApplyResize(MagickImage image, DetailSettings detail) =>
        ResizedSize(image.Width, image.Height, detail) is { } size && ResizeTo(image, size, detail);

    private const uint LowestSearchQuality = 12, HighestSearchQuality = 95;

    /// <summary>Where the search starts: most targets end up between quality 75 and 90.</summary>
    private const uint FirstSearchQuality = 80;

    private static byte[] FitQuality(MagickImage image, MagickFormat format, long targetBytes, CancellationToken ct) =>
        FitQuality(quality =>
        {
            ct.ThrowIfCancellationRequested();
            image.Quality = quality;
            return image.ToByteArray(format);
        }, targetBytes);

    /// <summary>
    /// The highest encoder quality (12 to 95) whose result fits <paramref name="targetBytes"/> —
    /// or the smallest result there is when nothing fits. Every try is a whole encode, so the
    /// search aims instead of halving the range: once it has a result on either side of the
    /// target it goes where their sizes say the target lies, and only halves when that step
    /// gained little. It ends on the same quality as halving does. On size tables recorded
    /// from twelve photos (each as JPEG and as WebP, 13 targets apiece) that took 4.0 encodes
    /// on average where halving takes 6.6.
    /// </summary>
    internal static byte[] FitQuality(Func<uint, byte[]> encode, long targetBytes)
    {
        uint lo = LowestSearchQuality, hi = HighestSearchQuality; // the answer is somewhere in lo…hi
        (uint Quality, long Bytes)? fits = null, tooBig = null;    // the nearest try on either side of the target
        byte[]? best = null, smallest = null;
        uint quality = FirstSearchQuality;
        bool halveNext = false;

        while (true)
        {
            var bytes = encode(quality);
            uint rangeBefore = hi - lo;

            if (smallest == null || bytes.Length < smallest.Length) smallest = bytes;
            if (bytes.Length <= targetBytes)
            {
                best = bytes; // fits — a higher quality might too
                fits = (quality, bytes.Length);
                lo = quality + 1;
            }
            else
            {
                tooBig = (quality, bytes.Length);
                hi = quality - 1;
            }
            if (lo > hi) break;

            if (fits is { } under && tooBig is { } over && over.Bytes > under.Bytes)
            {
                if (halveNext)
                {
                    quality = (lo + hi) / 2;
                    halveNext = false;
                }
                else
                {
                    // sizes grow roughly exponentially with quality: interpolate on their logarithms
                    double share = Math.Log((double)targetBytes / under.Bytes) / Math.Log((double)over.Bytes / under.Bytes);
                    quality = (uint)Math.Clamp(Math.Floor(under.Quality + share * (over.Quality - under.Quality)), lo, hi);
                    // a step that left more than half the range open (a jump in the curve) is followed by a halving
                    halveNext = hi - lo > rangeBefore / 2;
                }
            }
            else
            {
                // one side only so far: follow the usual curve from the nearest result
                var known = fits ?? tooBig!.Value;
                double scale = ScaleOf(known.Quality) * Math.Pow((double)known.Bytes / targetBytes, 1 / SizeExponent);
                quality = QualityForScale(scale, lo, hi);
            }
        }

        return best ?? smallest!;
    }

    /// <summary>
    /// How a file's size follows the quantiser scale behind a quality number: size ∝ scale^-0.68.
    /// Measured on six photos the exponent ran from 0.52 to 0.92 depending on the picture and
    /// the quality range; it only decides where the second try lands.
    /// </summary>
    private const double SizeExponent = 0.68;

    /// <summary>The quantiser scale a quality stands for (the JPEG convention: 50 → 100 %, 75 → 50 %, 95 → 10 %).</summary>
    private static double ScaleOf(uint quality) => quality < 50 ? 5000.0 / quality : 200.0 - 2.0 * quality;

    private static uint QualityForScale(double scale, uint lo, uint hi)
    {
        double quality = scale > 100 ? 5000 / scale : (200 - scale) / 2;
        return (uint)Math.Clamp(Math.Round(quality), lo, hi);
    }

    /// <summary>The zlib effort that squeezes the last few percent out of a PNG (see <see cref="PngLevelFor"/>).</summary>
    private const int PngHardLevel = 7;

    /// <summary>
    /// The zlib effort a PNG is written with: the chosen one, or what the speed preset stands for.
    /// Only speed and file size depend on it, never the pixels. Level 4 is the knee — measured on
    /// two 9-megapixel photos, levels 1–2 save about a fifth of the time for files 9–21 % larger,
    /// and levels 6–7 take 1.6 to 3.8 times as long for files 3–9 % smaller. (Under a size target
    /// the hard level is tried only when those few percent decide whether the picture has to be
    /// resized: see <see cref="WriteWithDownscale"/>.)
    /// </summary>
    internal static int PngLevelFor(ConversionOptions options) => options.Detail.Int(K.Compression, 0, 9) ?? options.Speed switch
    {
        SpeedPreset.Fastest => 1,
        SpeedPreset.Fast => 2,
        SpeedPreset.Balanced => 4,
        _ => PngHardLevel
    };

    /// <summary>
    /// Writes a format whose only way down to a size is fewer pixels: the picture is made as
    /// large as still fits (see <see cref="FitScale"/>). Each try starts again from the full-size
    /// pixels, so the result has been resampled once however many tries it took.
    /// </summary>
    /// <param name="squeezePng">
    /// A PNG that misses the target by a few percent is first compressed harder — many times the
    /// effort, and worth it exactly when it saves the picture from being resized at all.
    /// </param>
    private static void WriteWithDownscale(MagickImage image, string output, string dst,
        long targetBytes, CancellationToken ct, bool squeezePng = false)
    {
        var bytes = Encode(image, dst);
        if (squeezePng && bytes.Length > targetBytes && bytes.Length * 0.92 <= targetBytes)
        {
            var usual = image.Settings.GetDefine(MagickFormat.Png, "compression-level");
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", PngHardLevel.ToString());
            var squeezed = Encode(image, dst);
            if (squeezed.Length < bytes.Length) bytes = squeezed;
            if (usual != null) image.Settings.SetDefine(MagickFormat.Png, "compression-level", usual);
        }

        bytes = FitScale(bytes, scale =>
        {
            ct.ThrowIfCancellationRequested();
            using var smaller = (MagickImage)image.Clone();
            smaller.Resize(new Percentage(scale * 100));
            return Encode(smaller, dst);
        }, targetBytes);
        File.WriteAllBytes(output, bytes);
    }

    /// <summary>
    /// The picture at the largest scale whose file fits <paramref name="targetBytes"/>, given
    /// the file at full size. A lossless file shrinks with the scale to the power of 1.6 to 2.1
    /// (measured on five photos as PNG, TIFF and GIF), so the first try assumes 2 — the largest
    /// scale that could fit — and the next ones use the slope the tries themselves show. Usually
    /// that is two resizes; a result filling 90 % of the target is not worth another.
    /// </summary>
    /// <param name="encodeAt">Encodes the picture scaled by a factor between 0 and 1.</param>
    internal static byte[] FitScale(byte[] full, Func<double, byte[]> encodeAt, long targetBytes)
    {
        if (full.Length <= targetBytes) return full;

        const double goodEnough = 0.9;
        double scale = 1, exponent = 2;
        long size = full.Length;                 // of the latest try
        byte[]? best = null;
        byte[] smallest = full;
        double bestScale = 0;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            double next = scale * Math.Pow((double)targetBytes / size, 1 / exponent);
            if (attempt > 0) next *= 0.98;      // aimed with a measured slope: leave a little room
            next = Math.Clamp(next, 0.05, 1);
            if (best != null && next <= bestScale) break; // nothing larger is left to try

            var bytes = encodeAt(next);
            if (bytes.Length != size && Math.Abs(next - scale) > 1e-6)
                exponent = Math.Clamp(Math.Log((double)bytes.Length / size) / Math.Log(next / scale), 1.2, 2.6);
            scale = next;
            size = bytes.Length;

            if (bytes.Length < smallest.Length) smallest = bytes;
            if (bytes.Length > targetBytes) continue;
            if (next > bestScale)
            {
                best = bytes;
                bestScale = next;
            }
            if (bytes.Length >= targetBytes * goodEnough) break;
        }
        return best ?? smallest;
    }

    // ---------- writing ----------

    /// <summary>The zlib effort <see cref="ApplyEncoderSettings"/> chose for a PNG.</summary>
    private static int PngLevel(MagickImage image) =>
        int.TryParse(image.Settings.GetDefine(MagickFormat.Png, "compression-level"), out int level) ? level : 4;

    /// <summary>
    /// The picture in its target format, in memory. PNGs come from the fast encoder whenever it
    /// can write this picture (see <see cref="PixelEncoder"/>), everything else from ImageMagick.
    /// </summary>
    internal static byte[] Encode(MagickImage image, string dst) =>
        (dst == "png" ? PixelEncoder.TryPng(image, PngLevel(image)) : null) ?? image.ToByteArray(ToFormat(dst));

    /// <summary>Writes the picture to a file in its target format.</summary>
    internal static void Save(MagickImage image, string output, string dst)
    {
        if (dst == "png" && PixelEncoder.TryPng(image, PngLevel(image)) is { } png) File.WriteAllBytes(output, png);
        else image.Write(output, ToFormat(dst));
    }

    // ---------- animated gif/webp ----------

    private static bool ConvertAnimated(string input, string output, string src, string dst, long targetBytes,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        using var frames = new MagickImageCollection(input);
        frames.Coalesce();
        ct.ThrowIfCancellationRequested();
        progress?.Report(0.3);

        var detail = options.Detail;
        bool sizeTargeted = targetBytes != long.MaxValue;
        bool resized = false;
        foreach (var frame in frames)
            resized |= ApplyResize((MagickImage)frame, detail);

        if (sizeTargeted)
        {
            // Reduce colors and dimensions to approach the target.
            var pctOfSource = (double)targetBytes / Math.Max(new FileInfo(input).Length, 1);
            if (pctOfSource < 0.9 && !resized)
            {
                var scale = Math.Clamp(Math.Sqrt(pctOfSource) + 0.15, 0.4, 1.0);
                if (scale < 0.98)
                    foreach (var frame in frames)
                        frame.Resize(new Percentage(scale * 100));
            }
            if (dst == "gif" && !detail.Has(K.Colors))
                frames.Quantize(new QuantizeSettings { Colors = pctOfSource < 0.5 ? 96u : 160u });
        }
        if (dst == "gif" && detail.Int(K.Colors, 2, 256) is { } palette)
            frames.Quantize(new QuantizeSettings { Colors = (uint)palette });

        bool exact = false;
        foreach (var frame in frames)
        {
            // a gif's palette frames survive exactly in lossless webp (and usually get smaller)
            exact = ApplyEncoderSettings((MagickImage)frame, dst, IsLosslessSource(src), options, sizeTargeted);
            if (sizeTargeted && dst == "webp" && !detail.Has(K.Quality)) frame.Quality = 70;
        }

        progress?.Report(0.6);
        if (dst == "gif")
        {
            try { frames.OptimizeTransparency(); } catch { /* optimization is optional */ }
        }
        frames.Write(output, ToFormat(dst));
        return exact && dst == "webp" && !resized;
    }

    // ---------- shared ----------

    /// <summary>
    /// Configures the encoder for <paramref name="dst"/>: maximum fidelity at 100 %, and the speed
    /// preset decides how hard the compressor works — each replaced by the matching precise
    /// setting when one is given. Returns true when the encoding is pixel-exact.
    /// </summary>
    internal static bool ApplyEncoderSettings(MagickImage image, string dst, bool losslessSource,
        ConversionOptions options, bool sizeTargeted)
    {
        var speed = options.Speed;
        var detail = options.Detail;
        int? quality = detail.Int(K.Quality, 1, 100);

        switch (dst)
        {
            case "jpg":
                var jpeg = new JpegWriteDefines { OptimizeCoding = speed != SpeedPreset.Fastest };
                if (quality is { } jpegQuality) image.Quality = (uint)jpegQuality;
                else if (!sizeTargeted) image.Quality = (uint)JpegTopQuality;

                // Full-resolution colour unless asked otherwise: 4:2:0 visibly blurs red/blue edges and
                // text. When chasing a size the encoder's own default (4:2:0) is the better trade.
                jpeg.SamplingFactor = detail.Text(K.Chroma) switch
                {
                    "444" => JpegSamplingFactor.Ratio444,
                    "422" => JpegSamplingFactor.Ratio422,
                    "420" => JpegSamplingFactor.Ratio420,
                    _ => sizeTargeted ? null : JpegSamplingFactor.Ratio444
                };
                if (detail.Flag(K.Progressive) == true) image.Settings.Interlace = Interlace.Jpeg;
                else if (detail.Flag(K.Progressive) == false) image.Settings.Interlace = Interlace.NoInterlace;
                image.Settings.SetDefines(jpeg);
                return false;

            case "webp":
                bool lossless = detail.Flag(K.Lossless) ?? (quality == null && !sizeTargeted && losslessSource);
                if (lossless)
                {
                    // Quality must stay unset here: with a value below 100 the encoder silently switches
                    // to near-lossless and changes pixels. Effort is steered by "method" alone — 0 is
                    // already smaller than a PNG and ~20× faster than the higher methods.
                    image.Quality = 0;
                    image.Settings.SetDefines(new WebPWriteDefines
                    {
                        Lossless = true,
                        Method = speed == SpeedPreset.BestQuality ? 4 : 0,
                        ThreadLevel = true
                    });
                    return true;
                }
                var webp = new WebPWriteDefines
                {
                    // webp "method": 0 = fastest, 6 = best compression
                    Method = speed switch
                    {
                        SpeedPreset.Fastest => 0,
                        SpeedPreset.Fast => 2,
                        SpeedPreset.Balanced => 4,
                        _ => 6
                    },
                    ThreadLevel = true
                };
                // sharper colour edges, but ~5× the encode time — worth it only when time doesn't matter
                if (speed == SpeedPreset.BestQuality) webp.UseSharpYuv = true;
                if (quality is { } webpQuality) image.Quality = (uint)webpQuality;
                else if (!sizeTargeted) image.Quality = WebPTopQuality;
                image.Settings.SetDefines(webp);
                return false;

            case "png":
                // always pixel-exact; how hard the compressor works is PngLevelFor's choice
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", PngLevelFor(options).ToString());
                return true;

            case "tiff":
                // Always lossless; never inherit a lossy JPEG-in-TIFF mode from the source.
                // LZW with a horizontal predictor makes photos ~5× smaller than uncompressed for under a second.
                image.Settings.Compression = detail.Text(K.Compression) switch
                {
                    "none" => CompressionMethod.NoCompression,
                    "lzw" => CompressionMethod.LZW,
                    "zip" => CompressionMethod.Zip,
                    _ => sizeTargeted || speed == SpeedPreset.BestQuality
                        ? CompressionMethod.Zip
                        : speed == SpeedPreset.Fastest ? CompressionMethod.NoCompression : CompressionMethod.LZW
                };
                image.Settings.SetDefine(MagickFormat.Tiff, "predictor", "2");
                return true;

            case "bmp":
                return image.Depth <= 8;

            default:
                return false;
        }
    }

    internal static MagickFormat ToFormat(string ext) => ext switch
    {
        "jpg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "gif" => MagickFormat.Gif,
        "webp" => MagickFormat.WebP,
        "bmp" => MagickFormat.Bmp,
        "tiff" => MagickFormat.Tiff,
        _ => throw new FriendlyException($"Unsupported image format: {ext}")
    };
}
