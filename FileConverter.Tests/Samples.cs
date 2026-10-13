using System.Diagnostics;
using FileConverter.Core;
using FileConverter.Core.Settings;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

namespace FileConverter.Tests;

/// <summary>
/// Small source files made on the spot (nothing binary is kept in the repository), and the
/// shortcuts the engine tests use to convert them with a given set of precise settings.
/// </summary>
public sealed class Samples : IDisposable
{
    private readonly TempFolder _folder = new("samples");
    private readonly ConversionService _service = new();
    private int _counter;

    public string Folder => _folder.Path;

    public void Dispose() => _folder.Dispose();

    public static bool HasFfmpeg => ToolLocator.FfmpegPath != null && ToolLocator.FfprobePath != null;

    public static void RequireFfmpeg() => Assert.SkipUnless(HasFfmpeg, "FFmpeg is not installed on this machine.");

    /// <summary>A fresh output folder, so results of different conversions never collide.</summary>
    public string NewOutputFolder()
    {
        var path = Path.Combine(_folder.Path, $"out-{Interlocked.Increment(ref _counter)}");
        Directory.CreateDirectory(path);
        return path;
    }

    // ---------- pictures ----------

    /// <summary>A 320 × 240 picture with shapes and fine noise (so quality settings change its size).</summary>
    public static MagickImage Picture(uint width = 320, uint height = 240)
    {
        var image = new MagickImage("gradient:#2f6fd0-#f2c14e", new MagickReadSettings { Width = width, Height = height });
        new Drawables()
            .FillColor(new MagickColor("#d0443c")).Rectangle(30, 40, 130, 130)
            .FillColor(new MagickColor("#3fb68b")).Ellipse(220, 150, 60, 45, 0, 360)
            .Draw(image);
        image.AddNoise(NoiseType.Gaussian, 0.6);
        // an everyday picture: 8 bits per channel and no transparency (drawing switches the alpha channel on)
        image.Alpha(AlphaAction.Off);
        image.Depth = 8;
        return image;
    }

    private readonly Lazy<string> _png;
    private readonly Lazy<string> _jpg;
    private readonly Lazy<string> _transparentPng;
    private readonly Lazy<string> _taggedJpg;

    public string Png => _png.Value;
    public string Jpg => _jpg.Value;

    /// <summary>A png whose left half is fully transparent.</summary>
    public string TransparentPng => _transparentPng.Value;

    /// <summary>A jpg carrying a camera name and a GPS position.</summary>
    public string TaggedJpg => _taggedJpg.Value;

    public Samples()
    {
        _png = new(() => Save("photo.png", (image, path) => image.Write(path, MagickFormat.Png)));
        _jpg = new(() => Save("photo.jpg", (image, path) =>
        {
            image.Quality = 92;
            image.Write(path, MagickFormat.Jpeg);
        }));
        _transparentPng = new(() => Save("cutout.png", (image, path) =>
        {
            // the mask's black half becomes fully transparent
            image.Alpha(AlphaAction.Set);
            using var mask = new MagickImage(MagickColors.White, image.Width, image.Height);
            new Drawables().FillColor(MagickColors.Black).Rectangle(0, 0, 159, 239).Draw(mask);
            mask.Alpha(AlphaAction.Off);
            image.Composite(mask, CompositeOperator.CopyAlpha);
            image.Write(path, MagickFormat.Png32);
        }));
        _taggedJpg = new(() => Save("tagged.jpg", (image, path) =>
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(41, 1), new Rational(0, 1), new Rational(30, 1)]);
            image.SetProfile(exif);
            image.Quality = 92;
            image.Write(path, MagickFormat.Jpeg);
        }));
        _clip = new(() => MakeClip("clip.mp4", ["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k"]));
        _wav = new(() => MakeTone("tone.wav", ["-c:a", "pcm_s16le"]));
        _mp3 = new(() => MakeTone("tone.mp3", ["-c:a", "libmp3lame", "-b:a", "192k"]));
    }

    private string Save(string name, Action<MagickImage, string> save)
    {
        using var image = Picture();
        var path = Path.Combine(Folder, name);
        save(image, path);
        return path;
    }

    // ---------- sound and video ----------

    public const double ClipSeconds = 3.0;

    private readonly Lazy<string> _clip;
    private readonly Lazy<string> _wav;
    private readonly Lazy<string> _mp3;

    /// <summary>3 s, 320 × 240, 25 fps, H.264 with stereo AAC, titled "Sample clip".</summary>
    public string Clip => _clip.Value;

    /// <summary>3 s stereo 16-bit 44.1 kHz.</summary>
    public string Wav => _wav.Value;

    public string Mp3 => _mp3.Value;

    private string MakeClip(string name, string[] codecs)
    {
        var path = Path.Combine(Folder, name);
        Ffmpeg(["-f", "lavfi", "-i", $"testsrc2=size=320x240:rate=25:duration={ClipSeconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={ClipSeconds}", "-ac", "2", "-shortest",
            "-metadata", "title=Sample clip", .. codecs, path]);
        return path;
    }

    private string MakeTone(string name, string[] codec)
    {
        var path = Path.Combine(Folder, name);
        Ffmpeg(["-f", "lavfi", "-i", $"sine=frequency=440:duration={ClipSeconds}", "-ac", "2", "-ar", "44100",
            "-metadata", "title=Sample tone", .. codec, path]);
        return path;
    }

    /// <summary>The clip with a subtitle track, as an mkv.</summary>
    public string ClipWithSubtitles()
    {
        var path = Path.Combine(Folder, "subtitled.mkv");
        if (File.Exists(path)) return path;
        var srt = Path.Combine(Folder, "lines.srt");
        File.WriteAllText(srt, "1\n00:00:00,200 --> 00:00:02,000\nMerhaba\n");
        Ffmpeg(["-i", Clip, "-i", srt, "-map", "0", "-map", "1", "-c", "copy", "-c:s", "srt", path]);
        return path;
    }

    public static void Ffmpeg(IEnumerable<string> arguments)
    {
        var (ok, error, _) = FfmpegRunner.TryRunAsync(arguments, TimeSpan.FromMinutes(2), CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert.True(ok, "ffmpeg could not make a test sample: " + error);
    }

    /// <summary>One value out of ffprobe, e.g. "format_tags=title" or "stream=r_frame_rate".</summary>
    public static string Probe(string file, string entries, string? stream = null)
    {
        var psi = new ProcessStartInfo(ToolLocator.FfprobePath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("error");
        if (stream != null)
        {
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add(stream);
        }
        foreach (var arg in new[] { "-show_entries", entries, "-of", "default=noprint_wrappers=1:nokey=1", file })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    // ---------- converting ----------

    public static DetailSettings Settings(params (string Key, string Value)[] values) =>
        values.Length == 0 ? DetailSettings.None : new DetailSettings(values.ToDictionary(v => v.Key, v => v.Value));

    /// <summary>Converts a sample with the given precise settings and returns what was produced.</summary>
    public async Task<ConversionResult> ConvertAsync(string input, string target, params (string Key, string Value)[] settings) =>
        await ConvertAsync(input, target, new ConversionOptions { UseGpu = false, Speed = SpeedPreset.Fast }, settings);

    public async Task<ConversionResult> ConvertAsync(string input, string target, ConversionOptions options,
        params (string Key, string Value)[] settings)
    {
        var output = ConversionService.BuildOutputPath(input, target, NewOutputFolder());
        return await _service.ConvertAsync(input, output, options.WithDetail(Settings(settings)), null,
            TestContext.Current.CancellationToken);
    }

    public async Task<string> CombineAsync(IReadOnlyList<string> inputs, string target, params (string Key, string Value)[] settings)
    {
        var options = new ConversionOptions { UseGpu = false, Speed = SpeedPreset.Fast }.WithDetail(Settings(settings));
        return (await _service.ConvertManyAsync(inputs, target, options, NewOutputFolder(), null,
            TestContext.Current.CancellationToken))!;
    }

    public static long Size(string path) => new FileInfo(path).Length;
}
