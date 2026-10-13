using System.Diagnostics;

namespace FileConverter.Core.Platform;

/// <summary>External programs the engines can drive.</summary>
public enum ExternalTool
{
    Ffmpeg,
    Ffprobe,
    SevenZip,
    Rar,
    LibreOffice
}

/// <summary>
/// Everything that differs between operating systems. The engines only ever talk to this
/// interface; each download ships exactly one implementation (the project FileConverter.Platform.Windows
/// or FileConverter.Platform.Linux), so neither carries the other's code.
/// </summary>
public interface IPlatform
{
    /// <summary>"Windows" or "Linux", for messages.</summary>
    string Name { get; }

    /// <summary>Downloaded engines and measurement caches.</summary>
    string DataDirectory { get; }

    /// <summary>The user's settings.</summary>
    string ConfigDirectory { get; }

    /// <summary>The same two folders as a version under the app's earlier name knew them, if there was one here.</summary>
    (string Data, string Config)? EarlierDirectories { get; }

    /// <summary>File names a tool goes by, most preferred first (looked up in the tools folder and on PATH).</summary>
    IReadOnlyList<string> ExecutableNames(ExternalTool tool);

    /// <summary>Fixed install locations worth checking besides the tools folder and PATH.</summary>
    IReadOnlyList<string> KnownLocations(ExternalTool tool);

    /// <summary>Where a program can write output that should be thrown away.</summary>
    string NullDevice { get; }

    /// <summary>How this system's file names compare: ignoring case on Windows, exactly on Linux.</summary>
    StringComparison PathComparison { get; }

    /// <summary>Code page of text files saved without Unicode (Windows-1254 Turkish, 1252 Western, …).</summary>
    int LegacyCodePage { get; }

    /// <summary>Code page of file names in zip archives written by older Windows tools (437 US, 850 Western, 857 Turkish, …).</summary>
    int ArchiveNameCodePage { get; }

    /// <summary>A laptop or tablet: its CPU can't sustain a short benchmark's pace.</summary>
    bool IsBatteryPowered { get; }

    /// <summary>Changes when a GPU or its driver changes, so encoder measurements are redone.</summary>
    string GpuFingerprint();

    /// <summary>Hardware video encoders that can exist on this system, in order of preference.</summary>
    IReadOnlyList<GpuEncoder> GpuEncoders { get; }

    /// <summary>FFmpeg options naming the device a hardware encoder runs on, where it has to be told.</summary>
    IReadOnlyList<string> EncoderDeviceArguments(GpuEncoder gpu);

    /// <summary>Installed fonts to fall back on for characters the bundled PDF font lacks.</summary>
    IReadOnlyList<string> PdfFallbackFonts { get; }

    /// <summary>An installed office suite that can be automated in-process (Microsoft Word), if any.</summary>
    IOfficeAutomation? OfficeAutomation { get; }

    /// <summary>Orders two file names the way this system's file manager does.</summary>
    int CompareFileNames(string a, string b);

    /// <summary>Makes a second name for an existing file instead of copying it; false when that isn't possible.</summary>
    bool TryCreateHardLink(string linkPath, string existingFile);

    /// <summary>Connects a windowed program to the terminal it was started from, where that is needed.</summary>
    void AttachToParentConsole();

    /// <summary>Opens a file or folder with the program the user has chosen for it.</summary>
    void Open(string path);

    /// <summary>Opens the containing folder with the file selected.</summary>
    void ShowInFileManager(string path);

    /// <summary>What "Set up engines" will fetch, e.g. "FFmpeg (~100 MB)".</summary>
    string FfmpegDownloadSummary { get; }

    /// <summary>Downloads FFmpeg and FFprobe into <paramref name="toolsDirectory"/>.</summary>
    Task InstallFfmpegAsync(string toolsDirectory, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Downloads a 7-Zip console build into <paramref name="toolsDirectory"/>.</summary>
    Task InstallSevenZipAsync(string toolsDirectory, CancellationToken ct);

    /// <summary>The program a person starts, as a download names it ("MasterFileConverter.exe", "masterfileconverter").</summary>
    string LauncherName { get; }

    /// <summary>
    /// How this copy of the app can be replaced by a newer version, or null when it cannot replace
    /// itself: a build run out of its build folder, or a folder it may not write to.
    /// </summary>
    UpdateRoute? UpdateRoute { get; }

    /// <summary>How the installer of a newer version is run without its pages (Windows).</summary>
    /// <param name="restart">Start the app again when it is through.</param>
    /// <param name="unattended">Nobody is watching: it keeps even its error messages to itself.</param>
    ProcessStartInfo InstallerCommand(string installer, bool restart, bool unattended);

    /// <summary>
    /// Called by a newer version once it has put itself into <paramref name="folder"/>: whatever
    /// else an installation there consists of — on Linux the menu entry, the icons and the command.
    /// </summary>
    void AfterUpdate(string folder);

    /// <summary>
    /// Takes an installed copy off the computer again, where the app does that itself (Linux; on
    /// Windows it is "Installed apps"). False when this copy was never installed.
    /// </summary>
    /// <param name="keepSettings">Leave the settings for a later installation.</param>
    bool Uninstall(bool keepSettings);
}

/// <summary>High-fidelity document conversion through an installed office program.</summary>
public interface IOfficeAutomation
{
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>Does the program read the one format and write the other? (Extensions without the dot.)</summary>
    bool CanConvert(string sourceExtension, string targetExtension);

    /// <summary>Returns false when the program is unavailable or the conversion failed.</summary>
    Task<bool> ConvertAsync(string input, string output, string targetExtension, CancellationToken ct);
}

/// <summary>The platform implementation in use; set once at start-up.</summary>
public static class PlatformServices
{
    private static IPlatform? _current;

    public static IPlatform Current =>
        _current ?? throw new InvalidOperationException("PlatformServices.Use(...) must be called at start-up.");

    public static void Use(IPlatform platform) => _current = platform;

    public static StringComparer PathComparer =>
        Current.PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}

/// <summary>Behaviour both systems share; each platform overrides what differs.</summary>
public abstract class PlatformBase : IPlatform
{
    public abstract string Name { get; }
    public abstract string DataDirectory { get; }
    public virtual string ConfigDirectory => DataDirectory;
    public virtual (string Data, string Config)? EarlierDirectories => null;

    public abstract IReadOnlyList<string> ExecutableNames(ExternalTool tool);
    public virtual IReadOnlyList<string> KnownLocations(ExternalTool tool) => [];

    public abstract string NullDevice { get; }
    public abstract StringComparison PathComparison { get; }
    public abstract int LegacyCodePage { get; }
    public virtual int ArchiveNameCodePage => 437;
    public virtual bool IsBatteryPowered => false;
    public virtual string GpuFingerprint() => "";
    public abstract IReadOnlyList<GpuEncoder> GpuEncoders { get; }
    public virtual IReadOnlyList<string> EncoderDeviceArguments(GpuEncoder gpu) => [];
    public abstract IReadOnlyList<string> PdfFallbackFonts { get; }
    public virtual IOfficeAutomation? OfficeAutomation => null;

    public virtual int CompareFileNames(string a, string b) => NaturalSort.CompareManaged(a, b);

    public abstract bool TryCreateHardLink(string linkPath, string existingFile);

    public virtual void AttachToParentConsole() { }

    public virtual void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public virtual void ShowInFileManager(string path) =>
        Open(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);

    public abstract string FfmpegDownloadSummary { get; }
    public abstract Task InstallFfmpegAsync(string toolsDirectory, IProgress<double>? progress, CancellationToken ct);
    public abstract Task InstallSevenZipAsync(string toolsDirectory, CancellationToken ct);

    public abstract string LauncherName { get; }
    public virtual UpdateRoute? UpdateRoute => null;

    public virtual ProcessStartInfo InstallerCommand(string installer, bool restart, bool unattended) =>
        throw new NotSupportedException("This system has no installer.");

    public virtual void AfterUpdate(string folder) { }
    public virtual bool Uninstall(bool keepSettings) => false;

    /// <summary>Can files be made in this folder? (Tried, not read from its permissions: those do not tell on every file system.)</summary>
    protected static bool CanWriteTo(string folder)
    {
        try
        {
            using (File.Create(Path.Combine(folder, $".write-{Guid.NewGuid():N}.tmp"), 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
