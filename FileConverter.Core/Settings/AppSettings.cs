using System.Text.Json;
using System.Text.Json.Serialization;
using FileConverter.Core.Platform;

namespace FileConverter.Core.Settings;

/// <summary>What to do when the name a result would get is already taken.</summary>
public enum ExistingFileAction
{
    /// <summary>Keep the old file and number the new one: "photo (1).jpg".</summary>
    KeepBoth,
    Overwrite,
    Skip
}

/// <summary>Everything the app remembers between runs. Saved as settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Raised when the format changes in a way old files need converting for.</summary>
    public int Version { get; set; } = 1;

    public int SizePercent { get; set; } = 100;
    public SpeedPreset Speed { get; set; } = SpeedPreset.Balanced;
    public bool AutoSpeed { get; set; }
    public bool UseGpu { get; set; } = true;
    public bool CombineFiles { get; set; } = true;
    public string? OutputDirectory { get; set; }
    public ExistingFileAction WhenFileExists { get; set; } = ExistingFileAction.KeepBoth;

    /// <summary>Is the Detailed Settings panel open?</summary>
    public bool DetailedSettings { get; set; }

    /// <summary>Precise settings per target format ("jpg", "mp4", …): setting key → value. Missing = automatic.</summary>
    public Dictionary<string, Dictionary<string, string>> FormatSettings { get; set; } = [];

    /// <summary>Presets the user saved.</summary>
    public List<SettingsPreset> Presets { get; set; } = [];

    /// <summary>The target last chosen for each source type, offered again next time ("png" → "webp").</summary>
    public Dictionary<string, string> LastTargets { get; set; } = [];

    /// <summary>"dark", "light" or "system".</summary>
    public string Theme { get; set; } = "dark";

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}

/// <summary>A named bundle of precise settings for one kind of target ("Fit in 10 MB" for video).</summary>
public sealed class SettingsPreset
{
    public string Name { get; set; } = "";

    /// <summary>The kind of target it applies to: image, video, audio, gif, pdf, document or archive.</summary>
    public string Kind { get; set; } = "";

    public Dictionary<string, string> Values { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;

/// <summary>
/// Reads and writes settings.json in the platform's configuration folder. A file that can't be
/// read is set aside rather than overwritten, and a save replaces the old file in one step, so a
/// crash or a full disk never leaves half a file behind.
/// </summary>
public sealed class SettingsStore
{
    public string FilePath { get; }

    public SettingsStore(string? filePath = null) =>
        FilePath = filePath ?? Path.Combine(PlatformServices.Current.ConfigDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJson.Default.AppSettings);
            return Normalize(loaded ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // keep the unreadable file for the user to look at; start from defaults
            try { File.Move(FilePath, FilePath + ".unreadable", overwrite: true); } catch { /* best effort */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppSettingsJson.Default.AppSettings));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are a convenience; a read-only profile must not break converting
        }
    }

    /// <summary>Brings hand-edited or older files back into the ranges the app expects.</summary>
    private static AppSettings Normalize(AppSettings settings)
    {
        settings.SizePercent = Math.Clamp(settings.SizePercent, 10, 100);
        if (!Enum.IsDefined(settings.Speed)) settings.Speed = SpeedPreset.Balanced;
        if (!Enum.IsDefined(settings.WhenFileExists)) settings.WhenFileExists = ExistingFileAction.KeepBoth;
        if (settings.Theme is not ("dark" or "light" or "system")) settings.Theme = "dark";
        settings.FormatSettings ??= [];
        settings.Presets ??= [];
        settings.LastTargets ??= [];
        settings.Presets.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name) || p.Values == null);
        foreach (var key in settings.FormatSettings.Where(pair => pair.Value == null).Select(pair => pair.Key).ToList())
            settings.FormatSettings.Remove(key);
        return settings;
    }
}
