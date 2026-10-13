using FileConverter.Core.Platform;

namespace FileConverter.Core;

/// <summary>
/// Up to version 2.0 the app had another name and kept its settings and downloaded engines in
/// folders of that name. The first time this version runs — while it has no folder of its own
/// yet — it takes those over: the settings are copied, and each engine gets a second name in the
/// new place (the same file on disk, so nothing is downloaded or stored twice). The old folders
/// are left exactly as they were; on Windows another program keeps its own files in the same one.
/// </summary>
public static class EarlierVersion
{
    public static void Adopt(IPlatform platform)
    {
        if (platform.EarlierDirectories is not { } earlier) return;
        Adopt(earlier.Data, earlier.Config, platform.DataDirectory, platform.ConfigDirectory, platform.TryCreateHardLink);
    }

    internal static void Adopt(string earlierData, string earlierConfig, string data, string config,
        Func<string, string, bool> tryLink)
    {
        try
        {
            // both asked before either is made: on Windows the two are one folder
            bool settings = !Directory.Exists(config);
            bool engines = !Directory.Exists(data);

            if (settings && File.Exists(Path.Combine(earlierConfig, "settings.json")))
            {
                Directory.CreateDirectory(config);
                File.Copy(Path.Combine(earlierConfig, "settings.json"), Path.Combine(config, "settings.json"));
            }

            var earlierTools = Path.Combine(earlierData, "tools");
            if (engines && Directory.Exists(earlierTools))
            {
                var tools = Path.Combine(data, "tools");
                Directory.CreateDirectory(tools);
                foreach (var file in Directory.EnumerateFiles(earlierTools))
                {
                    if (Path.GetExtension(file) is ".tmp" or ".new") continue; // a download that never finished
                    var copy = Path.Combine(tools, Path.GetFileName(file));
                    if (!tryLink(copy, file)) File.Copy(file, copy);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // then this version starts as on a new machine: default settings, and it offers the engines again
        }
    }
}
