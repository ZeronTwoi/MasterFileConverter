using System.Globalization;

namespace FileConverter.Core.Settings;

/// <summary>Names of the precise settings. A setting that is absent means "automatic".</summary>
public static class SettingKeys
{
    // pictures (and every picture a conversion writes: frames, PDF pages)
    public const string Quality = "quality";
    public const string Lossless = "lossless";
    public const string Chroma = "chroma";
    public const string Progressive = "progressive";
    public const string Compression = "compression";
    public const string Colors = "colors";
    public const string Background = "background";
    public const string Metadata = "metadata";
    public const string MaxWidth = "maxWidth";
    public const string MaxHeight = "maxHeight";
    public const string Scale = "scale";
    public const string TargetSizeKb = "targetKb";

    // video
    public const string VideoCodec = "videoCodec";
    public const string Crf = "crf";
    public const string VideoBitrate = "videoBitrate";
    public const string TargetSizeMb = "targetMb";
    public const string Fps = "fps";
    public const string AudioCodec = "audioCodec";
    public const string Subtitles = "subtitles";
    public const string Speed = "speed";

    // audio
    public const string AudioBitrate = "audioBitrate";
    public const string VbrQuality = "vbrQuality";
    public const string SampleRate = "sampleRate";
    public const string Channels = "channels";
    public const string BitDepth = "bitDepth";

    // animation and frame sequences
    public const string Dither = "dither";
    public const string Loop = "loop";
    public const string FrameSeconds = "frameSeconds";
    public const string MaxFrames = "maxFrames";

    // documents
    public const string Dpi = "dpi";
    public const string Pages = "pages";
    public const string PageSize = "pageSize";
    public const string Margin = "margin";
    public const string FontSize = "fontSize";
    public const string Engine = "engine";
    public const string Sheets = "sheets";
    public const string Separator = "separator";

    // archives
    public const string Level = "level";
    public const string Password = "password";
    public const string Solid = "solid";
}

/// <summary>
/// The precise settings one conversion runs with: setting key → value as text. Engines ask for
/// what they understand and treat everything absent as "decide for me" — so with no settings at
/// all a conversion behaves exactly as it always has.
/// </summary>
public sealed class DetailSettings
{
    public static readonly DetailSettings None = new(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _values;

    public DetailSettings(IReadOnlyDictionary<string, string> values) => _values = values;

    /// <summary>A format's saved settings with one file's own settings laid over them.</summary>
    public static DetailSettings Merge(IReadOnlyDictionary<string, string>? formatSettings,
        IReadOnlyDictionary<string, string>? fileSettings)
    {
        if (formatSettings is not { Count: > 0 } && fileSettings is not { Count: > 0 }) return None;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in formatSettings ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(pair.Value)) merged[pair.Key] = pair.Value.Trim();
        foreach (var pair in fileSettings ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(pair.Value)) merged[pair.Key] = pair.Value.Trim();
        return merged.Count == 0 ? None : new DetailSettings(merged);
    }

    public bool IsEmpty => _values.Count == 0;
    public IReadOnlyDictionary<string, string> Values => _values;

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Text(string key) => _values.TryGetValue(key, out var value) ? value : null;

    /// <summary>The value as a number, or null when it is absent or not a number.</summary>
    public double? Number(string key) =>
        _values.TryGetValue(key, out var value) &&
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
        double.IsFinite(number)
            ? number
            : null;

    public int? Int(string key) => Number(key) is { } number ? (int)Math.Round(number) : null;

    /// <summary>The whole number within <paramref name="min"/>…<paramref name="max"/>, or null when not set.</summary>
    public int? Int(string key, int min, int max) => Int(key) is { } value ? Math.Clamp(value, min, max) : null;

    /// <summary>An on/off choice: true for "on", false for "off", null when not set.</summary>
    public bool? Flag(string key) => Text(key) switch
    {
        "on" => true,
        "off" => false,
        _ => null
    };

    /// <summary>True when the setting is present with exactly this value.</summary>
    public bool Is(string key, string value) => Text(key) == value;
}
