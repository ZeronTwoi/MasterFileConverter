using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FileConverter.Core;

public sealed record VideoStream(int Index, string Codec, int Width, int Height, string PixelFormat,
    double FrameRate, string? ColorTransfer, string? ColorPrimaries, string? ColorSpace, int Rotation)
{
    /// <summary>When the first frame is shown, in seconds from the start of the file (usually 0).</summary>
    public double StartOffset { get; init; }

    /// <summary>PQ (HDR10 / Dolby Vision) or HLG — needs 10 bits and its colour tags to survive.</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";

    /// <summary>The size the picture is shown at: a phone video stored sideways is as wide as it is stored tall.</summary>
    public (int Width, int Height) DisplaySize => Rotation % 180 != 0 ? (Height, Width) : (Width, Height);

    /// <summary>GIF/PNG-style RGB or palette frames rather than YUV video.</summary>
    public bool IsRgb => PixelFormat.StartsWith("rgb") || PixelFormat.StartsWith("bgr") || PixelFormat.StartsWith("gbr") ||
                         PixelFormat.StartsWith("argb") || PixelFormat.StartsWith("abgr") || PixelFormat == "pal8";
}

public sealed record AudioStream(int Index, string Codec, string SampleFormat, int BitsPerSample,
    int SampleRate, int Channels, string? ChannelLayout)
{
    /// <summary>Decodes bit-exactly, so its bit depth is worth preserving.</summary>
    public bool IsLossless => Codec.StartsWith("pcm_") ||
        Codec is "flac" or "alac" or "ape" or "wavpack" or "tta" or "truehd" or "mlp" or "wmalossless" or "tak" or "shorten";
}

public sealed record SubtitleStream(int Index, string Codec)
{
    public bool IsText => Codec is "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "mov_text" or "text";
    public bool IsBitmap => Codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle";
}

/// <summary>Album art: a still "video" stream flagged attached_pic.</summary>
public sealed record CoverArtStream(int Index, string Codec);

public sealed record MediaInfo(double DurationSeconds, bool HasVideo, bool HasAudio, int Width, int Height)
{
    /// <summary>The main video stream (cover art excluded), if any.</summary>
    public VideoStream? Video { get; init; }
    public IReadOnlyList<AudioStream> Audio { get; init; } = [];
    public IReadOnlyList<SubtitleStream> Subtitles { get; init; } = [];

    /// <summary>Embedded album art, if any.</summary>
    public CoverArtStream? CoverArt { get; init; }
}

/// <summary>Thin process wrapper around ffmpeg/ffprobe with JSON probing and live progress parsing.</summary>
public static class FfmpegRunner
{
    public static async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct)
    {
        var ffprobe = ToolLocator.FfprobePath
            ?? throw new FriendlyException("FFmpeg is not set up yet — use \"Set up engines\" first.");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", input })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var json = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            throw new FriendlyException("Could not read media file — it may be corrupt or unsupported.");

        using var doc = JsonDocument.Parse(json);
        double duration = 0, fileStart = 0;
        string? container = null;
        if (doc.RootElement.TryGetProperty("format", out var format))
        {
            duration = ParseDouble(Str(format, "duration"));
            fileStart = ParseDouble(Str(format, "start_time"));
            container = Str(format, "format_name");
        }

        VideoStream? video = null;
        CoverArtStream? coverArt = null;
        var audio = new List<AudioStream>();
        var subtitles = new List<SubtitleStream>();

        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                int index = Int(stream, "index");
                var codec = Str(stream, "codec_name") ?? "";
                switch (Str(stream, "codec_type"))
                {
                    case "video":
                        // Cover-art streams in mp3s/m4as also report as video
                        if (stream.TryGetProperty("disposition", out var disp) && Int(disp, "attached_pic") == 1)
                        {
                            coverArt ??= new CoverArtStream(index, codec);
                            break;
                        }
                        video ??= new VideoStream(index, codec, Int(stream, "width"), Int(stream, "height"),
                            Str(stream, "pix_fmt") ?? "", ParseRate(Str(stream, "avg_frame_rate"), Str(stream, "r_frame_rate")),
                            Str(stream, "color_transfer"), Str(stream, "color_primaries"), Str(stream, "color_space"),
                            ReadRotation(stream))
                        {
                            StartOffset = Math.Max(0, ParseDouble(Str(stream, "start_time")) - fileStart)
                        };
                        break;

                    case "audio":
                        int bits = (int)ParseDouble(Str(stream, "bits_per_raw_sample"));
                        if (bits == 0) bits = Int(stream, "bits_per_sample");
                        audio.Add(new AudioStream(index, codec, Str(stream, "sample_fmt") ?? "", bits,
                            (int)ParseDouble(Str(stream, "sample_rate")), Int(stream, "channels"), Str(stream, "channel_layout")));
                        break;

                    case "subtitle":
                        subtitles.Add(new SubtitleStream(index, codec));
                        break;
                }
            }
        }

        // A raw AAC file (ADTS) has no length written in it: ffprobe guesses one from the bitrate
        // of its first frames, which is a third off, or 1.3 s for 10 s, when its start is louder
        // than the rest. The times of its packets are exact.
        if (container == "aac" && audio.Count > 0 && await LastPacketEndAsync(ffprobe, input, ct) is > 0 and var end)
            duration = end;

        return new MediaInfo(duration, video != null, audio.Count > 0, video?.Width ?? 0, video?.Height ?? 0)
        {
            Video = video,
            Audio = audio,
            Subtitles = subtitles,
            CoverArt = coverArt
        };
    }

    /// <summary>Where the last audio packet of a file ends, in seconds; 0 when that cannot be read.</summary>
    private static async Task<double> LastPacketEndAsync(string ffprobe, string input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "quiet", "-select_streams", "a:0", "-show_entries", "packet=pts_time,duration_time",
                     "-of", "csv=p=0", input })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        // a line for each packet, some 45 a second: read as they come, not held all at once
        string? line, last = null;
        while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
            if (line.Length > 0) last = line;
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || last == null) return 0;
        return last.Split(',').Sum(field => ParseDouble(field));
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : 0;

    private static double ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>"30000/1001" → 29.97; falls back to the second candidate when the first is 0/0.</summary>
    private static double ParseRate(params string?[] candidates)
    {
        foreach (var text in candidates)
        {
            var parts = text?.Split('/');
            if (parts is not { Length: 2 }) continue;
            double num = ParseDouble(parts[0]), den = ParseDouble(parts[1]);
            if (num > 0 && den > 0) return num / den;
        }
        return 0;
    }

    /// <summary>Phone videos are stored sideways with a display-matrix rotation (older files: a "rotate" tag).</summary>
    private static int ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
            foreach (var entry in sideData.EnumerateArray())
                if (entry.TryGetProperty("rotation", out var rotation) && rotation.TryGetDouble(out var degrees))
                    return (int)Math.Round(degrees);
        if (stream.TryGetProperty("tags", out var tags))
            return (int)ParseDouble(Str(tags, "rotate"));
        return 0;
    }

    /// <summary>
    /// Runs ffmpeg with the given arguments (caller supplies everything except the executable),
    /// reporting progress as a fraction of <paramref name="durationSeconds"/> mapped into
    /// [progressOffset, progressOffset + progressScale].
    /// </summary>
    public static async Task RunAsync(IEnumerable<string> arguments, double durationSeconds,
        IProgress<double>? progress, CancellationToken ct,
        double progressScale = 1.0, double progressOffset = 0.0)
    {
        var ffmpeg = ToolLocator.FfmpegPath
            ?? throw new FriendlyException("FFmpeg is not set up yet — use \"Set up engines\" first.");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList quotes each argument itself, so any file name survives intact
        foreach (var arg in new[] { "-y", "-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1" })
            psi.ArgumentList.Add(arg);
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        await using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });

        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None)) != null)
            {
                stderr.AppendLine(line);
                if (stderr.Length > 4000) stderr.Remove(0, stderr.Length - 2000);
            }
        }, CancellationToken.None);

        string? progressLine;
        while ((progressLine = await process.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
        {
            // "out_time_us=1234567" — microseconds of output produced so far
            if (durationSeconds > 0 && progressLine.StartsWith("out_time_us=") &&
                long.TryParse(progressLine.AsSpan("out_time_us=".Length), out var us))
            {
                var fraction = Math.Clamp(us / 1_000_000.0 / durationSeconds, 0, 1);
                progress?.Report(progressOffset + fraction * progressScale);
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        await stderrTask;

        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var tail = stderr.ToString().Trim();
            if (tail.Length > 600) tail = "…" + tail[^600..];
            throw new FriendlyException($"FFmpeg failed: {(tail.Length > 0 ? tail : $"exit code {process.ExitCode}")}");
        }

        progress?.Report(progressOffset + progressScale);
    }

    /// <summary>
    /// Runs a short ffmpeg command for a capability check: reports whether it succeeded,
    /// what it printed on failure, and how long it took.
    /// </summary>
    public static async Task<(bool Ok, string Error, TimeSpan Elapsed)> TryRunAsync(IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken ct)
    {
        var ffmpeg = ToolLocator.FfmpegPath;
        if (ffmpeg == null) return (false, "", TimeSpan.Zero);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-y", "-hide_banner", "-nostdin", "-v", "error" })
            psi.ArgumentList.Add(arg);
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        var watch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi)!;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                // drain both pipes so a chatty encoder can't block on a full buffer
                var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                await stdout;
                return (process.ExitCode == 0, await stderr, watch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
                ct.ThrowIfCancellationRequested();
                return (false, "timed out", watch.Elapsed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message, watch.Elapsed);
        }
    }

    /// <summary>Where first-pass and benchmark encodes send output nobody needs.</summary>
    public static string NullSink => Platform.PlatformServices.Current.NullDevice;
}
