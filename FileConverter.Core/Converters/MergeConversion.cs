using System.Runtime.ExceptionServices;
using FileConverter.Core.Pdf;
using FileConverter.Core.Settings;
using ImageMagick;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using K = FileConverter.Core.Settings.SettingKeys;
using QDocument = QuestPDF.Fluent.Document;

namespace FileConverter.Core.Converters;

/// <summary>
/// Context-aware "many files → one file" conversions: multiple images become a
/// single multi-page PDF / animated GIF / multi-page TIFF, and any set of files
/// can be packed into a single archive. Page and frame order follows Explorer-style
/// natural filename ordering (1, 2, 10 — img_2 before img_10 — any naming convention).
/// </summary>
public static class MergeConversion
{
    /// <summary>Can several sources with this target be combined into one output?</summary>
    public static bool CanMerge(FileCategory sourceCategory, string targetExt)
    {
        var t = targetExt.ToLowerInvariant();
        // Archives always make sense combined — except archive→archive, which is a per-file repack.
        if (t is "zip" or "7z" or "tar" or "rar") return sourceCategory != FileCategory.Archive;
        return sourceCategory == FileCategory.Image && t is "pdf" or "gif" or "tiff" or "tif";
    }

    /// <summary>
    /// Name for the combined output, derived from the sources: the common filename
    /// prefix ("scan 01…scan 20" → "scan"), else the containing folder's name.
    /// </summary>
    public static string SuggestBaseName(IReadOnlyList<string> paths)
    {
        var names = paths.Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (names.Count > 0)
        {
            var prefix = names[0]!;
            foreach (var name in names.Skip(1))
            {
                int len = 0;
                while (len < prefix.Length && len < name!.Length &&
                       char.ToUpperInvariant(prefix[len]) == char.ToUpperInvariant(name[len]))
                    len++;
                prefix = prefix[..len];
                if (prefix.Length == 0) break;
            }
            // "IMG_20" from IMG_2001/IMG_2002 — drop the digits that belong to the numbering
            var trimmed = prefix.TrimEnd("0123456789".ToCharArray()).Trim(' ', '.', '-', '_', '(', '[');
            if (trimmed.Length >= 2) return trimmed;
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(paths[0])?.TrimEnd('\\', '/') ?? "");
        return string.IsNullOrWhiteSpace(folder) ? "Combined" : folder;
    }

    /// <summary>Combines the (already naturally sorted) inputs into a single output file.</summary>
    public static Task ConvertManyAsync(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var dst = FormatRegistry.GetExtension(output);
        return dst switch
        {
            "pdf" => Task.Run(() => ImagesToPdf(inputs, output, options, progress, ct), ct),
            "gif" => Task.Run(() => ImagesToGif(inputs, output, options, progress, ct), ct),
            "tiff" or "tif" => Task.Run(() => ImagesToTiff(inputs, output, options, progress, ct), ct),
            "zip" or "7z" or "tar" or "rar" => ArchiveConversion.PackManyAsync(inputs, output, options, progress, ct),
            _ => throw new FriendlyException($"Cannot combine multiple files into a single .{dst}.")
        };
    }

    /// <summary>
    /// Decoding the sources is the slow part of every merge and each one is independent,
    /// so it runs in parallel. Surfaces the first real error instead of an AggregateException.
    /// </summary>
    private static void ForEachIndex(int count, CancellationToken ct, Action<int> body)
    {
        var workers = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 12),
            CancellationToken = ct
        };
        try
        {
            Parallel.For(0, count, workers, body);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
    }

    // ---------- images → one PDF ----------

    /// <summary>
    /// One page per image, each page sized to the image's aspect ratio (or a sheet of paper with
    /// the image centred on it). At 100 % the pictures go into the PDF untouched: JPEG and PNG
    /// files as the bytes they are, anything else decoded once and stored losslessly — or, when
    /// its source was lossy already, as a top-quality JPEG. Nothing is compressed a second time,
    /// which is what makes this as fast as copying the files.
    /// </summary>
    public static void ImagesToPdf(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        ImageEngine.EnsureInitialized();
        if (options.HasSizeTarget)
        {
            ImagesToSmallerPdf(inputs, output, options, progress, ct);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"fc-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var pictures = new PdfPicture[inputs.Count];
            int prepared = 0;
            ForEachIndex(inputs.Count, ct, i =>
            {
                pictures[i] = PreparePicture(inputs[i], tempDir, i, options.Speed);
                progress?.Report(0.6 * Interlocked.Increment(ref prepared) / inputs.Count);
            });

            // Without a paper size every page is exactly as large as its picture; with one, the
            // picture is centred on a sheet of that size, turned to landscape for wide pictures.
            var paper = PaperSize(options.Detail.Text(K.PageSize));
            double margin = Math.Clamp(options.Detail.Number(K.Margin) ?? 0, 0, 100) * 72.0 / 25.4;
            var pages = pictures.Select(picture => PlaceOnPage(picture, paper, margin)).ToList();

            ImagePdfWriter.Write(output, pages, $"{AppInfo.DisplayName} {AppInfo.Version}",
                written => progress?.Report(0.6 + 0.4 * written / pages.Count), ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The picture as a PDF stores it. JPEG and PNG files are only read as far as their headers
    /// and used from where they lie; everything else — formats a PDF has no place for (webp, tiff,
    /// heic, gif …), print-colour JPEGs, PNGs with transparency — is decoded once and written to
    /// <paramref name="tempDir"/> as a PNG (with its transparency beside it) when the source was
    /// lossless or has transparency, otherwise as a top-quality JPEG.
    /// </summary>
    private static PdfPicture PreparePicture(string input, string tempDir, int index, SpeedPreset speed)
    {
        var ext = FormatRegistry.GetExtension(input);
        var direct = ext switch
        {
            "jpg" or "jpeg" => PdfPicture.TryJpeg(input),
            "png" => PdfPicture.TryPng(input),
            _ => null
        };
        if (direct != null) return direct;

        using var image = ImageConversion.LoadStill(input, ext);
        bool keepExact = ImageConversion.IsLosslessSource(ext) || image.HasAlpha;
        string pageFormat = keepExact ? "png" : "jpg";
        ImageConversion.Normalize(image, pageFormat, strip: false);
        var encoding = new ConversionOptions { Speed = speed };
        ImageConversion.ApplyEncoderSettings(image, pageFormat, keepExact, encoding, sizeTargeted: false);

        var path = Path.Combine(tempDir, $"page-{index:D4}.{pageFormat}");
        if (!keepExact)
        {
            image.Write(path, MagickFormat.Jpeg);
            return PdfPicture.TryJpeg(path) ?? throw Unreadable(input);
        }

        PdfPicture? transparency = null;
        if (image.HasAlpha)
        {
            if (!image.IsOpaque)
            {
                // a PDF keeps transparency in a gray picture of its own
                var alphaPath = Path.Combine(tempDir, $"page-{index:D4}-alpha.png");
                using (var alpha = image.Separate(Channels.Alpha).First())
                {
                    alpha.Depth = 8;
                    alpha.Settings.SetDefine(MagickFormat.Png, "color-type", "0");
                    alpha.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
                    alpha.Settings.SetDefine(MagickFormat.Png, "compression-level", ImageConversion.PngLevelFor(encoding).ToString());
                    alpha.Write(alphaPath, MagickFormat.Png);
                }
                transparency = PdfPicture.TryPng(alphaPath) ?? throw Unreadable(input);
            }
            image.Alpha(AlphaAction.Off);
        }

        ImageConversion.Save(image, path, "png");
        var picture = PdfPicture.TryPng(path);
        if (picture == null)
        {
            // a kind of PNG the reader above does not take: plain 8-bit colour is always one it does
            image.Settings.SetDefine(MagickFormat.Png, "color-type", "2");
            image.Settings.SetDefine(MagickFormat.Png, "bit-depth", "8");
            image.Write(path, MagickFormat.Png);
            picture = PdfPicture.TryPng(path) ?? throw Unreadable(input);
        }
        return picture with { Transparency = transparency };
    }

    private static FriendlyException Unreadable(string input) =>
        new($"{Path.GetFileName(input)} could not be prepared for the PDF.");

    /// <summary>Page size and the picture's place on it, in points.</summary>
    private static PdfPicturePage PlaceOnPage(PdfPicture picture, PageSize? paper, double margin)
    {
        // 96 dpi pixels → points; keep pages inside PDF limits and above a readable minimum.
        var (pixelWidth, pixelHeight) = picture.DisplaySize;
        double width = pixelWidth * 72.0 / 96.0, height = pixelHeight * 72.0 / 96.0;
        double scale = Math.Min(1.0, 14000.0 / Math.Max(width, height));
        if (Math.Max(width, height) < 144) scale = 144.0 / Math.Max(Math.Max(width, height), 1);
        width *= scale;
        height *= scale;

        if (paper is not { } chosen)
            return new PdfPicturePage(picture, width + 2 * margin, height + 2 * margin, margin, margin, width, height);

        var sheet = width > height ? chosen.Landscape() : chosen.Portrait();
        double fit = Math.Min(Math.Max(sheet.Width - 2 * margin, 1) / width, Math.Max(sheet.Height - 2 * margin, 1) / height);
        width *= fit;
        height *= fit;
        return new PdfPicturePage(picture, sheet.Width, sheet.Height, (sheet.Width - width) / 2, (sheet.Height - height) / 2, width, height);
    }

    /// <summary>
    /// The size slider's version: the pictures are laid out by QuestPDF, which stores them at a
    /// lower resolution and JPEG quality the smaller the result is asked to be.
    /// </summary>
    private static void ImagesToSmallerPdf(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"fc-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var pages = new (string Path, float WidthPt, float HeightPt)[inputs.Count];
            int prepared = 0;
            ForEachIndex(inputs.Count, ct, i =>
            {
                pages[i] = PreparePdfPage(inputs[i], tempDir, i);
                progress?.Report(0.5 * Interlocked.Increment(ref prepared) / inputs.Count);
            });

            int pct = options.SizePercent;
            var settings = new DocumentSettings
            {
                ImageRasterDpi = Math.Max(72, (int)(288 * Math.Sqrt(pct / 100.0))),
                ImageCompressionQuality = pct switch
                {
                    >= 70 => ImageCompressionQuality.High,
                    >= 45 => ImageCompressionQuality.Medium,
                    >= 25 => ImageCompressionQuality.Low,
                    _ => ImageCompressionQuality.VeryLow
                }
            };

            // Without a paper size every page is exactly as large as its picture; with one, the
            // picture is centred on a sheet of that size, turned to landscape for wide pictures.
            var paper = PaperSize(options.Detail.Text(K.PageSize));
            float margin = (float)(Math.Clamp(options.Detail.Number(K.Margin) ?? 0, 0, 100) * 72.0 / 25.4);

            QDocument.Create(container =>
            {
                foreach (var (path, w, h) in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    container.Page(page =>
                    {
                        page.Margin(margin);
                        if (paper is { } sheet)
                        {
                            page.Size(w > h ? sheet.Landscape() : sheet.Portrait());
                            page.Content().AlignCenter().AlignMiddle().Image(path).UseOriginalImage(false).FitArea();
                        }
                        else
                        {
                            page.Size(w + 2 * margin, h + 2 * margin);
                            page.Content().Image(path).UseOriginalImage(false).FitArea();
                        }
                    });
                }
            }).WithSettings(settings).GeneratePdf(output);

            progress?.Report(1.0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The paper size a setting names, or null for "fit each page to its content".</summary>
    internal static PageSize? PaperSize(string? name) => name switch
    {
        "a4" => PageSizes.A4,
        "letter" => PageSizes.Letter,
        "legal" => PageSizes.Legal,
        "a3" => PageSizes.A3,
        "a5" => PageSizes.A5,
        _ => null
    };

    /// <summary>
    /// For <see cref="ImagesToSmallerPdf"/>: a path QuestPDF/Skia can read plus the page size in
    /// points. Upright JPEGs and PNGs are used as they are (only their header is read here).
    /// Everything else — formats Skia can't read (tiff/heic/ico…), EXIF-rotated or CMYK
    /// photos — is decoded once and handed over as a PNG when the source was lossless or
    /// has transparency, otherwise as a top-quality JPEG.
    /// </summary>
    private static (string Path, float WidthPt, float HeightPt) PreparePdfPage(string input, string tempDir, int index)
    {
        var ext = FormatRegistry.GetExtension(input);
        var header = new MagickImageInfo(input);
        bool upright = header.Orientation is Orientation.Undefined or Orientation.TopLeft;
        bool embeddable = ext is "jpg" or "jpeg" or "png" && upright && header.ColorSpace != ColorSpace.CMYK;

        string pagePath = input;
        uint width = header.Width, height = header.Height;
        if (!embeddable)
        {
            using var image = ImageConversion.LoadStill(input, ext);
            bool keepExact = ImageConversion.IsLosslessSource(ext) || image.HasAlpha;
            string pageFormat = keepExact ? "png" : "jpg";
            ImageConversion.Normalize(image, pageFormat, strip: false);

            // a throw-away intermediate: encode it fast, the PDF writer stores or recompresses it anyway
            var intermediate = new ConversionOptions { Speed = SpeedPreset.Fastest };
            ImageConversion.ApplyEncoderSettings(image, pageFormat, keepExact, intermediate, sizeTargeted: false);

            pagePath = Path.Combine(tempDir, $"page-{index:D4}.{pageFormat}");
            image.Write(pagePath, ImageConversion.ToFormat(pageFormat));
            width = image.Width;
            height = image.Height;
        }

        // 96 dpi pixels → points; keep pages inside PDF limits and above a readable minimum.
        double wPt = width * 72.0 / 96.0;
        double hPt = height * 72.0 / 96.0;
        double scale = Math.Min(1.0, 14000.0 / Math.Max(wPt, hPt));
        if (Math.Max(wPt, hPt) < 144) scale = 144.0 / Math.Max(Math.Max(wPt, hPt), 1);
        return (pagePath, (float)(wPt * scale), (float)(hPt * scale));
    }

    // ---------- images → one animated GIF (slideshow) ----------

    private static void ImagesToGif(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        ImageEngine.EnsureInitialized();

        // GIF frames are stored nearly uncompressed, so slides are bounded to 1280 px; the size slider
        // scales that down. Each of these has a precise setting that takes its place.
        var detail = options.Detail;
        int pct = options.HasSizeTarget ? options.SizePercent : 100;
        double maxSide = 1280 * Math.Clamp(Math.Sqrt(pct / 100.0), 0.35, 1.0);
        uint colors = (uint)(detail.Int(K.Colors, 2, 256) ?? (pct < 50 ? 128 : 256));
        // GIF times frames in hundredths of a second
        uint delay = (uint)Math.Clamp(Math.Round((detail.Number(K.FrameSeconds) ?? 1.0) * 100), 2, 6000);
        uint iterations = detail.Is(K.Loop, "once") ? 1u : 0u; // 0 = loop forever

        var slides = new MagickImage?[inputs.Count];
        try
        {
            // the first picture defines the canvas every other slide is fitted onto
            slides[0] = LoadSlide(inputs[0], maxSide, detail);
            uint canvasW = slides[0]!.Width, canvasH = slides[0]!.Height;

            int loaded = 0;
            ForEachIndex(inputs.Count, ct, i =>
            {
                var img = slides[i] ??= LoadSlide(inputs[i], maxSide, detail);
                if (i > 0)
                {
                    // uniform canvas: fit, then pad onto white so mixed sizes line up
                    img.Resize(new MagickGeometry(canvasW, canvasH));
                    img.BackgroundColor = MagickColors.White;
                    img.Extent(canvasW, canvasH, Gravity.Center);
                }
                // Each slide gets its own 256-colour palette: one palette shared by
                // unrelated photos would band and posterise all of them.
                img.Quantize(new QuantizeSettings { Colors = colors });
                img.AnimationDelay = delay;
                img.AnimationIterations = iterations;
                progress?.Report(0.8 * Interlocked.Increment(ref loaded) / inputs.Count);
            });

            using var frames = new MagickImageCollection();
            for (int i = 0; i < slides.Length; i++)
            {
                frames.Add(slides[i]!); // collection now owns the image
                slides[i] = null;
            }
            progress?.Report(0.85);
            frames.Write(output, MagickFormat.Gif);
            progress?.Report(1.0);
        }
        finally
        {
            foreach (var slide in slides) slide?.Dispose();
        }
    }

    private static MagickImage LoadSlide(string input, double maxSide, DetailSettings detail)
    {
        var img = ImageConversion.LoadStill(input, FormatRegistry.GetExtension(input));
        ImageConversion.Normalize(img, "gif", strip: false);
        if (detail.Has(K.MaxWidth) || detail.Has(K.MaxHeight))
            ImageConversion.ApplyResize(img, detail);
        else if (Math.Max(img.Width, img.Height) > maxSide)
            img.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide));
        return img;
    }

    // ---------- images → one multi-page TIFF ----------

    /// <summary>
    /// Every picture is compressed as a TIFF of its own, all at once, and the files are joined
    /// (<see cref="TiffFiles"/>) — written as one collection, the pages would be compressed one
    /// after the other (11 s against 1 s for 26 photos).
    /// </summary>
    private static void ImagesToTiff(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        ImageEngine.EnsureInitialized();

        var pages = new byte[inputs.Count][];
        int done = 0;
        ForEachIndex(inputs.Count, ct, i =>
        {
            using var page = LoadTiffPage(inputs[i], options);
            pages[i] = page.ToByteArray(MagickFormat.Tiff);
            progress?.Report(0.9 * Interlocked.Increment(ref done) / inputs.Count);
        });

        // a result of 4 GB or more, or a page that cannot be moved: one collection, as before
        if (!TiffFiles.TryJoin(pages, output))
            ImagesToTiffInOneGo(inputs, output, options, progress, ct);
        progress?.Report(1.0);
    }

    private static MagickImage LoadTiffPage(string input, ConversionOptions options)
    {
        var img = ImageConversion.LoadStill(input, FormatRegistry.GetExtension(input));
        try
        {
            ImageConversion.Normalize(img, "tiff", strip: false);
            if (!ImageConversion.ApplyResize(img, options.Detail) && options.HasSizeTarget && options.SizePercent < 95)
                img.Resize(new Percentage(Math.Sqrt(options.SizePercent / 100.0) * 100));
            ImageConversion.ApplyEncoderSettings(img, "tiff", losslessSource: true, options, options.HasSizeTarget);
            return img;
        }
        catch
        {
            img.Dispose();
            throw;
        }
    }

    private static void ImagesToTiffInOneGo(IReadOnlyList<string> inputs, string output,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var loaded = new MagickImage?[inputs.Count];
        try
        {
            int done = 0;
            ForEachIndex(inputs.Count, ct, i =>
            {
                loaded[i] = LoadTiffPage(inputs[i], options);
                progress?.Report(0.8 * Interlocked.Increment(ref done) / inputs.Count);
            });

            using var pages = new MagickImageCollection();
            for (int i = 0; i < loaded.Length; i++)
            {
                pages.Add(loaded[i]!); // collection now owns the image
                loaded[i] = null;
            }
            pages.Write(output, MagickFormat.Tiff);
            progress?.Report(1.0);
        }
        finally
        {
            foreach (var img in loaded) img?.Dispose();
        }
    }
}
