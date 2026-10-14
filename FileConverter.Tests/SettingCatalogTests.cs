using FileConverter.Core;
using FileConverter.Core.Settings;
using Xunit;
using K = FileConverter.Core.Settings.SettingKeys;

namespace FileConverter.Tests;

/// <summary>The list of precise settings itself: what is offered where, and how values are read.</summary>
public class SettingCatalogTests
{
    private static string[] Keys(string source, string target, bool merging = false) =>
        SettingCatalog.For(source, target, merging).Select(d => d.Key).ToArray();

    private static string[] PanelKeys(string source, string target) =>
        SettingCatalog.For(source, target).Where(d => d.InPanel).Select(d => d.Key).ToArray();

    private static Dictionary<string, string> Values(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    [Fact]
    public void TheSettingsOfferedDependOnBothEndsOfTheConversion()
    {
        // a photo → jpg: quality and size, nothing about pages or frames
        Assert.Contains(K.Quality, Keys("png", "jpg"));
        Assert.Contains(K.Chroma, Keys("png", "jpg"));
        Assert.Contains(K.Metadata, Keys("png", "jpg"));
        Assert.DoesNotContain(K.Dpi, Keys("png", "jpg"));
        Assert.DoesNotContain(K.Fps, Keys("png", "jpg"));

        // a PDF → jpg adds a resolution and a page list; a video → jpg a frame rate
        Assert.Contains(K.Dpi, Keys("pdf", "jpg"));
        Assert.Contains(K.Pages, Keys("pdf", "jpg"));
        Assert.Contains(K.Fps, Keys("mp4", "jpg"));
        Assert.Contains(K.MaxFrames, Keys("mp4", "jpg"));

        // png has no quality to set, only effort — and that is the Speed setting's, so only a script sets it
        Assert.DoesNotContain(K.Quality, Keys("jpg", "png"));
        Assert.Contains(K.Compression, Keys("jpg", "png"));
        Assert.DoesNotContain(K.Compression, PanelKeys("jpg", "png"));

        // video
        Assert.Contains(K.VideoCodec, Keys("mov", "mp4"));
        Assert.Contains(K.Crf, Keys("mov", "mp4"));
        Assert.DoesNotContain(K.Crf, Keys("mp4", "avi"));
        Assert.Contains(K.AudioCodec, Keys("mov", "mp4"));
        Assert.DoesNotContain(K.AudioCodec, Keys("gif", "mp4")); // a gif has no sound

        // audio taken out of a video is audio
        Assert.Contains(K.AudioBitrate, Keys("mp4", "mp3"));
        Assert.Contains(K.BitDepth, Keys("mp3", "wav"));
        Assert.DoesNotContain(K.AudioBitrate, Keys("mp3", "wav"));

        // a slideshow gif has a duration per picture, a gif from video a frame rate
        Assert.Contains(K.FrameSeconds, Keys("png", "gif", merging: true));
        Assert.DoesNotContain(K.FrameSeconds, Keys("png", "gif"));
        Assert.Contains(K.Dither, Keys("mp4", "gif"));

        // archives
        Assert.Equal([K.SizeMode, K.Level, K.Password, K.Solid], Keys("txt", "7z"));
        Assert.Equal([K.SizeMode, K.Level, K.Password], Keys("txt", "zip"));
        Assert.Empty(Keys("zip", "tar"));

        // documents
        Assert.Equal([K.PageSize, K.Margin], Keys("jpg", "pdf"));
        Assert.Equal([K.PageSize, K.Margin, K.FontSize], Keys("txt", "pdf"));
        Assert.Equal([K.Engine], Keys("docx", "pdf"));
        Assert.Empty(Keys("txt", "docx"));
    }

    [Fact]
    public void EveryOfferedConversionHasSettingsThatAreWellFormed()
    {
        var sources = new[] { "jpg", "png", "gif", "webp", "heic", "mp3", "wav", "mp4", "mkv", "txt", "md", "pdf", "docx", "doc", "rtf", "html", "odt", "epub", "xlsx", "xls", "ods", "csv", "pptx", "ppt", "odp", "zip", "7z", "xyz" };
        foreach (var source in sources)
        foreach (var target in FormatRegistry.GetTargets(source, rarOutputAvailable: true))
        foreach (var merging in new[] { false, true })
        {
            var definitions = SettingCatalog.For(source, target, merging);
            Assert.Equal(definitions.Count, definitions.Select(d => d.Key).Distinct().Count());
            foreach (var definition in definitions)
            {
                Assert.False(string.IsNullOrWhiteSpace(definition.Label), $"{source}→{target} {definition.Key} has no label");
                Assert.False(string.IsNullOrWhiteSpace(definition.Help), $"{source}→{target} {definition.Key} has no explanation");
                Assert.False(string.IsNullOrWhiteSpace(definition.Automatic));
                if (definition.Kind == SettingKind.Choice) Assert.True(definition.Choices.Count >= 2);
                if (definition.Kind == SettingKind.Number) Assert.True(definition.Max > definition.Min);
                if (definition.Suggested != null) Assert.Equal(definition.Suggested, definition.Normalize(definition.Suggested));
            }

            // what the bar at the bottom sets for all files has no second place in the panel
            Assert.DoesNotContain(definitions, d => d.InPanel && d.Key == K.Speed);

            // size and quality are set one way at a time: every way offered has its setting to
            // type (lossless needs none), and every such setting belongs to a way that is offered
            var ways = definitions.SingleOrDefault(d => d.Key == K.SizeMode);
            if (ways != null)
            {
                Assert.True(ways.ChoiceRequired);
                Assert.False(ways.OnCommandLine);
                Assert.Equal(SizeModes.Slider, ways.Choices[0].Value);
                foreach (var way in ways.Choices.Skip(1))
                    Assert.True(way.Value == SizeModes.Lossless || definitions.Any(d => d.Mode == way.Value && d.InPanel),
                        $"{source}→{target} offers \"{way.Label}\" with nothing to set");
            }
            foreach (var definition in definitions.Where(d => d.Mode != null))
                Assert.True(ways?.Choices.Any(c => c.Value == definition.Mode) == true,
                    $"{source}→{target} {definition.Key} belongs to a way that is not offered");
        }
    }

    [Fact]
    public void OnlyTheChosenWayOfSettingSizeAndQualityIsInForce()
    {
        var video = SettingCatalog.For("mov", "mp4");

        // a way chosen: its setting is kept, the others' are dropped, and so is the choice itself
        Assert.Equal(Values((K.VideoBitrate, "4000"), (K.MaxWidth, "1280")), SettingCatalog.Resolve(video,
            Values((K.SizeMode, SizeModes.Bitrate), (K.Crf, "20"), (K.VideoBitrate, "4000"), (K.TargetSizeMb, "10"), (K.MaxWidth, "1280"))));
        // the slider chosen: none of them
        Assert.Empty(SettingCatalog.Resolve(video, Values((K.SizeMode, SizeModes.Slider), (K.Crf, "20"))));

        // saved by 2.5.0, before there was a choice: the setting that took precedence then
        Assert.Equal(SizeModes.Size, SettingCatalog.ModeOf(video, Values((K.Crf, "20"), (K.TargetSizeMb, "10"))));
        Assert.Equal(Values((K.TargetSizeMb, "10")), SettingCatalog.Resolve(video, Values((K.Crf, "20"), (K.TargetSizeMb, "10"))));
        Assert.Equal(SizeModes.Quality, SettingCatalog.ModeOf(video, Values((K.Crf, "20"))));
        Assert.Equal(SizeModes.Slider, SettingCatalog.ModeOf(video, Values((K.MaxWidth, "1280"))));

        // a way this conversion does not have is none: avi has no quality scale
        Assert.Equal(SizeModes.Slider, SettingCatalog.ModeOf(SettingCatalog.For("mov", "avi"), Values((K.SizeMode, SizeModes.Quality))));

        // lossless WebP is a way with nothing to type: it stands for lossless=on
        var webp = SettingCatalog.For("png", "webp");
        Assert.Equal(Values((K.Lossless, "on")), SettingCatalog.Resolve(webp, Values((K.SizeMode, SizeModes.Lossless), (K.Quality, "80"))));
        Assert.Equal(Values((K.Quality, "80")), SettingCatalog.Resolve(webp, Values((K.Lossless, "off"), (K.Quality, "80"))));
        Assert.Equal(SizeModes.Lossless, SettingCatalog.ModeOf(webp, Values((K.Lossless, "on"))));

        // whether the File size slider still decides
        Assert.True(SettingCatalog.ReplacesSlider(video, SettingCatalog.Resolve(video, Values((K.Crf, "20")))));
        Assert.False(SettingCatalog.ReplacesSlider(video, SettingCatalog.Resolve(video, Values((K.SizeMode, SizeModes.Quality)))));
        Assert.False(SettingCatalog.ReplacesSlider(video, Values((K.MaxWidth, "1280"))));
    }

    [Fact]
    public void ThePanelAndTheCommandLineEachHaveTheirOwnSettings()
    {
        // the bar at the bottom decides speed: the panel has no second speed, a script still may
        Assert.DoesNotContain(K.Speed, PanelKeys("mov", "mp4"));
        Assert.Contains(K.Speed, Keys("mov", "mp4"));
        Assert.DoesNotContain(K.Compression, PanelKeys("wav", "flac"));
        Assert.Contains(K.Compression, PanelKeys("png", "tiff")); // which TIFF compression is a format matter, not speed

        // the panel's way of choosing is its own; a script sets the values directly
        var choice = SettingCatalog.For("mov", "mp4").Single(d => d.Key == K.SizeMode);
        Assert.True(choice.InPanel);
        Assert.False(choice.OnCommandLine);
        Assert.Equal([SizeModes.Slider, SizeModes.Quality, SizeModes.Bitrate, SizeModes.Size], choice.Choices.Select(c => c.Value));
        Assert.Equal([SizeModes.Slider, SizeModes.Bitrate, SizeModes.Size],
            SettingCatalog.For("mov", "avi").Single(d => d.Key == K.SizeMode).Choices.Select(c => c.Value));
        // pages of a PDF cannot be aimed at a size in kilobytes, so that is not offered for them
        Assert.Equal([SizeModes.Slider, SizeModes.Quality],
            SettingCatalog.For("pdf", "jpg").Single(d => d.Key == K.SizeMode).Choices.Select(c => c.Value));
        Assert.DoesNotContain(K.TargetSizeKb, Keys("pdf", "jpg"));
        // nothing to compress any other way: no choice
        Assert.DoesNotContain(K.SizeMode, Keys("mp3", "flac"));
        Assert.DoesNotContain(K.SizeMode, Keys("txt", "tar"));
    }

    [Fact]
    public void TypedValuesAreTidiedOrRejected()
    {
        var quality = SettingCatalog.For("png", "jpg").Single(d => d.Key == K.Quality);
        Assert.Equal("85", quality.Normalize(" 85 "));
        Assert.Equal("100", quality.Normalize("250"));   // clamped into range
        Assert.Equal("1", quality.Normalize("-4"));
        Assert.Equal("73", quality.Normalize("72.6"));   // whole numbers only
        Assert.Null(quality.Normalize("high"));
        Assert.Null(quality.Normalize(""));

        var megabytes = SettingCatalog.For("mov", "mp4").Single(d => d.Key == K.TargetSizeMb);
        Assert.Equal("9.5", megabytes.Normalize("9,5"));  // a decimal comma is a decimal point

        var chroma = SettingCatalog.For("png", "jpg").Single(d => d.Key == K.Chroma);
        Assert.Equal("420", chroma.Normalize("420"));
        Assert.Null(chroma.Normalize("411"));
    }

    [Fact]
    public void AFilesOwnSettingsWinOverItsFormats()
    {
        var format = new Dictionary<string, string> { [K.Quality] = "80", [K.MaxWidth] = "1920", [K.Chroma] = " " };
        var file = new Dictionary<string, string> { [K.Quality] = "55" };

        var merged = DetailSettings.Merge(format, file);
        Assert.Equal(55, merged.Int(K.Quality));
        Assert.Equal(1920, merged.Int(K.MaxWidth));
        Assert.False(merged.Has(K.Chroma)); // blank means automatic

        Assert.True(DetailSettings.Merge(null, null).IsEmpty);
        Assert.Same(DetailSettings.None, DetailSettings.Merge(new Dictionary<string, string>(), null));
    }

    [Fact]
    public void BuiltInPresetsOnlyUseSettingsThatExist()
    {
        // one representative conversion per kind of result, including the special sources
        var examples = new Dictionary<string, (string Source, string Target)[]>
        {
            ["image"] = [("png", "jpg"), ("pdf", "jpg")],
            ["video"] = [("mov", "mp4"), ("mov", "mkv")],
            ["audio"] = [("wav", "mp3")],
            ["gif"] = [("mp4", "gif")],
            ["pdf"] = [("jpg", "pdf")],
            ["archive"] = [("txt", "7z")]
        };

        foreach (var preset in SettingCatalog.BuiltInPresets)
        {
            Assert.True(examples.ContainsKey(preset.Kind), $"preset \"{preset.Name}\" has an unknown kind");
            var definitions = examples[preset.Kind].SelectMany(e => SettingCatalog.For(e.Source, e.Target)).ToList();
            foreach (var (key, value) in preset.Values)
            {
                var matching = definitions.Where(d => d.Key == key).ToList();
                Assert.True(matching.Count > 0, $"preset \"{preset.Name}\" sets {key}, which no {preset.Kind} conversion has");
                Assert.True(matching.Any(d => d.Normalize(value) == value),
                    $"preset \"{preset.Name}\" sets {key} to \"{value}\", which is not a valid value");
            }
        }
    }

    /// <summary>A speed of its own reaches a conversion only from a script (--set speed=…), not from the panel.</summary>
    [Fact]
    public void ASpeedSettingOverridesTheBatchSpeedForThatFileOnly()
    {
        var options = new ConversionOptions { Speed = SpeedPreset.Balanced };
        Assert.Equal(SpeedPreset.Fastest, options.WithDetail(Samples.Settings((K.Speed, "fastest"))).Speed);
        Assert.Equal(SpeedPreset.Balanced, options.WithDetail(Samples.Settings((K.Quality, "80"))).Speed);
    }
}
