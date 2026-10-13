using FileConverter.Core.Platform;

namespace FileConverter.Core;

/// <summary>One-time start-up for the conversion engines; every host (app, command line, tests) calls it first.</summary>
public static class Engines
{
    private static int _initialized;

    /// <summary>
    /// How to start another copy of this program — the executable, then any arguments that have to
    /// come first — or null when the host is not the app itself (a test runner). Work that cannot
    /// share a process, such as drawing PDF pages, is handed to such copies.
    /// </summary>
    public static IReadOnlyList<string>? SelfCommand { get; private set; }

    /// <param name="selfCommand">See <see cref="SelfCommand"/>.</param>
    public static void Initialize(IPlatform platform, IReadOnlyList<string>? selfCommand = null)
    {
        PlatformServices.Use(platform);
        if (selfCommand is { Count: > 0 }) SelfCommand = selfCommand;
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        EarlierVersion.Adopt(platform);

        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = true;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
    }
}
