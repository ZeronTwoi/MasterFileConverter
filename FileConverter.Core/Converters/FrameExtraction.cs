using System.Globalization;
using System.Runtime.ExceptionServices;
using ImageMagick;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Converters;

/// <summary>
/// Context-aware "one file → many files" conversions: animated GIF/WebP → numbered
/// image frames and video → a frame sequence (PDF → one image per page is
/// <see cref="PdfPageExport"/>). Multi-file results land in a sibling folder
/// named after the source; numbering is zero-padded so any viewer sorts it right.
/// Frames are independent of each other, so they are encoded in parallel.
/// </summary>
public static class FrameExtraction
{
    /// <summary>Worker count for per-frame encoding inside one job.</summary>
    private static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 1, 12);

    /// <summary>"anim.png" → new folder "anim frames" (deduped) next to it.</summary>
    internal static string CreateOutputFolder(string outputFilePath, string suffix)
    {
        var dir = Path.GetDirectoryName(outputFilePath)!;
        var baseName = Path.GetFileNameWithoutExtension(ConversionService.FinalName(outputFilePath));
        var candidate = Path.Combine(dir, $"{baseName} {suffix}");
        for (int i = 1; Directory.Exists(candidate) || File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{baseName} {suffix} ({i})");
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    internal static void DeleteFolderQuiet(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
    }

    // ---------- animated gif/webp → image frames ----------

    /// <summary>Writes every frame of an animated image as a numbered still; returns the folder.</summary>
    public static string AnimatedImageToFrames(string input, string outputFilePath, string src, string dst,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        using var frames = new MagickImageCollection(input);
        frames.Coalesce();

        var folder = CreateOutputFolder(outputFilePath, "frames");
        try
        {
            var detail = options.Detail;
            var baseName = Path.GetFileNameWithoutExtension(outputFilePath);
            int digits = Math.Max(2, frames.Count.ToString().Length);
            // an explicit quality replaces the slider, as it does for single pictures
            bool sized = options.HasSizeTarget && !detail.Has(K.Quality);
            var list = frames.ToList();
            int done = 0;

            Parallel.For(0, list.Count, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = ct }, i =>
            {
                var frame = (MagickImage)list[i];
                ImageConversion.Normalize(frame, dst, ImageConversion.StripMetadata(detail, sizeTargeted: false), detail);
                bool resized = ImageConversion.ApplyResize(frame, detail);
                if (sized && !resized && options.SizePercent < 95)
                    frame.Resize(new Percentage(Math.Sqrt(options.SizePercent / 100.0) * 100));
                ImageConversion.ApplyEncoderSettings(frame, dst, ImageConversion.IsLosslessSource(src), options, sized);
                if (sized && dst is "jpg" or "webp") frame.Quality = 85;

                ImageConversion.Save(frame, Path.Combine(folder, $"{baseName} {(i + 1).ToString($"D{digits}")}.{dst}"), dst);
                progress?.Report((double)Interlocked.Increment(ref done) / list.Count);
            });
            return folder;
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            DeleteFolderQuiet(folder);
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            throw;
        }
        catch
        {
            DeleteFolderQuiet(folder);
            throw;
        }
    }

    // ---------- video → image frames ----------

    /// <summary>
    /// Extracts a frame sequence at full resolution. Short clips keep every frame (up to 30 fps);
    /// longer videos are sampled so the result stays near ~300 images (a 1 h film → 1 frame every 12 s).
    /// The rate, a frame limit, the JPEG quality and a maximum size can each be set precisely.
    /// </summary>
    public static async Task<string> VideoToFramesAsync(string input, string outputFilePath, string dst,
        MediaInfo info, ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var video = info.Video
            ?? throw new FriendlyException("This file has no video track to extract frames from.");
        var detail = options.Detail;

        double fps = detail.Number(K.Fps) is { } chosen and > 0
            ? chosen
            : info.DurationSeconds > 0 ? Math.Min(30.0, 300.0 / info.DurationSeconds) : 1.0;
        if (video.FrameRate > 0) fps = Math.Min(fps, video.FrameRate); // never invent duplicate frames

        int pct = options.HasSizeTarget ? options.SizePercent : 100;
        var filters = new List<string>();
        if (video.IsHdr) filters.Add(MediaFilters.TonemapToSdr(video));
        filters.Add($"fps={fps.ToString("0.####", CultureInfo.InvariantCulture)}");
        if (MediaFilters.FitInside(video, detail.Int(K.MaxWidth, 16, 16384), detail.Int(K.MaxHeight, 16, 16384)) is { } fit)
        {
            filters.Add(fit);
        }
        else if (pct < 90 && !detail.Has(K.MaxWidth) && !detail.Has(K.MaxHeight))
        {
            var factor = Math.Sqrt(pct / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
            filters.Add($"scale=trunc(iw*{factor}/2)*2:-2:flags=lanczos");
        }

        var folder = CreateOutputFolder(outputFilePath, "frames");
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(outputFilePath);
            // ffmpeg reads the output name as a printf pattern, so a literal % in the file name must be doubled
            var pattern = Path.Combine(folder, baseName).Replace("%", "%%") + $" %04d.{dst}";

            var args = new List<string> { "-i", input, "-map", $"0:{video.Index}", "-vf", string.Join(",", filters) };
            if (dst == "jpg")
            {
                // mjpeg quantiser: 1 = best the encoder can do, 31 = worst
                int q = detail.Int(K.Quality, 1, 100) is { } quality
                    ? (int)Math.Round(1 + (100 - quality) * 30 / 99.0)
                    : pct >= 100 ? 1 : Math.Clamp(2 + (100 - pct) / 12, 2, 12);
                args.AddRange(["-q:v", q.ToString(), "-qmin", "1"]);
            }
            if (detail.Int(K.MaxFrames, 1, 100_000) is { } maxFrames)
                args.AddRange(["-frames:v", maxFrames.ToString()]);
            args.Add(pattern);
            await FfmpegRunner.RunAsync(args, info.DurationSeconds, progress, ct);

            if (Directory.GetFiles(folder).Length == 0)
                throw new FriendlyException("No frames could be extracted from this video.");
            return folder;
        }
        catch
        {
            DeleteFolderQuiet(folder);
            throw;
        }
    }
}
