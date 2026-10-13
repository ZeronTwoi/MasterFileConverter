namespace FileConverter.Core;

/// <summary>
/// The files the app writes while it works and removes when it is done: an unpacked archive, a
/// palette, an encoder's first-pass notes. They lie in the system's temp folder and carry the
/// app's name, so that what a crash or a power cut left behind can be told from everything else
/// there — and cleared away, by the app at a later start or by its uninstaller.
/// </summary>
public static class WorkFiles
{
    private static string Prefix => AppInfo.ProgramName + "-";

    /// <summary>A name nothing else has: "…/MasterFileConverter-pack-1a2b3c…".</summary>
    public static string NewPath(string purpose, string extension = "") =>
        Path.Combine(Path.GetTempPath(), $"{Prefix}{purpose}-{Guid.NewGuid():N}{extension}");

    /// <summary>
    /// Removes work files older than <paramref name="age"/>. Nothing the app does takes that long,
    /// so they belong to a run that ended without tidying up.
    /// </summary>
    public static void RemoveLeftovers(TimeSpan age) => RemoveLeftovers(Path.GetTempPath(), age);

    internal static void RemoveLeftovers(string tempFolder, TimeSpan age)
    {
        try
        {
            var limit = DateTime.UtcNow - age;
            foreach (var entry in Directory.EnumerateFileSystemEntries(tempFolder, Prefix + "*"))
            {
                try
                {
                    bool folder = Directory.Exists(entry);
                    if ((folder ? Directory.GetLastWriteTimeUtc(entry) : File.GetLastWriteTimeUtc(entry)) > limit) continue;

                    if (folder) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // in use after all, or another user's on a shared temp folder
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp folder that cannot be listed has nothing of ours to remove
        }
    }
}
