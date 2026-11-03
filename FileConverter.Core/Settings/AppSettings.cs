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

    /// <summary>Is the Format settings panel open? (Named after its first name, so older settings files keep it.)</summary>
    public bool DetailedSettings { get; set; }

    /// <summary>Precise settings per target format ("jpg", "mp4", …): setting key → value. Missing = automatic.</summary>
    public Dictionary<string, Dictionary<string, string>> FormatSettings { get; set; } = [];

    /// <summary>Presets the user saved.</summary>
    public List<SettingsPreset> Presets { get; set; } = [];

    /// <summary>The target last chosen for each source type, offered again next time ("png" → "webp").</summary>
    public Dictionary<string, string> LastTargets { get; set; } = [];

    /// <summary>The window's colours: one of <see cref="ColourSchemes"/>.</summary>
    public string Colours { get; set; } = ColourSchemes.Default;

    /// <summary>
    /// Look once at each start whether a newer version is out? Nothing as long as the user has not
    /// been asked; after a "no" the app never asks again and never connects for this.
    /// </summary>
    public bool? NotifyAboutUpdates { get; set; }

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}

/// <summary>One set of the window's colours, as #RRGGBB.</summary>
public sealed record ColourScheme(
    string Id, string Name, string Background, string Surface, string Text, string Dim,
    string Line, string Success, string Warning, string Error);

/// <summary>
/// The colours the window can be given, in its menu: plain lines on black, and the palettes known
/// from Linux desktops and terminals. The dim text of some is a little lighter than the original
/// palette's, which is meant for comments in code and not for text that has to be read.
/// </summary>
public static class ColourSchemes
{
    public const string Default = "white";

    public static IReadOnlyList<ColourScheme> All { get; } =
    [
        new("white", "White", "#0B0B0C", "#151517", "#E8E8E8", "#909096", "#FFFFFF", "#5FD38D", "#FFB000", "#FF6E6E"),
        new("green", "Green", "#07100A", "#0E1B13", "#D3F5DC", "#74A384", "#5FF287", "#5FF287", "#E6D15C", "#FF6E6E"),
        new("amber", "Amber", "#100B04", "#1C140A", "#FFE6BA", "#AD9163", "#FFB000", "#B9D96C", "#FFB000", "#FF7A6B"),
        new("tokyo-night", "Tokyo Night", "#1A1B26", "#24283B", "#C0CAF5", "#8089B3", "#7AA2F7", "#9ECE6A", "#E0AF68", "#F7768E"),
        new("catppuccin", "Catppuccin", "#1E1E2E", "#2A2B3C", "#CDD6F4", "#9399B2", "#CBA6F7", "#A6E3A1", "#F9E2AF", "#F38BA8"),
        new("gruvbox", "Gruvbox", "#1D2021", "#282828", "#EBDBB2", "#A89984", "#FE8019", "#B8BB26", "#FABD2F", "#FB4934"),
        new("nord", "Nord", "#2E3440", "#3B4252", "#ECEFF4", "#A7B1C4", "#88C0D0", "#A3BE8C", "#EBCB8B", "#E5949C"),
        new("dracula", "Dracula", "#282A36", "#343746", "#F8F8F2", "#98A2CE", "#BD93F9", "#50FA7B", "#F1FA8C", "#FF6E6E")
    ];

    public static ColourScheme Find(string id) => All.FirstOrDefault(scheme => scheme.Id == id) ?? All[0];
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
        if (!ColourSchemes.All.Any(c => c.Id == settings.Colours)) settings.Colours = ColourSchemes.Default;
        settings.FormatSettings ??= [];
        settings.Presets ??= [];
        settings.LastTargets ??= [];
        settings.Presets.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Name) || p.Values == null);
        foreach (var key in settings.FormatSettings.Where(pair => pair.Value == null).Select(pair => pair.Key).ToList())
            settings.FormatSettings.Remove(key);
        return settings;
    }
}
