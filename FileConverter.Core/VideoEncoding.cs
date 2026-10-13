using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FileConverter.Core.Platform;

namespace FileConverter.Core;

public enum GpuEncoder
{
    None,
    Nvidia,
    Intel,
    Amd,
    /// <summary>The Linux video acceleration API, which AMD and Intel graphics drivers implement.</summary>
    Vaapi
}

/// <summary>What a video re-encode produces.</summary>
public enum VideoCodec
{
    H264,
    /// <summary>HEVC Main, 8-bit — when H.265 is chosen for ordinary (SDR) video.</summary>
    Hevc,
    /// <summary>HEVC Main10 — used when the source is HDR, so its 10 bits and colour tags survive.</summary>
    Hevc10Bit,
    Vp9,
    Vp9TenBit,
    Av1,
    Av1TenBit,
    Mpeg4
}

/// <summary>A concrete encoder for one job: which GPU (or none) and whether its richer rate-control options work.</summary>
public readonly record struct EncoderChoice(VideoCodec Codec, GpuEncoder Gpu, bool Rich)
{
    public bool IsHardware => Gpu != GpuEncoder.None;
}

/// <summary>
/// Encoder command lines. "Quality mode" (no bitrate) is what the 100 % slider position uses:
/// a constant-quality target at which a re-encode is visually indistinguishable from its
/// source. The quality numbers differ per encoder and per preset on purpose — they were
/// matched by measurement (PSNR/SSIM against the source) so that every speed preset lands
/// on the same fidelity and only encode time and file size change between them.
/// </summary>
public static class VideoEncoding
{
    public static VideoCodec CodecFor(string targetExt, bool hdr) => targetExt switch
    {
        "webm" => hdr ? VideoCodec.Vp9TenBit : VideoCodec.Vp9,
        "avi" => VideoCodec.Mpeg4,
        _ => hdr ? VideoCodec.Hevc10Bit : VideoCodec.H264
    };

    public static bool IsTenBit(VideoCodec codec) => codec is VideoCodec.Hevc10Bit or VideoCodec.Vp9TenBit or VideoCodec.Av1TenBit;

    public static bool IsHevc(VideoCodec codec) => codec is VideoCodec.Hevc or VideoCodec.Hevc10Bit;

    /// <summary>
    /// The AV1 encoder this FFmpeg build has, preferring SVT-AV1 (many times faster than libaom
    /// at the same quality), or null when it can't encode AV1 at all.
    /// </summary>
    public static string? Av1Encoder =>
        FfmpegCapabilities.HasEncoder("libsvtav1") ? "libsvtav1"
        : FfmpegCapabilities.HasEncoder("libaom-av1") ? "libaom-av1"
        : null;

    /// <summary>
    /// ffmpeg arguments selecting and configuring the video encoder. A bitrate (size-targeted
    /// encode) or a hand-picked quality level is only meaningful for the CPU encoders — GPU
    /// encoders are used in their own calibrated quality mode only.
    /// </summary>
    /// <param name="crf">A constant-quality level chosen by the user; null for the measured default.</param>
    public static List<string> Arguments(EncoderChoice choice, SpeedPreset speed, int? bitrateKbps, int width, int? crf = null) =>
        choice.Gpu switch
        {
            GpuEncoder.Nvidia => Nvenc(choice, speed),
            GpuEncoder.Intel => QuickSync(choice, speed),
            GpuEncoder.Amd => Amf(speed),
            GpuEncoder.Vaapi => Vaapi(),
            _ => Software(choice.Codec, speed, bitrateKbps, width, crf)
        };

    /// <summary>
    /// Filters an encoder needs at the end of the chain. VA-API encodes frames that live on the
    /// graphics card, so ordinary frames are converted to its pixel layout and uploaded first.
    /// </summary>
    public static string? FilterSuffix(EncoderChoice choice) =>
        choice.Gpu == GpuEncoder.Vaapi ? "format=nv12,hwupload" : null;

    private static string[] Bitrate(int kbps) =>
        ["-b:v", $"{kbps}k", "-maxrate", $"{(int)(kbps * 1.5)}k", "-bufsize", $"{kbps * 2}k"];

    private static List<string> Software(VideoCodec codec, SpeedPreset speed, int? kbps, int width, int? chosenCrf)
    {
        var args = new List<string>();
        switch (codec)
        {
            case VideoCodec.H264:
                // Faster x264 presets lose a little fidelity at the same CRF, so each gets the CRF
                // that brings it back level: all four land at ~50–51 dB PSNR on the reference clip,
                // at roughly the source's own size (only "superfast" is ~50 % larger).
                // ultrafast is avoided: it disables deblocking and adaptive quantisation, which no
                // CRF makes up for.
                var (preset, crf) = speed switch
                {
                    SpeedPreset.Fastest => ("superfast", "19"),
                    SpeedPreset.Fast => ("veryfast", "15"),
                    SpeedPreset.Balanced => ("faster", "17"),
                    _ => ("slow", "18")
                };
                // At a fixed size the preset decides the quality instead of the file size, so the
                // balanced choice moves one notch towards compression efficiency.
                if (kbps != null && speed == SpeedPreset.Balanced) preset = "medium";
                // a hand-picked level is taken literally — it is the user's number, not ours to rebalance
                if (chosenCrf is { } pickedCrf) crf = Math.Clamp(pickedCrf, 0, 51).ToString();
                args.AddRange(["-c:v", "libx264", "-preset", preset]);
                args.AddRange(kbps is { } h264Rate ? Bitrate(h264Rate) : ["-crf", crf]);
                args.AddRange(["-pix_fmt", "yuv420p"]);
                break;

            case VideoCodec.Hevc:
            case VideoCodec.Hevc10Bit:
                args.AddRange(["-c:v", "libx265", "-preset", speed switch
                {
                    SpeedPreset.Fastest => "ultrafast",
                    SpeedPreset.Fast => "superfast",
                    SpeedPreset.Balanced => "veryfast",
                    _ => "medium"
                }]);
                args.AddRange(kbps is { } hevcRate ? Bitrate(hevcRate) : ["-crf", Math.Clamp(chosenCrf ?? 18, 0, 51).ToString()]);
                args.AddRange(["-pix_fmt", codec == VideoCodec.Hevc10Bit ? "yuv420p10le" : "yuv420p", "-x265-params", "log-level=error"]);
                break;

            case VideoCodec.Av1:
            case VideoCodec.Av1TenBit:
                // AV1 is chosen for small files, so its default level sits where it is still clean
                // rather than at "indistinguishable"; that is what the Quality setting is for.
                string av1Crf = Math.Clamp(chosenCrf ?? 26, 0, 63).ToString();
                if (Av1Encoder == "libsvtav1")
                {
                    args.AddRange(["-c:v", "libsvtav1", "-preset", speed switch
                    {
                        SpeedPreset.Fastest => "12",
                        SpeedPreset.Fast => "10",
                        SpeedPreset.Balanced => "8",
                        _ => "5"
                    }]);
                    args.AddRange(kbps is { } svtRate ? ["-b:v", $"{svtRate}k"] : ["-crf", av1Crf]);
                }
                else
                {
                    // libaom is slow by nature; row-based threading and its fastest usable modes keep it practical
                    args.AddRange(["-c:v", "libaom-av1", "-row-mt", "1", "-tiles", width >= 1200 ? "2x2" : "1x1", "-cpu-used", speed switch
                    {
                        SpeedPreset.Fastest => "8",
                        SpeedPreset.Fast => "7",
                        SpeedPreset.Balanced => "6",
                        _ => "4"
                    }]);
                    args.AddRange(kbps is { } aomRate ? ["-b:v", $"{aomRate}k"] : ["-crf", av1Crf, "-b:v", "0"]);
                }
                args.AddRange(["-pix_fmt", codec == VideoCodec.Av1TenBit ? "yuv420p10le" : "yuv420p"]);
                break;

            case VideoCodec.Vp9:
            case VideoCodec.Vp9TenBit:
                // row-mt + tiles let libvpx use more than one core. Its "realtime" mode reaches the
                // same fidelity as "good" roughly ten times faster for ~50 % larger files, so the
                // slow mode is kept for "Best quality" only.
                int tileColumns = width >= 3000 ? 3 : width >= 1200 ? 2 : width >= 600 ? 1 : 0;
                args.AddRange(["-c:v", "libvpx-vp9", "-row-mt", "1", "-tile-columns", tileColumns.ToString()]);
                var (deadline, cpuUsed, vp9Crf) = speed switch
                {
                    SpeedPreset.Fastest => ("realtime", "8", "21"),
                    SpeedPreset.Fast => ("realtime", "6", "21"),
                    SpeedPreset.Balanced => ("realtime", "5", "21"),
                    _ => ("good", "2", "20")
                };
                args.AddRange(["-deadline", deadline, "-cpu-used", cpuUsed]);
                if (chosenCrf is { } pickedVp9Crf) vp9Crf = Math.Clamp(pickedVp9Crf, 0, 63).ToString();
                // size target: constant bitrate — libvpx's one-pass VBR overshoots (23 % over in testing)
                args.AddRange(kbps is { } vp9Rate
                    ? ["-b:v", $"{vp9Rate}k", "-minrate", $"{vp9Rate}k", "-maxrate", $"{vp9Rate}k"]
                    : ["-crf", vp9Crf, "-b:v", "0"]);
                args.AddRange(["-pix_fmt", codec == VideoCodec.Vp9TenBit ? "yuv420p10le" : "yuv420p"]);
                break;

            case VideoCodec.Mpeg4:
                args.AddRange(["-c:v", "mpeg4", "-vtag", "xvid"]);
                args.AddRange(kbps is { } mpeg4Rate ? Bitrate(mpeg4Rate) : ["-q:v", "3"]);
                args.AddRange(["-pix_fmt", "yuv420p"]);
                break;
        }
        return args;
    }

    // Quick Sync was measured: it needs a quality number ~2 lower than x264's CRF for the same
    // fidelity. The NVENC and AMF values are set by analogy and have not been calibrated on real
    // hardware yet.

    private static List<string> Nvenc(EncoderChoice choice, SpeedPreset speed)
    {
        bool hevc = IsHevc(choice.Codec);
        var args = new List<string>
        {
            "-c:v", hevc ? "hevc_nvenc" : "h264_nvenc",
            "-preset", speed switch { SpeedPreset.Fastest => "p1", SpeedPreset.Fast => "p4", _ => "p6" },
            "-tune", "hq", "-rc", "vbr", "-cq", hevc ? "20" : "18", "-b:v", "0"
        };
        // look-ahead and adaptive quantisation are not available on every GPU generation
        if (choice.Rich) args.AddRange(["-rc-lookahead", "20", "-spatial-aq", "1", "-temporal-aq", "1"]);
        args.AddRange(choice.Codec switch
        {
            VideoCodec.Hevc10Bit => ["-profile:v", "main10", "-pix_fmt", "p010le"],
            VideoCodec.Hevc => ["-profile:v", "main", "-pix_fmt", "yuv420p"],
            _ => ["-profile:v", "high", "-pix_fmt", "yuv420p"]
        });
        return args;
    }

    private static List<string> QuickSync(EncoderChoice choice, SpeedPreset speed)
    {
        bool hevc = IsHevc(choice.Codec);
        List<string> args =
        [
            "-c:v", hevc ? "hevc_qsv" : "h264_qsv",
            "-preset", speed switch { SpeedPreset.Fastest => "veryfast", SpeedPreset.Fast => "faster", _ => "medium" },
            "-global_quality", hevc ? "17" : "15"
        ];
        args.AddRange(choice.Codec switch
        {
            VideoCodec.Hevc10Bit => ["-profile:v", "main10", "-pix_fmt", "p010le"],
            VideoCodec.Hevc => ["-profile:v", "main", "-pix_fmt", "nv12"],
            _ => ["-pix_fmt", "nv12"]
        });
        return args;
    }

    private static List<string> Amf(SpeedPreset speed) =>
    [
        "-c:v", "h264_amf",
        "-quality", speed switch { SpeedPreset.Fastest => "speed", SpeedPreset.Fast => "balanced", _ => "quality" },
        "-rc", "cqp", "-qp_i", "18", "-qp_p", "18", "-qp_b", "20",
        "-pix_fmt", "yuv420p"
    ];

    /// <summary>
    /// Constant-quantiser H.264 through VA-API. The device option is the platform's (which render
    /// node); the quality value is set by analogy with the other hardware encoders and has not
    /// been calibrated on real hardware yet.
    /// </summary>
    private static List<string> Vaapi() =>
    [
        .. PlatformServices.Current.EncoderDeviceArguments(GpuEncoder.Vaapi),
        "-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", "19"
    ];
}

/// <summary>
/// What this machine's GPU can do for video encoding, for display in the UI. Both speeds are
/// 1080p frames per second; <paramref name="CpuFps"/> is what the CPU is expected to sustain.
/// </summary>
public sealed record GpuReport(GpuEncoder Gpu, double GpuFps, double CpuFps, string? Hint)
{
    public bool Usable => Gpu != GpuEncoder.None;

    /// <summary>Is the GPU encoder fast enough, compared to this CPU, to be worth its larger files?</summary>
    public bool Faster => Usable && CpuFps > 0 && GpuFps / CpuFps >= HardwareEncoders.RequiredAdvantage(SpeedPreset.Balanced);
}

/// <summary>
/// Decides whether a video encode should run on the GPU (NVIDIA NVENC, Intel Quick Sync, AMD AMF).
///
/// Two things are measured rather than assumed, once per machine setup:
/// • whether an encoder works at all — ffmpeg listing it only means it was compiled in, so each
///   candidate encodes a few blank frames with the options a real job would use;
/// • whether it is actually faster than the CPU — a laptop iGPU can lose to a many-core CPU,
///   and then the CPU encoder wins on both speed and compression.
/// Results are cached on disk, keyed by the ffmpeg build, the GPU drivers and the core count,
/// so they are re-measured when any of those change. Everything is local.
/// </summary>
public static class HardwareEncoders
{
    private const int BenchmarkFrames = 240;

    /// <summary>Which hardware encoders can exist here is the platform's call (AMF is Windows-only, for one).</summary>
    private static IReadOnlyList<GpuEncoder> Vendors => PlatformServices.Current.GpuEncoders;

    private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> Probes = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<double>>> Speeds = new();
    private static readonly SemaphoreSlim OneBenchmarkAtATime = new(1);
    private static readonly object CacheLock = new();
    private static Cache? _cache;

    /// <summary>Remembered measurements; discarded as a whole when <see cref="Key"/> no longer matches.</summary>
    internal sealed class Cache
    {
        public string Key { get; set; } = "";
        public Dictionary<string, bool> Usable { get; set; } = [];
        public Dictionary<string, double> Fps { get; set; } = [];
        public string? Hint { get; set; }
    }

    private static string CachePath => Path.Combine(PlatformServices.Current.DataDirectory, "encoders.json");

    public static string Describe(GpuEncoder gpu) => gpu switch
    {
        GpuEncoder.Nvidia => "NVIDIA NVENC",
        GpuEncoder.Intel => "Intel Quick Sync",
        GpuEncoder.Amd => "AMD AMF",
        GpuEncoder.Vaapi => "VA-API",
        _ => "none"
    };

    /// <summary>
    /// How many times faster than the CPU encoder (both measured at the Balanced preset) the GPU
    /// has to be before it is used: its files are larger, so a marginal lead isn't worth it. The
    /// faster presets raise the bar because the CPU encoder speeds up more there than the GPU does.
    /// </summary>
    public static double RequiredAdvantage(SpeedPreset speed) => speed == SpeedPreset.Balanced ? 1.4 : 1.6;

    private static bool Supports(GpuEncoder gpu, VideoCodec codec) => (gpu, codec) switch
    {
        (GpuEncoder.Nvidia or GpuEncoder.Intel, VideoCodec.H264 or VideoCodec.Hevc or VideoCodec.Hevc10Bit) => true,
        (GpuEncoder.Amd or GpuEncoder.Vaapi, VideoCodec.H264) => true,
        _ => false
    };

    /// <summary>True once every measurement <see cref="PickAsync"/> needs for this codec is already known.</summary>
    public static bool IsResolved(VideoCodec codec)
    {
        var cache = LoadCache();
        lock (CacheLock)
        {
            foreach (var gpu in Vendors)
            {
                if (!Supports(gpu, codec)) continue;
                foreach (bool rich in gpu == GpuEncoder.Nvidia ? new[] { true, false } : [false])
                {
                    var id = Id(new EncoderChoice(codec, gpu, rich));
                    if (!cache.Usable.TryGetValue(id, out var usable)) return false;
                    if (!usable) continue;
                    return codec != VideoCodec.H264 || (cache.Fps.ContainsKey(id) && cache.Fps.ContainsKey(CpuId));
                }
            }
            return true; // nothing usable — nothing left to measure
        }
    }

    /// <summary>
    /// The hardware encoder to use for this job, or null when the CPU should encode: no GPU
    /// encoder works, or it isn't decisively faster than this machine's CPU at this preset.
    /// </summary>
    public static async Task<EncoderChoice?> PickAsync(VideoCodec codec, SpeedPreset speed, CancellationToken ct)
    {
        if (await FindUsableAsync(codec, ct) is not { } choice) return null;

        // x265 is several times slower than any hardware HEVC encoder — no need to race them
        if (codec != VideoCodec.H264) return choice;

        double cpu = await SustainedCpuFpsAsync(ct);
        double gpu = await MeasureAsync(Id(choice), choice, ct);
        return cpu > 0 && gpu / cpu >= RequiredAdvantage(speed) ? choice : null;
    }

    /// <summary>Which GPU (if any) can encode ordinary H.264 video, and how it compares — what the UI reports.</summary>
    public static async Task<GpuReport> DetectAsync(CancellationToken ct = default)
    {
        var choice = await FindUsableAsync(VideoCodec.H264, ct);
        double gpuFps = 0, cpuFps = 0;
        if (choice is { } usable)
        {
            cpuFps = await SustainedCpuFpsAsync(ct);
            gpuFps = await MeasureAsync(Id(usable), usable, ct);
        }
        string? hint;
        lock (CacheLock) hint = LoadCache().Hint;
        return new GpuReport(choice?.Gpu ?? GpuEncoder.None, gpuFps, cpuFps, hint);
    }

    /// <summary>
    /// The CPU encoder's speed over a real, minutes-long job. The benchmark is a two-second burst,
    /// and laptops don't hold that pace: firmware power and thermal limits step in after a few
    /// seconds of all-core load, while a GPU encoder is unaffected. On the 16-core laptop this was
    /// tuned on, x264 fell from 176 to about 30 frames/s within four seconds and Quick Sync stayed
    /// at 220. Battery-powered machines are therefore credited with half their burst speed.
    /// </summary>
    private static async Task<double> SustainedCpuFpsAsync(CancellationToken ct)
    {
        double burst = await MeasureAsync(CpuId, new EncoderChoice(VideoCodec.H264, GpuEncoder.None, false), ct);
        return PlatformServices.Current.IsBatteryPowered ? burst * 0.5 : burst;
    }

    /// <summary>Forget everything measured so far (the ffmpeg binary changed).</summary>
    public static void Reset()
    {
        Probes.Clear();
        Speeds.Clear();
        lock (CacheLock) _cache = null;
    }

    private const string CpuId = "cpu:H264";

    private static string Id(EncoderChoice choice) => $"{choice.Gpu}:{choice.Codec}:{(choice.Rich ? "rich" : "plain")}";

    /// <summary>First working hardware encoder for the codec, in order of preference.</summary>
    private static async Task<EncoderChoice?> FindUsableAsync(VideoCodec codec, CancellationToken ct)
    {
        foreach (var gpu in Vendors)
        {
            if (!Supports(gpu, codec)) continue;
            // only NVENC has an optional richer option set; try it first, then the plain one
            if (gpu == GpuEncoder.Nvidia && await ProbeAsync(new EncoderChoice(codec, gpu, Rich: true), ct))
                return new EncoderChoice(codec, gpu, Rich: true);
            if (await ProbeAsync(new EncoderChoice(codec, gpu, Rich: false), ct))
                return new EncoderChoice(codec, gpu, Rich: false);
        }
        return null;
    }

    /// <summary>Tiny clip for "does this encoder start at all".</summary>
    private const string ProbeSource = "testsrc2=size=320x240:rate=30";

    /// <summary>
    /// 1080p benchmark clip: a scrolling, finely textured pattern. Of the built-in generators it is
    /// the one on which x264 and a hardware encoder keep the same speed ratio as on camera footage
    /// (flat test cards flatter the CPU encoder, pure noise cripples it).
    /// </summary>
    private const string BenchmarkSource = "cellauto=size=480x270:rate=30,scale=1920:1080:flags=bicubic";

    /// <summary>A throw-away encode of a generated clip, with the options a real job would use.</summary>
    private static List<string> TestEncode(string source, int frames, EncoderChoice choice, int width)
    {
        List<string> args = ["-f", "lavfi", "-i", source, "-frames:v", frames.ToString()];
        if (VideoEncoding.FilterSuffix(choice) is { } filter) args.AddRange(["-vf", filter]);
        args.AddRange(VideoEncoding.Arguments(choice, SpeedPreset.Balanced, bitrateKbps: null, width));
        args.AddRange(["-f", "null", FfmpegRunner.NullSink]);
        return args;
    }

    private static async Task<bool> ProbeAsync(EncoderChoice choice, CancellationToken ct)
    {
        var id = Id(choice);
        lock (CacheLock)
            if (LoadCache().Usable.TryGetValue(id, out var known)) return known;

        // the probe itself is never cancelled half-way, so one job's cancellation can't poison the result
        var probe = Probes.GetOrAdd(id, _ => new Lazy<Task<bool>>(async () =>
        {
            var args = TestEncode(ProbeSource, frames: 8, choice, width: 320);
            var (ok, error, _) = await FfmpegRunner.TryRunAsync(args, TimeSpan.FromSeconds(20), CancellationToken.None);

            lock (CacheLock)
            {
                var cache = LoadCache();
                cache.Usable[id] = ok;
                // the one failure a user can fix themselves: an NVIDIA card whose driver predates this ffmpeg
                if (!ok && choice.Gpu == GpuEncoder.Nvidia &&
                    Regex.Match(error, @"minimum required Nvidia driver for nvenc is ([\d.]+)") is { Success: true } match)
                    cache.Hint = $"Your NVIDIA GPU can't be used for encoding yet: its driver is older than this FFmpeg needs. " +
                                 $"Update the NVIDIA driver to {match.Groups[1].Value} or newer to enable NVENC.";
                SaveCache(cache);
            }
            return ok;
        }));
        return await probe.Value.WaitAsync(ct);
    }

    /// <summary>Encoding speed (frames per second) on a synthetic 1080p clip at the Balanced preset.</summary>
    private static async Task<double> MeasureAsync(string id, EncoderChoice choice, CancellationToken ct)
    {
        lock (CacheLock)
            if (LoadCache().Fps.TryGetValue(id, out var known)) return known;

        var measurement = Speeds.GetOrAdd(id, _ => new Lazy<Task<double>>(async () =>
        {
            var args = TestEncode(BenchmarkSource, BenchmarkFrames, choice, width: 1920);

            // one at a time: two encoders racing each other would both look slow
            await OneBenchmarkAtATime.WaitAsync();
            double fps;
            try
            {
                var (ok, _, elapsed) = await FfmpegRunner.TryRunAsync(args, TimeSpan.FromSeconds(90), CancellationToken.None);
                fps = ok && elapsed > TimeSpan.Zero ? BenchmarkFrames / elapsed.TotalSeconds : 0;
            }
            finally
            {
                OneBenchmarkAtATime.Release();
            }

            lock (CacheLock)
            {
                var cache = LoadCache();
                cache.Fps[id] = Math.Round(fps, 1);
                SaveCache(cache);
            }
            return fps;
        }));
        return await measurement.Value.WaitAsync(ct);
    }

    // ---------- on-disk cache ----------

    private static Cache LoadCache()
    {
        lock (CacheLock)
        {
            if (_cache != null) return _cache;

            var key = MachineKey();
            try
            {
                if (File.Exists(CachePath) &&
                    JsonSerializer.Deserialize(File.ReadAllText(CachePath), EncoderCacheJson.Default.Cache) is { } saved && saved.Key == key)
                    return _cache = saved;
            }
            catch
            {
                // unreadable cache — measure again
            }
            return _cache = new Cache { Key = key };
        }
    }

    private static void SaveCache(Cache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, EncoderCacheJson.Default.Cache));
        }
        catch
        {
            // the cache only saves time; losing it is harmless
        }
    }

    /// <summary>Changes whenever a measurement could come out differently: ffmpeg build, GPU drivers, core count.</summary>
    private static string MachineKey()
    {
        var parts = new List<string> { Environment.ProcessorCount.ToString() };
        try
        {
            if (ToolLocator.FfmpegPath is { } ffmpeg)
            {
                var file = new FileInfo(ffmpeg);
                parts.Add($"{ffmpeg}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
            }

            // display adapters and their driver versions
            parts.Add(PlatformServices.Current.GpuFingerprint());
        }
        catch
        {
            // adapters unreadable: the key still covers ffmpeg and the core count
        }
        return string.Join(";", parts);
    }
}

/// <summary>Source-generated JSON for the encoder cache, so it keeps working in a trimmed build.</summary>
[JsonSerializable(typeof(HardwareEncoders.Cache))]
internal sealed partial class EncoderCacheJson : JsonSerializerContext;
