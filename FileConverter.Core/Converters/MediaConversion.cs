using System.Globalization;
using FileConverter.Core.Settings;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Core.Converters;

/// <summary>
/// Audio/video conversions via FFmpeg: any audio↔audio, video↔video,
/// video→audio extraction, video↔gif.
///
/// At 100 % the rule is "never decode what doesn't have to be decoded": streams the target
/// container can hold are copied bit-for-bit (instant and lossless), and only the rest is
/// re-encoded at a visually lossless quality target. Below 100 % the size slider drives
/// bitrate targeting. The speed preset picks encoder presets and whether the GPU encodes.
///
/// Precise settings override these choices one at a time. Any of them that changes the
/// stream itself (format, quality, bitrate, size, resolution, frame rate) means it is
/// encoded rather than copied.
/// </summary>
public static class MediaConversion
{
    public static async Task<ConversionResult> ConvertAsync(string input, string output, ConversionOptions options,
        WorkGates gates, IProgress<double>? progress, CancellationToken ct)
    {
        var dst = FormatRegistry.GetExtension(output);
        var srcBytes = new FileInfo(input).Length;

        MediaInfo info;
        using (await gates.EnterAsync(JobWeight.Light, ct))
            info = await FfmpegRunner.ProbeAsync(input, ct);

        if (dst is "png" or "jpg")
        {
            // video → numbered frame images in a folder
            using (await gates.EnterAsync(JobWeight.Heavy, ct))
                return new ConversionResult(
                    await FrameExtraction.VideoToFramesAsync(input, output, dst, info, options, progress, ct));
        }

        if (dst is "mp3" or "wav" or "flac" or "aac" or "m4a" or "ogg" or "opus")
        {
            using (await gates.EnterAsync(JobWeight.Light, ct))
                return await ConvertToAudioAsync(input, output, dst, info, srcBytes, options, progress, ct);
        }

        if (dst == "gif")
        {
            // a clip long enough to be made in several stretches at once keeps several cores busy
            using (await gates.EnterAsync(GifStretchesFor(info, options).Count > 1 ? JobWeight.Heavy : JobWeight.Light, ct))
                return await ConvertToGifAsync(input, output, info, options, progress, ct);
        }

        return await ConvertToVideoAsync(input, output, dst, info, srcBytes, options, gates, progress, ct);
    }

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Megabytes → the bits available to a stream of the given length, leaving a little room for the container.</summary>
    private static double BitsFor(double megabytes, double headroom) => megabytes * 1024 * 1024 * 8 * headroom;

    // ---------- audio ----------

    private static async Task<ConversionResult> ConvertToAudioAsync(string input, string output, string dst,
        MediaInfo info, long srcBytes, ConversionOptions options,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (!info.HasAudio)
            throw new FriendlyException("This file has no audio track to convert.");

        var detail = options.Detail;
        var source = info.Audio[0];
        bool stripTags = detail.Is(K.Metadata, "strip");
        var head = new List<string> { "-i", input, "-map", $"0:{source.Index}" };

        // album art survives in the formats that can carry it
        if (!stripTags && dst is "mp3" or "flac" or "m4a" && info.CoverArt is { Codec: "mjpeg" or "png" } cover)
            head.AddRange(["-map", $"0:{cover.Index}", "-c:v", "copy", "-disposition:v:0", "attached_pic"]);
        if (dst == "mp3")
            head.AddRange(["-id3v2_version", "3"]);
        if (dst == "m4a")
            head.AddRange(["-movflags", "+faststart"]); // playable while it downloads
        if (stripTags)
            head.AddRange(["-map_metadata", "-1"]);

        // settings that change the samples or how they are encoded rule out a plain copy
        bool reshaped = detail.Has(K.SampleRate) || detail.Has(K.Channels) || detail.Has(K.BitDepth);
        bool customEncode = reshaped || detail.Has(K.AudioBitrate) || detail.Has(K.VbrQuality) ||
                            detail.Has(K.Compression) || detail.Has(K.TargetSizeMb);

        if (!options.HasSizeTarget && !customEncode && CanCopyAudioTo(source, dst))
        {
            try
            {
                await FfmpegRunner.RunAsync([.. head, "-c:a", "copy", output], info.DurationSeconds, progress, ct);
                return new ConversionResult(output, "lossless copy");
            }
            catch (FriendlyException)
            {
                // an unusual stream variant the target can't take as-is — encode it instead
            }
        }

        var encode = AudioEncoderArgs(dst, source, info.DurationSeconds, srcBytes, options);
        await FfmpegRunner.RunAsync([.. head, .. encode, output], info.DurationSeconds, progress, ct);
        return new ConversionResult(output, dst is "wav" or "flac" && source.IsLossless && !reshaped ? "lossless" : null);
    }

    /// <summary>Is the source's audio already in the target format, so it can be re-wrapped untouched?</summary>
    private static bool CanCopyAudioTo(AudioStream source, string dst) => dst switch
    {
        "mp3" => source.Codec == "mp3",
        "aac" => source.Codec == "aac",
        "flac" => source.Codec == "flac",
        "m4a" => source.Codec is "aac" or "alac",
        "ogg" => source.Codec == "vorbis",
        "opus" => source.Codec == "opus",
        "wav" => source.Codec is "pcm_u8" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" or "pcm_f64le",
        _ => false
    };

    private static List<string> AudioEncoderArgs(string dst, AudioStream source, double duration, long srcBytes,
        ConversionOptions options)
    {
        var detail = options.Detail;
        var args = new List<string>();
        bool lossless = dst is "wav" or "flac";

        // In order of precedence: an exact size, a chosen bitrate, the size slider.
        string? targetBitrate = null;
        if (!lossless)
        {
            if (detail.Number(K.TargetSizeMb) is { } megabytes and > 0 && duration > 0)
                targetBitrate = $"{Math.Clamp((int)(BitsFor(megabytes, 0.97) / duration / 1000.0), 8, 640)}k";
            else if (detail.Int(K.AudioBitrate, 8, 640) is { } chosen)
                targetBitrate = $"{chosen}k";
            else if (options.HasSizeTarget && duration > 0 && !detail.Has(K.VbrQuality))
            {
                // Target bitrate so the output lands near SizePercent of the source size.
                var targetBits = srcBytes * 8.0 * options.SizePercent / 100.0 * 0.97;
                targetBitrate = $"{Math.Clamp((int)(targetBits / duration / 1000.0), 32, 320)}k";
            }
        }

        switch (dst)
        {
            case "mp3":
                args.AddRange(["-c:a", "libmp3lame"]);
                // V0 is LAME's transparent variable bitrate
                args.AddRange(targetBitrate != null
                    ? ["-b:a", targetBitrate]
                    : ["-q:a", (detail.Int(K.VbrQuality, 0, 9) ?? 0).ToString()]);
                if (source.Channels > 2 && !detail.Has(K.Channels)) args.AddRange(["-ac", "2"]);
                break;

            case "aac":
            case "m4a": // the same AAC sound, in the MP4 container players and phones expect
                args.AddRange(["-c:a", "aac", "-b:a", targetBitrate ?? $"{TransparentAacKbps(source.Channels)}k"]);
                break;

            case "opus":
                // transparent for music at about 80 kbps per channel; Opus always works at 48 kHz
                int opusChannels = ChannelCount(detail) ?? source.Channels;
                args.AddRange(["-c:a", "libopus", "-b:a", targetBitrate ?? $"{Math.Clamp(opusChannels * 80, 64, 510)}k"]);
                // libopus has no "5.1(side)" layout; relabel it as plain 5.1
                if (source.ChannelLayout == "5.1(side)" && !detail.Has(K.Channels))
                    args.AddRange(["-af", "aformat=channel_layouts=5.1"]);
                break;

            case "ogg":
                args.AddRange(["-c:a", "libvorbis"]);
                args.AddRange(targetBitrate != null
                    ? ["-b:a", targetBitrate]
                    : ["-q:a", Num(Math.Clamp(detail.Number(K.VbrQuality) ?? 8, -1, 10))]);
                break;

            case "flac":
                args.AddRange(["-c:a", "flac", "-compression_level", (detail.Int(K.Compression, 0, 12) ?? options.Speed switch
                {
                    SpeedPreset.Fastest => 2,
                    SpeedPreset.BestQuality => 8,
                    _ => 5
                }).ToString()]);
                switch (detail.Text(K.BitDepth))
                {
                    case "16": args.AddRange(["-sample_fmt", "s16"]); break;
                    case "24": args.AddRange(["-sample_fmt", "s32", "-bits_per_raw_sample", "24"]); break;
                    default:
                        // a lossy source decodes to float; 16 bits already exceed what it contains
                        if (!source.IsLossless) args.AddRange(["-sample_fmt", "s16"]);
                        break;
                }
                break;

            case "wav":
                args.AddRange(["-c:a", detail.Text(K.BitDepth) switch
                {
                    "16" => "pcm_s16le",
                    "24" => "pcm_s24le",
                    "32" => "pcm_f32le",
                    _ => WavCodecFor(source)
                }]);
                break;

            default:
                throw new FriendlyException($"Unsupported audio format: {dst}");
        }

        if (dst != "opus" && detail.Int(K.SampleRate, 4000, 384000) is { } rate) args.AddRange(["-ar", rate.ToString()]);
        if (ChannelCount(detail) is { } channels) args.AddRange(["-ac", channels.ToString()]);
        return args;
    }

    private static int? ChannelCount(DetailSettings detail) => detail.Text(K.Channels) switch
    {
        "mono" => 1,
        "stereo" => 2,
        _ => null
    };

    /// <summary>PCM flavour that holds the source's samples exactly (24-bit stays 24-bit, float stays float).</summary>
    private static string WavCodecFor(AudioStream source)
    {
        if (!source.IsLossless) return "pcm_s16le";
        return source.SampleFormat switch
        {
            "u8" or "u8p" => "pcm_u8",
            "s16" or "s16p" => "pcm_s16le",
            "flt" or "fltp" => "pcm_f32le",
            "dbl" or "dblp" => "pcm_f64le",
            _ => source.BitsPerSample is > 0 and <= 24 ? "pcm_s24le" : "pcm_s32le"
        };
    }

    /// <summary>AAC bitrate at which the native encoder is transparent: 256k stereo, more for surround.</summary>
    private static int TransparentAacKbps(int channels) =>
        Math.Clamp(Math.Min(channels, 2) * 128 + Math.Max(channels - 2, 0) * 48, 128, 640);

    // ---------- gif ----------

    /// <summary>Stretches of a clip turned into GIF at the same time, at most; each keeps about one core busy.</summary>
    private static int GifWorkers => Math.Clamp(Environment.ProcessorCount / 2, 1, 12);

    /// <summary>A stretch shorter than this many frames is not worth a process of its own.</summary>
    private const int MinGifStretchFrames = 24;

    /// <summary>For tests: caps the number of stretches (1 = the whole clip in one go).</summary>
    internal static int? GifStretchLimit { get; set; }

    /// <summary>One stretch of the clip: which frames of the GIF it makes, and where FFmpeg starts reading for it.</summary>
    /// <param name="Frames">How many frames it is expected to make.</param>
    /// <param name="Seek">Position in the file to start decoding at, in seconds; null for the beginning.</param>
    /// <param name="Skip">Frames between that position and the stretch's first frame.</param>
    /// <param name="ToEnd">The last stretch: it runs to the end of the clip, however many frames that turns out to be.</param>
    internal sealed record GifStretch(int FirstFrame, int Frames, double? Seek, int Skip, bool ToEnd);

    /// <summary>
    /// Divides a GIF of <paramref name="fps"/> frames per second into stretches of equal length.
    /// Every stretch but the first starts decoding about a second early (FFmpeg seeks to the
    /// picture it can start from, at or before that point) and drops what lies before its first
    /// frame, so all of them sample the clip on the same grid of frame times and join seamlessly.
    /// Returns nothing when the clip is too short to be worth dividing.
    /// </summary>
    internal static List<GifStretch> PlanGifStretches(double durationSeconds, double fps, double startOffset, int workers)
    {
        int total = (int)Math.Floor(durationSeconds * fps);
        int count = Math.Min(workers, total / MinGifStretchFrames);
        if (count < 2) return [];

        // A GIF times its frames in hundredths of a second. Stretches that last a whole number of
        // them (three frames at 30 per second) join without a rounding step in the timing.
        int unit = 1;
        for (int frames = 1; frames <= MinGifStretchFrames / 2; frames++)
        {
            double hundredths = frames * 100.0 / fps;
            if (Math.Abs(hundredths - Math.Round(hundredths)) > 1e-6) continue;
            unit = frames;
            break;
        }
        int Boundary(int k) => k >= count ? total : (int)Math.Round((double)total * k / count / unit) * unit;

        var stretches = new List<GifStretch>(count);
        for (int k = 0; k < count; k++)
        {
            int first = Boundary(k), next = Boundary(k + 1);
            bool last = k == count - 1;
            if (k == 0)
            {
                stretches.Add(new GifStretch(0, next, null, 0, last));
                continue;
            }
            int lead = Math.Min(first, (int)Math.Ceiling(fps));
            stretches.Add(new GifStretch(first, next - first, startOffset + (first - lead) / fps, lead, last));
        }
        return stretches;
    }

    /// <summary>The stretches a clip would be divided into, with this job's frame rate.</summary>
    private static List<GifStretch> GifStretchesFor(MediaInfo info, ConversionOptions options)
    {
        if (info.Video is not { } video || info.DurationSeconds <= 0) return [];
        return PlanGifStretches(info.DurationSeconds, GifFrameRate(video, options), video.StartOffset,
            Math.Min(GifWorkers, GifStretchLimit ?? int.MaxValue));
    }

    private static double GifFrameRate(VideoStream video, ConversionOptions options)
    {
        int pct = options.HasSizeTarget ? options.SizePercent : 100;
        double sourceFps = video.FrameRate > 0 ? video.FrameRate : 25;
        return Math.Min(sourceFps, options.Detail.Number(K.Fps) is { } chosenFps
            ? Math.Clamp(chosenFps, 1, 50)
            : pct >= 100 ? 30 : pct >= 70 ? 25 : pct >= 40 ? 15 : 10);
    }

    private static async Task<ConversionResult> ConvertToGifAsync(string input, string output, MediaInfo info,
        ConversionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var video = info.Video
            ?? throw new FriendlyException("This file has no video track to turn into a GIF.");
        var detail = options.Detail;

        // GIF stores every frame nearly uncompressed (a 20 s 1080p clip is ~400 MB), so even 100 %
        // is bounded: up to 960 px on the long side at up to 30 fps — a sharp, smooth GIF that is
        // still shareable. The size slider scales both down from there; precise settings replace them.
        const int maxSide = 960;
        int pct = options.HasSizeTarget ? options.SizePercent : 100;
        double fps = GifFrameRate(video, options);
        int colors = detail.Int(K.Colors, 2, 256) ?? (pct >= 70 ? 256 : pct >= 40 ? 128 : 64);

        var (displayWidth, displayHeight) = video.DisplaySize;
        double scale;
        if (detail.Int(K.MaxWidth, 16, 4096) is { } maxWidth)
        {
            scale = Math.Min(1.0, (double)maxWidth / Math.Max(displayWidth, 1));
        }
        else
        {
            scale = Math.Min(1.0, (double)maxSide / Math.Max(Math.Max(displayWidth, displayHeight), 1));
            if (pct < 100) scale *= Math.Clamp(Math.Sqrt(pct / 100.0), 0.3, 1.0);
        }

        // Ordered (bayer) dither is the right default for moving pictures: error diffusion takes
        // ~5× longer and its frame-to-frame noise makes the file ~40 % bigger. "Best quality" opts into it.
        string dither = detail.Text(K.Dither) switch
        {
            "bayer" => "bayer:bayer_scale=3",
            "sierra" => "sierra2_4a",
            "none" => "none",
            _ => options.Speed == SpeedPreset.BestQuality ? "sierra2_4a" : "bayer:bayer_scale=3"
        };
        // -loop 0 repeats forever, -1 plays once
        string loop = detail.Is(K.Loop, "once") ? "-1" : "0";

        // what every frame goes through after it has been picked: HDR → SDR, then the resize
        var look = new List<string>();
        if (video.IsHdr) look.Add(MediaFilters.TonemapToSdr(video));
        if (scale < 0.999) look.Add($"scale=round(iw*{Num(scale)}):-1:flags=lanczos");
        string mapping = $"paletteuse=dither={dither}:diff_mode=rectangle";

        // Two steps (build the palette, then apply it) instead of one filter graph with a split:
        // the split has to hold every decoded frame in memory until the palette is finished.
        var palette = WorkFiles.NewPath("palette", ".png");
        try
        {
            await FfmpegRunner.RunAsync(
                ["-i", input, "-map", $"0:{video.Index}",
                    "-vf", GifPaletteFilters(video, info.DurationSeconds, fps, scale, colors),
                    "-update", "1", "-frames:v", "1", palette],
                info.DurationSeconds, progress, ct, progressScale: 0.1);

            var stretches = GifStretchesFor(info, options);
            if (stretches.Count == 0 ||
                !await GifInStretchesAsync(input, output, palette, video, stretches, fps, look, mapping, loop, progress, ct))
            {
                // a short clip — or stretches that would not join: the whole clip in one go
                progress?.Report(0.1);
                string chain = string.Join(",", [$"fps={Num(fps)}", .. look]);
                await FfmpegRunner.RunAsync(
                    ["-i", input, "-i", palette,
                        "-lavfi", $"[0:{video.Index}]{chain}[x];[x][1:v]{mapping}",
                        "-an", "-loop", loop, output],
                    info.DurationSeconds, progress, ct, progressScale: 0.9, progressOffset: 0.1);
            }
        }
        finally
        {
            try { File.Delete(palette); } catch { /* best effort */ }
        }
        return new ConversionResult(output);
    }

    /// <summary>
    /// The filters that find the clip's 256 colours. A few frames per second describe them as well
    /// as all of them; so does a fraction of each frame's pixels, picked as they are (averaging
    /// neighbours would invent colours that are not in the picture). On a one-minute 1080p clip,
    /// every third pixel each way took the step from 15 s to 1.3 s and left the finished GIF's
    /// fidelity where it was (36.48 dB either way). Short clips have few frames to learn from
    /// and are read in full.
    /// </summary>
    private static string GifPaletteFilters(VideoStream video, double duration, double fps, double scale, int colors)
    {
        const double enoughSamples = 4_000_000;
        double paletteFps = Math.Min(fps, 6);
        var (width, height) = video.DisplaySize;
        double pixels = width * scale * height * scale;
        double frames = Math.Max(1, duration * paletteFps);
        int step = 3;
        while (step > 1 && pixels / (step * step) * frames < enoughSamples) step--;

        var filters = new List<string> { $"fps={Num(paletteFps)}" };
        if (video.IsHdr) filters.Add(MediaFilters.TonemapToSdr(video));
        if (step > 1) filters.Add($"scale=round(iw*{Num(scale / step)}):-1:flags=neighbor");
        else if (scale < 0.999) filters.Add($"scale=round(iw*{Num(scale)}):-1:flags=lanczos");
        filters.Add($"palettegen=max_colors={colors}:stats_mode=full");
        return string.Join(",", filters);
    }

    /// <summary>
    /// Mapping every pixel of every frame onto the palette is the slow part of making a GIF (76 of
    /// 87 s for a one-minute clip) and FFmpeg does it on one core. So the clip is divided into
    /// stretches, each made by an FFmpeg of its own, and the results are joined
    /// (<see cref="GifFiles"/>). Returns false when the stretches could not be made or joined —
    /// the caller then makes the GIF in one go.
    /// </summary>
    private static async Task<bool> GifInStretchesAsync(string input, string output, string palette, VideoStream video,
        List<GifStretch> stretches, double fps, List<string> look, string mapping, string loop,
        IProgress<double>? progress, CancellationToken ct)
    {
        // the parts are as large as the result: they go next to it, not into the system's temp folder
        var folder = Path.Combine(Path.GetDirectoryName(output)!, $".fc-gif-{Guid.NewGuid():N}"[..16]);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            int totalFrames = stretches.Sum(stretch => stretch.Frames);
            var done = new double[stretches.Count];
            var parts = stretches.Select((_, k) => Path.Combine(folder, $"part-{k:D3}.gif")).ToList();

            var jobs = stretches.Select(async (stretch, k) =>
            {
                // the same grid of frame times in every stretch: the first frame after a seek counts from zero
                List<string> chain = stretch.Seek == null
                    ? [$"fps={Num(fps)}"]
                    : [$"fps={Num(fps)}:start_time=0", $"trim=start_frame={stretch.Skip}", "setpts=PTS-STARTPTS"];
                chain.AddRange(look);

                List<string> args = [];
                if (stretch.Seek is { } seek) args.AddRange(["-ss", seek.ToString("0.######", CultureInfo.InvariantCulture)]);
                args.AddRange(["-i", input, "-i", palette,
                    "-lavfi", $"[0:{video.Index}]{string.Join(",", chain)}[x];[x][1:v]{mapping}", "-an"]);
                if (!stretch.ToEnd) args.AddRange(["-frames:v", stretch.Frames.ToString()]);
                args.AddRange(["-loop", loop, parts[k]]);

                var share = new Progress<double>(fraction =>
                {
                    double sum;
                    lock (done)
                    {
                        done[k] = fraction * stretch.Frames;
                        sum = done.Sum();
                    }
                    progress?.Report(0.1 + 0.85 * Math.Min(1.0, sum / totalFrames));
                });
                try
                {
                    await FfmpegRunner.RunAsync(args, stretch.Frames / fps, share, stop.Token);
                }
                catch
                {
                    stop.Cancel(); // one stretch failed: the others need not finish
                    throw;
                }
            }).ToList();

            try
            {
                await Task.WhenAll(jobs);
            }
            catch (Exception ex) when (ex is FriendlyException or OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                return false;
            }

            return await Task.Run(() => GifFiles.TryJoin(parts, output, ct), ct);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------- video ----------

    private static async Task<ConversionResult> ConvertToVideoAsync(string input, string output, string dst,
        MediaInfo info, long srcBytes, ConversionOptions options, WorkGates gates,
        IProgress<double>? progress, CancellationToken ct)
    {
        var video = info.Video ?? throw new FriendlyException("This file has no video track.");
        var src = FormatRegistry.GetExtension(input);
        var detail = options.Detail;

        // ---- what the precise settings ask for ----
        var chosenCodec = ChosenCodec(detail.Text(K.VideoCodec), dst, video.IsHdr);
        int? crf = dst == "avi" ? null : detail.Int(K.Crf, 0, 63);
        int? chosenKbps = detail.Int(K.VideoBitrate, 50, 400_000);
        double? targetMegabytes = detail.Number(K.TargetSizeMb) is { } megabytes and > 0 ? megabytes : null;
        var resize = MediaFilters.FitInside(video, detail.Int(K.MaxWidth, 64, 8192), detail.Int(K.MaxHeight, 64, 8192));
        double? reducedFps = detail.Number(K.Fps) is { } limit && video.FrameRate > limit + 0.01 ? Math.Max(limit, 1) : null;
        bool noAudio = detail.Is(K.AudioCodec, "none");
        bool noSubtitles = detail.Is(K.Subtitles, "remove");

        // An exact size beats a chosen bitrate, which beats a quality level, which beats the slider.
        bool sized = info.DurationSeconds > 0 &&
                     (targetMegabytes != null || (options.HasSizeTarget && chosenKbps == null && crf == null));

        // avi carries one audio track reliably; every other target keeps them all
        IReadOnlyList<AudioStream> audio = noAudio ? [] : dst == "avi" ? info.Audio.Take(1).ToList() : info.Audio;
        var subtitles = noSubtitles
            ? (Maps: new List<string>(), Codecs: new List<string>())
            : PlanSubtitles(info.Subtitles, dst);

        var maps = new List<string> { "-i", input, "-map", $"0:{video.Index}" };
        foreach (var track in audio) maps.AddRange(["-map", $"0:{track.Index}"]);
        maps.AddRange(subtitles.Maps);

        bool isoContainer = dst is "mp4" or "mov";
        List<string> container = isoContainer ? ["-movflags", "+faststart"] : [];
        if (detail.Is(K.Metadata, "strip")) container.AddRange(["-map_metadata", "-1", "-map_chapters", "-1"]);

        // the fallback when an extra audio or subtitle track is what a muxer chokes on
        var firstAudio = audio.Take(1).ToList();
        List<string> essentialMaps = ["-i", input, "-map", $"0:{video.Index}"];
        foreach (var track in firstAudio) essentialMaps.AddRange(["-map", $"0:{track.Index}"]);
        bool hasExtras = audio.Count > 1 || subtitles.Maps.Count > 0;

        // 1) Lossless: the target container can hold the video stream as it is, and nothing asks for it to change.
        bool mustEncode = sized || crf != null || chosenKbps != null || resize != null || reducedFps != null ||
                          (chosenCodec is { } wanted && !IsAlready(video, wanted));
        if (!mustEncode && CanCopyVideoTo(video, src, dst))
        {
            // "hvc1" is the HEVC flavour Apple players insist on
            string[] tag = video.Codec == "hevc" && isoContainer ? ["-tag:v", "hvc1"] : [];

            var attempts = new List<(List<string> Maps, IReadOnlyList<AudioStream> Audio, List<string> Subtitles)>
                { (maps, audio, subtitles.Codecs) };
            if (hasExtras) attempts.Add((essentialMaps, firstAudio, []));

            using (await gates.EnterAsync(JobWeight.Light, ct))
            {
                foreach (var attempt in attempts)
                {
                    try
                    {
                        await FfmpegRunner.RunAsync(
                            [.. attempt.Maps, "-c:v", "copy", .. tag, .. AudioTrackArgs(attempt.Audio, dst, sizedKbps: null, detail),
                                .. attempt.Subtitles, .. container, output],
                            info.DurationSeconds, progress, ct);
                        if (await LooksCompleteAsync(output, info, expectAudio: attempt.Audio.Count > 0, ct))
                            return new ConversionResult(output, attempt.Audio.All(track => CopiesAudio(track, dst, null, detail))
                                ? "lossless copy (no re-encoding)"
                                : "video copied losslessly, audio re-encoded");
                    }
                    catch (FriendlyException)
                    {
                        // this stream can't be re-wrapped that way — try the next option, finally encode it
                    }
                    progress?.Report(0);
                }
            }
        }

        // 2) Re-encode the video; audio the container accepts is still copied.
        var codec = chosenCodec ?? VideoEncoding.CodecFor(dst, video.IsHdr);
        string[] hevcTag = VideoEncoding.IsHevc(codec) && isoContainer ? ["-tag:v", "hvc1"] : [];
        var filters = new VideoFilters(video, codec, resize, reducedFps);

        int? videoKbps = chosenKbps;
        int? audioKbps = null;
        if (sized)
        {
            // Size targeting: derive a video bitrate from the size asked for (exactly, or as a share of the source).
            int perTrack = audio.Count > 0 ? detail.Int(K.AudioBitrate, 16, 640) ?? 128 : 0;
            var totalBits = targetMegabytes is { } megabytes2
                ? BitsFor(megabytes2, 0.96)
                : srcBytes * 8.0 * options.SizePercent / 100.0 * 0.96;
            int kbps = (int)(totalBits / info.DurationSeconds / 1000.0) - perTrack * audio.Count;
            if (kbps < 80) { perTrack = audio.Count > 0 ? 64 : 0; kbps = Math.Max(60, kbps + 64 * audio.Count); }
            videoKbps = kbps;
            audioKbps = perTrack;
        }
        var encodeAudio = AudioTrackArgs(audio, dst, audioKbps, detail);

        // A GPU encoder that is clearly faster than this CPU is used for everything but "Best
        // quality": in quality mode it only costs file size. A size or bitrate target stays on the
        // CPU: there the GPU would cost quality instead, and it misses small targets (38 % over in
        // testing, where two-pass x264 landed within 2 %). So does a hand-picked quality level,
        // whose scale belongs to the CPU encoder.
        bool gpuWanted = options.UseGpu && videoKbps == null && crf == null && options.Speed != SpeedPreset.BestQuality;
        EncoderChoice? hardware = null;
        if (gpuWanted)
        {
            // first time on this machine the encoders are timed against each other — on a quiet CPU
            using var quiet = HardwareEncoders.IsResolved(codec) ? null : await gates.EnterAsync(JobWeight.Exclusive, ct);
            hardware = await HardwareEncoders.PickAsync(codec, options.Speed, ct);
        }

        if (hardware is { } gpu)
        {
            try
            {
                using (await gates.EnterAsync(JobWeight.Heavy, ct))
                {
                    var encoder = VideoEncoding.Arguments(gpu, options.Speed, videoKbps, video.Width);
                    await FfmpegRunner.RunAsync(
                        [.. maps, .. encoder, .. filters.Arguments(VideoEncoding.FilterSuffix(gpu)), .. hevcTag,
                            .. encodeAudio, .. subtitles.Codecs, .. container, output],
                        info.DurationSeconds, progress, ct);
                    return new ConversionResult(output, $"encoded on GPU ({HardwareEncoders.Describe(gpu.Gpu)})");
                }
            }
            catch (FriendlyException)
            {
                // resolution or format the GPU encoder rejects — the CPU encoder takes anything
                progress?.Report(0);
            }
        }

        // a CPU encoder already uses every core; sharing them only slows everything down (see WorkGates)
        using (await gates.EnterAsync(JobWeight.Exclusive, ct))
        {
            var software = new EncoderChoice(codec, GpuEncoder.None, Rich: false);
            var softwareArgs = VideoEncoding.Arguments(software, options.Speed, videoKbps, video.Width, crf);
            List<string> videoExtras = [.. filters.Arguments(), .. hevcTag];
            try
            {
                await EncodeOnCpuAsync(maps, softwareArgs, videoExtras, [.. encodeAudio, .. subtitles.Codecs, .. container],
                    output, codec, videoKbps != null, options, info, progress, ct);
            }
            catch (FriendlyException) when (hasExtras)
            {
                // an extra audio or subtitle track is what broke it — keep the essentials
                progress?.Report(0);
                await EncodeOnCpuAsync(essentialMaps, softwareArgs, videoExtras,
                    [.. AudioTrackArgs(firstAudio, dst, audioKbps, detail), .. container],
                    output, codec, videoKbps != null, options, info, progress, ct);
            }
        }
        return new ConversionResult(output);
    }

    /// <summary>The video format the user picked, when the target container can hold it.</summary>
    private static VideoCodec? ChosenCodec(string? choice, string dst, bool hdr) => (choice, dst) switch
    {
        ("h264", "mp4" or "mkv" or "mov") => VideoCodec.H264,
        ("hevc", "mp4" or "mkv" or "mov") => hdr ? VideoCodec.Hevc10Bit : VideoCodec.Hevc,
        ("vp9", "mkv" or "webm") => hdr ? VideoCodec.Vp9TenBit : VideoCodec.Vp9,
        ("av1", "mp4" or "mkv" or "webm") when VideoEncoding.Av1Encoder != null => hdr ? VideoCodec.Av1TenBit : VideoCodec.Av1,
        _ => null
    };

    /// <summary>Is the source video already in the format that was asked for?</summary>
    private static bool IsAlready(VideoStream video, VideoCodec wanted) => wanted switch
    {
        VideoCodec.H264 => video.Codec == "h264",
        VideoCodec.Hevc or VideoCodec.Hevc10Bit => video.Codec == "hevc",
        VideoCodec.Vp9 or VideoCodec.Vp9TenBit => video.Codec == "vp9",
        VideoCodec.Av1 or VideoCodec.Av1TenBit => video.Codec == "av1",
        _ => false
    };

    /// <summary>One CPU encode; two-pass when chasing a bitrate on the slower presets.</summary>
    private static async Task EncodeOnCpuAsync(List<string> maps, List<string> encoder, List<string> videoExtras,
        List<string> tail, string output, VideoCodec codec, bool bitrateTargeted, ConversionOptions options, MediaInfo info,
        IProgress<double>? progress, CancellationToken ct)
    {
        bool twoPass = bitrateTargeted && codec == VideoCodec.H264 &&
                       options.Speed is SpeedPreset.Balanced or SpeedPreset.BestQuality;
        if (!twoPass)
        {
            await FfmpegRunner.RunAsync([.. maps, .. encoder, .. videoExtras, .. tail, output],
                info.DurationSeconds, progress, ct);
            return;
        }

        var passLog = WorkFiles.NewPath("pass");
        try
        {
            // the analysis pass only needs the video stream
            await FfmpegRunner.RunAsync(
                [.. maps.Take(4), .. encoder, .. videoExtras, "-an", "-pass", "1", "-passlogfile", passLog, "-f", "null", FfmpegRunner.NullSink],
                info.DurationSeconds, progress, ct, progressScale: 0.5);
            await FfmpegRunner.RunAsync(
                [.. maps, .. encoder, .. videoExtras, "-pass", "2", "-passlogfile", passLog, .. tail, output],
                info.DurationSeconds, progress, ct, progressScale: 0.5, progressOffset: 0.5);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(passLog) + "*"))
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Can the video stream be moved into the target container without re-encoding?
    /// Deliberately conservative: only pairings that play everywhere the target format is expected to.
    /// </summary>
    private static bool CanCopyVideoTo(VideoStream video, string sourceExt, string dst)
    {
        // avi/wmv keep timestamps too loosely (packed B-frames, no pts) to re-wrap reliably
        if (sourceExt is "avi" or "wmv") return false;
        // only mp4/mov players honour a rotation flag; elsewhere the pixels have to be turned
        if (video.Rotation % 360 != 0 && dst is not ("mp4" or "mov")) return false;

        return dst switch
        {
            "mp4" => video.Codec is "h264" or "hevc" or "av1" or "mpeg4",
            "mov" => video.Codec is "h264" or "hevc" or "mpeg4" or "prores" or "mjpeg" or "dnxhd",
            "mkv" => video.Codec is "h264" or "hevc" or "av1" or "vp8" or "vp9" or "mpeg4" or "mpeg2video"
                or "mpeg1video" or "theora" or "mjpeg" or "prores" or "vc1" or "ffv1" or "dnxhd",
            "webm" => video.Codec is "vp8" or "vp9" or "av1",
            _ => false
        };
    }

    private static bool CanMuxAudio(string codec, string dst) => dst switch
    {
        "mp4" => codec is "aac" or "mp3" or "ac3" or "eac3" or "alac",
        "mov" => codec is "aac" or "mp3" or "ac3" or "eac3" or "alac" or "pcm_s16le" or "pcm_s24le",
        "mkv" => codec is "aac" or "mp3" or "mp2" or "ac3" or "eac3" or "dts" or "truehd" or "flac" or "opus"
            or "vorbis" or "alac" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le",
        "webm" => codec is "opus" or "vorbis",
        "avi" => codec is "mp3" or "mp2" or "ac3" or "pcm_s16le",
        _ => false
    };

    /// <summary>The audio format picked for a video's sound, when the container can hold it.</summary>
    private static string? ChosenAudioCodec(DetailSettings detail, string dst) => (detail.Text(K.AudioCodec), dst) switch
    {
        ("aac", "mp4" or "mov" or "mkv") => "aac",
        ("mp3", "mp4" or "mkv" or "avi") => "mp3",
        ("opus", "mkv" or "webm") => "opus",
        ("flac", "mkv") => "flac",
        _ => null
    };

    /// <summary>
    /// Is this track copied rather than encoded? Yes when the container accepts it and nothing —
    /// a size target, a chosen format it isn't already in, a bitrate, a channel count — asks for a change.
    /// </summary>
    private static bool CopiesAudio(AudioStream track, string dst, int? sizedKbps, DetailSettings detail)
    {
        if (sizedKbps != null || detail.Has(K.AudioBitrate) || ChannelCount(detail) != null) return false;
        if (!CanMuxAudio(track.Codec, dst)) return false;
        return ChosenAudioCodec(detail, dst) is not { } chosen || track.Codec == chosen;
    }

    /// <summary>
    /// Per-track audio settings: tracks the container accepts are copied, the rest are encoded at
    /// a transparent bitrate (or at <paramref name="sizedKbps"/> when a size target is set).
    /// </summary>
    private static List<string> AudioTrackArgs(IReadOnlyList<AudioStream> tracks, string dst, int? sizedKbps, DetailSettings detail)
    {
        var args = new List<string>();
        int? chosenKbps = detail.Int(K.AudioBitrate, 16, 640);
        int? channels = ChannelCount(detail);

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (CopiesAudio(track, dst, sizedKbps, detail))
            {
                args.AddRange([$"-c:a:{i}", "copy"]);
                continue;
            }

            int outputChannels = channels ?? track.Channels;
            var codec = ChosenAudioCodec(detail, dst) ?? dst switch
            {
                "webm" => "opus",
                "avi" => "mp3",
                _ => "aac"
            };
            switch (codec)
            {
                case "opus":
                    int opusKbps = sizedKbps ?? chosenKbps ?? Math.Clamp(outputChannels * 96, 96, 510);
                    args.AddRange([$"-c:a:{i}", "libopus", $"-b:a:{i}", $"{opusKbps}k"]);
                    // libopus has no "5.1(side)" layout; relabel it as plain 5.1
                    if (track.ChannelLayout == "5.1(side)" && channels == null)
                        args.AddRange([$"-filter:a:{i}", "aformat=channel_layouts=5.1"]);
                    break;

                case "mp3":
                    // constant bitrate: VBR mp3 drifts out of sync in avi
                    args.AddRange([$"-c:a:{i}", "libmp3lame", $"-b:a:{i}", $"{Math.Clamp(sizedKbps ?? chosenKbps ?? 320, 64, 320)}k"]);
                    if (track.Channels > 2 && channels == null) args.AddRange([$"-ac:a:{i}", "2"]); // mp3 is stereo at most
                    break;

                case "flac":
                    args.AddRange([$"-c:a:{i}", "flac"]);
                    break;

                default:
                    args.AddRange([$"-c:a:{i}", "aac", $"-b:a:{i}", $"{sizedKbps ?? chosenKbps ?? TransparentAacKbps(outputChannels)}k"]);
                    break;
            }
            if (channels is { } count) args.AddRange([$"-ac:a:{i}", count.ToString()]);
        }
        return args;
    }

    /// <summary>Subtitle tracks the target can carry, converted to its native text format where needed.</summary>
    private static (List<string> Maps, List<string> Codecs) PlanSubtitles(IReadOnlyList<SubtitleStream> subtitles, string dst)
    {
        var maps = new List<string>();
        var codecs = new List<string>();
        foreach (var subtitle in subtitles)
        {
            string? codec = dst switch
            {
                "mkv" => subtitle.Codec is "mov_text" or "text" ? "srt"
                    : subtitle.IsText || subtitle.IsBitmap ? "copy" : null,
                "mp4" or "mov" => subtitle.IsText ? "mov_text" : null,
                "webm" => subtitle.IsText ? "webvtt" : null,
                _ => null
            };
            if (codec == null) continue;

            codecs.AddRange([$"-c:s:{maps.Count / 2}", codec]);
            maps.AddRange(["-map", $"0:{subtitle.Index}"]);
        }
        return (maps, codecs);
    }

    /// <summary>The filter chain of one video encode: frame-rate and size limits first, then colour handling.</summary>
    private sealed class VideoFilters(VideoStream video, VideoCodec codec, string? resize, double? fps)
    {
        public List<string> Arguments(string? suffix = null)
        {
            var filters = new List<string>();
            // dropping frames and shrinking first makes every later step cheaper
            if (fps is { } rate) filters.Add($"fps={Num(rate)}");
            if (resize != null) filters.Add(resize);

            if (video.IsHdr && !VideoEncoding.IsTenBit(codec))
                filters.Add(MediaFilters.TonemapToSdr(video));
            // 4:2:0 needs even dimensions. Cropping the odd pixel row/column keeps every other pixel
            // untouched, where scaling would resample (soften) the whole frame. (A resize already rounds to even.)
            if (resize == null && (video.Width % 2 != 0 || video.Height % 2 != 0))
                filters.Add("crop=trunc(iw/2)*2:trunc(ih/2)*2");
            // RGB sources (gif) would otherwise be converted with the SD matrix and shift colour on HD players
            if (video.IsRgb)
                filters.Add("scale=out_color_matrix=bt709:out_range=tv:flags=accurate_rnd+full_chroma_int,format=yuv420p," +
                            "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709");

            // Colour tags are stamped on the frames here: the encoder takes them from the frames it is
            // given, and ffmpeg ignores the -color_trc/-color_primaries output options. HDR needs them
            // to survive, or players show the picture as washed-out SDR.
            if (video.IsHdr && VideoEncoding.IsTenBit(codec))
            {
                var tags = new List<string> { $"color_trc={video.ColorTransfer}" };
                if (video.ColorPrimaries is { Length: > 0 } and not "unknown") tags.Add($"color_primaries={video.ColorPrimaries}");
                if (video.ColorSpace is { Length: > 0 } and not "unknown") tags.Add($"colorspace={video.ColorSpace}");
                filters.Add("setparams=" + string.Join(":", tags));
            }
            if (suffix != null) filters.Add(suffix);
            return filters.Count > 0 ? ["-vf", string.Join(",", filters)] : [];
        }
    }

    /// <summary>Sanity check after a stream copy: the output must contain the video and run as long as the source.</summary>
    private static async Task<bool> LooksCompleteAsync(string output, MediaInfo source, bool expectAudio, CancellationToken ct)
    {
        try
        {
            var result = await FfmpegRunner.ProbeAsync(output, ct);
            if (!result.HasVideo) return false;
            if (expectAudio && result.Audio.Count < Math.Min(source.Audio.Count, 1)) return false;
            if (source.DurationSeconds <= 0) return true;
            return Math.Abs(result.DurationSeconds - source.DurationSeconds) <= Math.Max(1.0, source.DurationSeconds * 0.02);
        }
        catch (FriendlyException)
        {
            return false;
        }
    }
}

/// <summary>Filter snippets shared by the video, GIF and frame-extraction paths.</summary>
public static class MediaFilters
{
    /// <summary>
    /// HDR (PQ/HLG, BT.2020) → SDR BT.709 tone mapping, for targets that can only hold 8-bit SDR.
    /// Without it HDR footage comes out grey and washed-out.
    /// </summary>
    public static string TonemapToSdr(VideoStream video)
    {
        string transfer = video.ColorTransfer == "arib-std-b67" ? "arib-std-b67" : "smpte2084";
        return $"zscale=tin={transfer}:min=bt2020nc:pin=bt2020:rin=tv:t=linear:npl=100,format=gbrpf32le," +
               "zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";
    }

    /// <summary>
    /// A scale filter that shrinks the picture to fit inside the given limits, keeping its
    /// proportions and even dimensions — or null when the video already fits (it is never enlarged).
    /// </summary>
    public static string? FitInside(VideoStream video, int? maxWidth, int? maxHeight)
    {
        var (width, height) = video.DisplaySize;
        bool tooWide = maxWidth is { } w && width > w;
        bool tooTall = maxHeight is { } h && height > h;
        if (!tooWide && !tooTall) return null;

        string boxWidth = maxWidth is { } limitW ? $"min({limitW}\\,iw)" : "iw";
        string boxHeight = maxHeight is { } limitH ? $"min({limitH}\\,ih)" : "ih";
        return $"scale=w={boxWidth}:h={boxHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos";
    }
}
