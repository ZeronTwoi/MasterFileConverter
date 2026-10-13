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

        // png has no quality to set, only effort
        Assert.DoesNotContain(K.Quality, Keys("jpg", "png"));
        Assert.Contains(K.Compression, Keys("jpg", "png"));

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
        Assert.Equal([K.Level, K.Password, K.Solid], Keys("txt", "7z"));
        Assert.Equal([K.Level, K.Password], Keys("txt", "zip"));
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
        var sources = new[] { "jpg", "png", "gif", "webp", "heic", "mp3", "wav", "mp4", "mkv", "txt", "md", "pdf", "docx", "doc", "zip", "7z", "xyz" };
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
            }
        }
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

    [Fact]
    public void ASpeedSettingOverridesTheBatchSpeedForThatFileOnly()
    {
        var options = new ConversionOptions { Speed = SpeedPreset.Balanced };
        Assert.Equal(SpeedPreset.Fastest, options.WithDetail(Samples.Settings((K.Speed, "fastest"))).Speed);
        Assert.Equal(SpeedPreset.Balanced, options.WithDetail(Samples.Settings((K.Quality, "80"))).Speed);
    }
}
