using FileConverter.Core;
using FileConverter.Core.Converters;
using ImageMagick;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>Each precise video, audio and animation setting, checked on the file it produces (needs FFmpeg).</summary>
public class MediaSettingsTests(Samples samples) : IClassFixture<Samples>
{
    private static async Task<MediaInfo> ProbeAsync(string path) =>
        await FfmpegRunner.ProbeAsync(path, TestContext.Current.CancellationToken);

    // ---------- video ----------

    [Fact]
    public async Task WithoutSettingsVideoIsCopiedNotReencoded()
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Clip, "mkv");

        Assert.Equal("lossless copy (no re-encoding)", result.Note);
        Assert.Equal("h264", (await ProbeAsync(result.OutputPath)).Video!.Codec);
    }

    [Theory]
    [InlineData("mkv", "hevc", "hevc")]
    [InlineData("mp4", "hevc", "hevc")]
    [InlineData("mkv", "vp9", "vp9")]
    [InlineData("mkv", "h264", "h264")]
    public async Task TheChosenVideoFormatIsUsed(string target, string setting, string expectedCodec)
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Clip, target, (K.VideoCodec, setting));

        var info = await ProbeAsync(result.OutputPath);
        Assert.Equal(expectedCodec, info.Video!.Codec);
        Assert.True(info.HasAudio);
        // asking for the format the video is already in is no reason to encode it again
        if (setting == "h264") Assert.Equal("lossless copy (no re-encoding)", result.Note);
        else Assert.Null(result.Note);
    }

    [Fact]
    public async Task Av1IsOfferedOnlyWhenThisFfmpegCanEncodeIt()
    {
        Samples.RequireFfmpeg();
        var offered = Core.Settings.SettingCatalog.For("mp4", "mkv")
            .Single(d => d.Key == K.VideoCodec).Choices.Any(c => c.Value == "av1");
        Assert.Equal(VideoEncoding.Av1Encoder != null, offered);
        Assert.SkipWhen(VideoEncoding.Av1Encoder == null, "this FFmpeg build has no AV1 encoder");

        var result = await samples.ConvertAsync(samples.Clip, "mkv", (K.VideoCodec, "av1"), (K.Speed, "fastest"));
        Assert.Equal("av1", (await ProbeAsync(result.OutputPath)).Video!.Codec);
    }

    [Fact]
    public async Task AFormatTheContainerCannotHoldIsIgnored()
    {
        Samples.RequireFfmpeg();
        // H.265 does not go into webm; the conversion still works, with webm's own format
        var result = await samples.ConvertAsync(samples.Clip, "webm", (K.VideoCodec, "hevc"));
        Assert.Equal("vp9", (await ProbeAsync(result.OutputPath)).Video!.Codec);
    }

    [Fact]
    public async Task AQualityLevelForcesAnEncodeAndDecidesTheSize()
    {
        Samples.RequireFfmpeg();
        var fine = await samples.ConvertAsync(samples.Clip, "mkv", (K.Crf, "16"));
        var coarse = await samples.ConvertAsync(samples.Clip, "mkv", (K.Crf, "40"));

        Assert.Null(fine.Note);
        Assert.True(Samples.Size(coarse.OutputPath) * 2 < Samples.Size(fine.OutputPath),
            $"CRF 40 gave {Samples.Size(coarse.OutputPath)} bytes, CRF 16 {Samples.Size(fine.OutputPath)}");
    }

    [Fact]
    public async Task ATargetSizeInMegabytesIsMet()
    {
        Samples.RequireFfmpeg();
        const double megabytes = 0.12;
        var options = new ConversionOptions { UseGpu = false, Speed = SpeedPreset.Balanced };
        var result = await samples.ConvertAsync(samples.Clip, "mp4", options, (K.TargetSizeMb, "0.12"));

        long size = Samples.Size(result.OutputPath);
        Assert.InRange(size, (long)(megabytes * 1024 * 1024 * 0.5), (long)(megabytes * 1024 * 1024 * 1.15));
    }

    [Fact]
    public async Task AVideoBitrateIsUsed()
    {
        Samples.RequireFfmpeg();
        var low = await samples.ConvertAsync(samples.Clip, "mp4", (K.VideoBitrate, "150"));
        var high = await samples.ConvertAsync(samples.Clip, "mp4", (K.VideoBitrate, "1500"));
        Assert.True(Samples.Size(low.OutputPath) * 2 < Samples.Size(high.OutputPath));
    }

    [Theory]
    [InlineData("160", null, 160, 120)]
    [InlineData(null, "120", 160, 120)]
    [InlineData("200", "90", 120, 90)]
    public async Task VideoIsScaledDownToTheLimits(string? maxWidth, string? maxHeight, int width, int height)
    {
        Samples.RequireFfmpeg();
        var settings = new List<(string, string)>();
        if (maxWidth != null) settings.Add((K.MaxWidth, maxWidth));
        if (maxHeight != null) settings.Add((K.MaxHeight, maxHeight));

        var result = await samples.ConvertAsync(samples.Clip, "mp4", settings.ToArray());
        var video = (await ProbeAsync(result.OutputPath)).Video!;
        Assert.Equal((width, height), (video.Width, video.Height));
    }

    [Fact]
    public async Task LimitsLargerThanTheVideoChangeNothing()
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Clip, "mkv", (K.MaxWidth, "1920"), (K.MaxHeight, "1080"), (K.Fps, "60"));
        Assert.Equal("lossless copy (no re-encoding)", result.Note);
    }

    [Fact]
    public async Task TheFrameRateIsReduced()
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Clip, "mp4", (K.Fps, "10"));
        Assert.Equal(10, (await ProbeAsync(result.OutputPath)).Video!.FrameRate, 1);
    }

    [Fact]
    public async Task TheSoundOfAVideoCanBeRemovedOrReencoded()
    {
        Samples.RequireFfmpeg();
        var silent = await samples.ConvertAsync(samples.Clip, "mkv", (K.AudioCodec, "none"));
        var silentInfo = await ProbeAsync(silent.OutputPath);
        Assert.False(silentInfo.HasAudio);
        Assert.True(silentInfo.HasVideo);
        Assert.Equal("lossless copy (no re-encoding)", silent.Note);

        var mp3 = await samples.ConvertAsync(samples.Clip, "mkv", (K.AudioCodec, "mp3"), (K.AudioBitrate, "96"), (K.Channels, "mono"));
        var track = (await ProbeAsync(mp3.OutputPath)).Audio.Single();
        Assert.Equal("mp3", track.Codec);
        Assert.Equal(1, track.Channels);
        Assert.Equal("video copied losslessly, audio re-encoded", mp3.Note);

        var opus = await samples.ConvertAsync(samples.Clip, "mkv", (K.AudioCodec, "opus"));
        Assert.Equal("opus", (await ProbeAsync(opus.OutputPath)).Audio.Single().Codec);
    }

    [Fact]
    public async Task SubtitlesAreKeptUnlessRemoved()
    {
        Samples.RequireFfmpeg();
        var source = samples.ClipWithSubtitles();

        var kept = await samples.ConvertAsync(source, "mp4");
        var removed = await samples.ConvertAsync(source, "mp4", (K.Subtitles, "remove"));

        Assert.Single((await ProbeAsync(kept.OutputPath)).Subtitles);
        Assert.Empty((await ProbeAsync(removed.OutputPath)).Subtitles);
    }

    [Fact]
    public async Task TagsAreKeptUnlessRemoved()
    {
        Samples.RequireFfmpeg();
        var kept = await samples.ConvertAsync(samples.Clip, "mkv");
        var removed = await samples.ConvertAsync(samples.Clip, "mkv", (K.Metadata, "strip"));

        Assert.Equal("Sample clip", Samples.Probe(kept.OutputPath, "format_tags=title"));
        Assert.Equal("", Samples.Probe(removed.OutputPath, "format_tags=title"));
    }

    // ---------- audio ----------

    [Fact]
    public async Task WithoutSettingsLosslessAudioStaysLossless()
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Wav, "flac");
        Assert.Equal("lossless", result.Note);

        var copy = await samples.ConvertAsync(samples.Clip, "aac");
        Assert.Equal("lossless copy", copy.Note);
    }

    [Fact]
    public async Task M4aAndOpusAreWrittenAndCopiedWhereTheyCanBe()
    {
        Samples.RequireFfmpeg();

        // the AAC sound of an mp4 goes into an m4a untouched
        var extracted = await samples.ConvertAsync(samples.Clip, "m4a");
        Assert.Equal("lossless copy", extracted.Note);
        Assert.Equal("aac", (await ProbeAsync(extracted.OutputPath)).Audio.Single().Codec);

        var m4a = await samples.ConvertAsync(samples.Wav, "m4a", (K.AudioBitrate, "96"));
        var m4aInfo = await ProbeAsync(m4a.OutputPath);
        Assert.Equal("aac", m4aInfo.Audio.Single().Codec);
        Assert.Equal(Samples.ClipSeconds, m4aInfo.DurationSeconds, 0.3);

        var opus = await samples.ConvertAsync(samples.Wav, "opus", (K.Channels, "mono"));
        var track = (await ProbeAsync(opus.OutputPath)).Audio.Single();
        Assert.Equal("opus", track.Codec);
        Assert.Equal(1, track.Channels);

        // Opus has no sample rate to pick, so none is offered
        Assert.DoesNotContain(Core.Settings.SettingCatalog.For("wav", "opus"), d => d.Key == K.SampleRate);
        Assert.Contains(Core.Settings.SettingCatalog.For("wav", "m4a"), d => d.Key == K.SampleRate);
    }

    [Fact]
    public async Task CoverArtSurvivesIntoM4a()
    {
        Samples.RequireFfmpeg();
        var withCover = Path.Combine(samples.Folder, "cover.mp3");
        Samples.Ffmpeg(["-i", samples.Mp3, "-i", samples.Jpg, "-map", "0:a", "-map", "1:v", "-c:a", "copy", "-c:v", "mjpeg",
            "-disposition:v:0", "attached_pic", "-id3v2_version", "3", withCover]);
        Assert.NotNull((await ProbeAsync(withCover)).CoverArt);

        var kept = await samples.ConvertAsync(withCover, "m4a");
        var removed = await samples.ConvertAsync(withCover, "m4a", (K.Metadata, "strip"));

        Assert.NotNull((await ProbeAsync(kept.OutputPath)).CoverArt);
        Assert.Null((await ProbeAsync(removed.OutputPath)).CoverArt);
    }

    [Fact]
    public async Task AudioBitrateDecidesTheFileSize()
    {
        Samples.RequireFfmpeg();
        var low = await samples.ConvertAsync(samples.Wav, "mp3", (K.AudioBitrate, "48"));
        var high = await samples.ConvertAsync(samples.Wav, "mp3", (K.AudioBitrate, "320"));
        Assert.True(Samples.Size(low.OutputPath) * 3 < Samples.Size(high.OutputPath));

        var smallVbr = await samples.ConvertAsync(samples.Wav, "mp3", (K.VbrQuality, "9"));
        var bestVbr = await samples.ConvertAsync(samples.Wav, "mp3", (K.VbrQuality, "0"));
        Assert.True(Samples.Size(smallVbr.OutputPath) < Samples.Size(bestVbr.OutputPath));
    }

    [Fact]
    public async Task SampleRateChannelsAndBitDepthAreApplied()
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Wav, "flac",
            (K.SampleRate, "22050"), (K.Channels, "mono"), (K.BitDepth, "24"));

        var track = (await ProbeAsync(result.OutputPath)).Audio.Single();
        Assert.Equal(22050, track.SampleRate);
        Assert.Equal(1, track.Channels);
        Assert.Equal(24, track.BitsPerSample);
        Assert.Null(result.Note); // no longer the same samples

        var wav = await samples.ConvertAsync(samples.Mp3, "wav", (K.BitDepth, "24"));
        Assert.Equal("pcm_s24le", (await ProbeAsync(wav.OutputPath)).Audio.Single().Codec);
    }

    [Fact]
    public async Task ASettingThatChangesTheSoundPreventsAPlainCopy()
    {
        Samples.RequireFfmpeg();
        // mp3 → mp3 is not offered, so take the aac out of the clip: normally a copy, with a bitrate an encode
        var encoded = await samples.ConvertAsync(samples.Clip, "aac", (K.AudioBitrate, "64"));
        Assert.Null(encoded.Note);
        Assert.True(Samples.Size(encoded.OutputPath) < 40_000);
    }

    [Fact]
    public async Task AnAudioTargetSizeIsMet()
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Wav, "mp3", (K.TargetSizeMb, "0.03"));
        Assert.InRange(Samples.Size(result.OutputPath), 10_000, (long)(0.03 * 1024 * 1024 * 1.1));
    }

    [Fact]
    public async Task AudioTagsAreKeptUnlessRemoved()
    {
        Samples.RequireFfmpeg();
        var kept = await samples.ConvertAsync(samples.Wav, "mp3");
        var removed = await samples.ConvertAsync(samples.Wav, "mp3", (K.Metadata, "strip"));

        Assert.Equal("Sample tone", Samples.Probe(kept.OutputPath, "format_tags=title"));
        Assert.Equal("", Samples.Probe(removed.OutputPath, "format_tags=title"));
    }

    // ---------- animation and frames ----------

    [Fact]
    public async Task GifSettingsShapeTheAnimation()
    {
        Samples.RequireFfmpeg();
        var result = await samples.ConvertAsync(samples.Clip, "gif",
            (K.Fps, "5"), (K.MaxWidth, "120"), (K.Colors, "16"), (K.Loop, "once"), (K.Dither, "none"));

        using var frames = new MagickImageCollection(result.OutputPath);
        Assert.InRange(frames.Count, 13, 17); // 3 s at 5 frames per second
        Assert.Equal(120u, frames[0].Width);
        Assert.Equal(1u, frames[0].AnimationIterations);
    }

    /// <summary>A three-second clip of the moving test picture, made to try one awkward property of video files.</summary>
    private string AwkwardClip(string kind)
    {
        string name = kind switch
        {
            "late" => "late.mkv",
            "transport" => "transport.ts",
            "webm" => "other.webm",
            _ => kind + ".mp4"
        };
        var path = Path.Combine(samples.Folder, name);
        if (File.Exists(path)) return path;

        string Picture(string rate) => $"testsrc2=size=320x240:rate={rate}:duration=3";
        string[] h264 = ["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p"];
        Samples.Ffmpeg(kind switch
        {
            // the television rate: 29.97 frames per second, which no whole number of hundredths fits
            "ntsc" => ["-f", "lavfi", "-i", Picture("30000/1001"), .. h264, path],
            // twice the rate a GIF is made at: every second frame is dropped
            "fast" => ["-f", "lavfi", "-i", Picture("60"), .. h264, path],
            // one picture the others all depend on: nowhere to start decoding but the beginning
            "sparse" => ["-f", "lavfi", "-i", Picture("25"), .. h264, "-g", "600", "-keyint_min", "600", "-sc_threshold", "0", path],
            // the picture starts half a second after the sound
            "late" => ["-f", "lavfi", "-i", "sine=frequency=440:duration=3.5", "-itsoffset", "0.5", "-f", "lavfi", "-i", Picture("25"),
                "-map", "1:v", "-map", "0:a", .. h264, "-c:a", "aac", path],
            // a broadcast stream: its clock does not start at zero
            "transport" => ["-f", "lavfi", "-i", Picture("25"), "-c:v", "mpeg2video", "-q:v", "4", path],
            "webm" => ["-f", "lavfi", "-i", Picture("30"), "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-crf", "30", "-b:v", "0", path],
            _ => throw new ArgumentException(kind)
        });
        return path;
    }

    /// <summary>
    /// A clip is turned into a GIF as several stretches at once, joined afterwards. What comes out
    /// has to be the animation a single pass makes: the same frames in the same order — the
    /// test picture changes with every frame, so one lost or doubled at a join would show.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("ntsc")]
    [InlineData("fast")]
    [InlineData("sparse")]
    [InlineData("late")]
    [InlineData("transport")]
    [InlineData("webm")]
    public async Task AGifMadeInStretchesIsTheSameAnimation(string kind)
    {
        Samples.RequireFfmpeg();
        Assert.SkipWhen(Environment.ProcessorCount < 4, "with fewer than four cores a clip is not divided");
        var clip = kind == "plain" ? samples.Clip : AwkwardClip(kind);

        ConversionResult whole;
        MediaConversion.GifStretchLimit = 1;
        try
        {
            whole = await samples.ConvertAsync(clip, "gif", (K.MaxWidth, "160"));
        }
        finally
        {
            MediaConversion.GifStretchLimit = null;
        }
        var joined = await samples.ConvertAsync(clip, "gif", (K.MaxWidth, "160"));

        using var expected = new MagickImageCollection(whole.OutputPath);
        using var actual = new MagickImageCollection(joined.OutputPath);
        expected.Coalesce();
        actual.Coalesce();
        Assert.InRange(expected.Count, 72, 92); // 3 s at 25 to 30 frames per second
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.True(expected[i].Compare(actual[i], ErrorMetric.RootMeanSquared) == 0, $"frame {i + 1} differs");

        // hundredths of a second: each join may round its last frame's time differently
        long length = expected.Sum(frame => (long)frame.AnimationDelay), joinedLength = actual.Sum(frame => (long)frame.AnimationDelay);
        Assert.InRange(joinedLength, length - 4, length + 4);
        Assert.Equal(0u, actual[0].AnimationIterations); // repeats for ever, as asked
    }

    [Fact]
    public void AClipIsDividedIntoStretchesThatMeetExactly()
    {
        // 60 s at 30 frames per second, the picture starting 0.5 s into the file
        var stretches = MediaConversion.PlanGifStretches(60, 30, startOffset: 0.5, workers: 8);

        Assert.Equal(8, stretches.Count);
        Assert.Equal(1800, stretches.Sum(s => s.Frames));
        Assert.Null(stretches[0].Seek);
        for (int k = 1; k < stretches.Count; k++)
        {
            Assert.Equal(stretches[k - 1].FirstFrame + stretches[k - 1].Frames, stretches[k].FirstFrame);
            // decoding starts a second (30 frames) early, on the same grid of frame times
            Assert.Equal(30, stretches[k].Skip);
            Assert.Equal(0.5 + (stretches[k].FirstFrame - 30) / 30.0, stretches[k].Seek!.Value, 6);
        }
        Assert.True(stretches[^1].ToEnd);
        Assert.DoesNotContain(stretches.Take(7), s => s.ToEnd);

        // a GIF counts time in hundredths of a second: at 30 frames per second every stretch is
        // a multiple of three frames long, so that no join has to round
        var uneven = MediaConversion.PlanGifStretches(20, 30, 0, 7);
        Assert.Equal(600, uneven.Sum(s => s.Frames));
        Assert.All(uneven, s => Assert.Equal(0, s.FirstFrame % 3));

        // too short to be worth dividing: under two stretches of 24 frames
        Assert.Empty(MediaConversion.PlanGifStretches(1.5, 30, 0, 8));
        Assert.Empty(MediaConversion.PlanGifStretches(60, 30, 0, 1));
    }

    [Fact]
    public async Task FrameExtractionFollowsTheRateTheLimitAndTheSize()
    {
        Samples.RequireFfmpeg();
        var twoPerSecond = await samples.ConvertAsync(samples.Clip, "png", (K.Fps, "2"), (K.MaxWidth, "160"));
        var files = Directory.GetFiles(twoPerSecond.OutputPath, "*.png");
        Assert.InRange(files.Length, 5, 7); // 3 s at 2 frames per second
        Assert.Equal(160u, new MagickImageInfo(files[0]).Width);

        var limited = await samples.ConvertAsync(samples.Clip, "jpg", (K.MaxFrames, "4"), (K.Quality, "40"));
        var best = await samples.ConvertAsync(samples.Clip, "jpg", (K.MaxFrames, "4"), (K.Quality, "100"));
        Assert.Equal(4, Directory.GetFiles(limited.OutputPath, "*.jpg").Length);
        Assert.True(Directory.GetFiles(limited.OutputPath).Sum(Samples.Size) < Directory.GetFiles(best.OutputPath).Sum(Samples.Size));
    }

    [Fact]
    public async Task ASlideshowGifUsesTheChosenTiming()
    {
        var pictures = new[] { samples.Png, samples.Jpg, samples.TaggedJpg };
        var result = await samples.CombineAsync(pictures, "gif", (K.FrameSeconds, "0.5"), (K.Loop, "once"), (K.MaxWidth, "160"));

        using var frames = new MagickImageCollection(result);
        Assert.Equal(3, frames.Count);
        Assert.Equal(50u, frames[0].AnimationDelay);
        Assert.Equal(1u, frames[0].AnimationIterations);
        Assert.Equal(160u, frames[0].Width);
    }
}
