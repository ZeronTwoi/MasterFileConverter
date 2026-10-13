using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using FileConverter.Core;
using FileConverter.Core.Platform;
using SharpCompress.Readers;

[assembly: SupportedOSPlatform("linux")]

namespace FileConverter.Platform;

/// <summary>The platform this build of the app runs on.</summary>
public static class HostPlatform
{
    public static IPlatform Create() => new LinuxPlatform();
}

internal sealed partial class LinuxPlatform : PlatformBase
{
    public override string Name => "Linux";

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string newPath);

    /// <summary>Fails across file systems; the caller copies instead.</summary>
    public override bool TryCreateHardLink(string linkPath, string existingFile)
    {
        try
        {
            return Link(existingFile, linkPath) == 0;
        }
        catch
        {
            return false;
        }
    }

    // $XDG_DATA_HOME (~/.local/share) for engines and caches, $XDG_CONFIG_HOME (~/.config) for settings
    public override string DataDirectory { get; } =
        UserFolder(Environment.SpecialFolder.LocalApplicationData, ".local/share", AppInfo.UnixName);

    public override string ConfigDirectory { get; } =
        UserFolder(Environment.SpecialFolder.ApplicationData, ".config", AppInfo.UnixName);

    /// <summary>Version 2.0 was called FileConverter and kept its two folders under that name.</summary>
    public override (string Data, string Config)? EarlierDirectories { get; } = (
        UserFolder(Environment.SpecialFolder.LocalApplicationData, ".local/share", "fileconverter"),
        UserFolder(Environment.SpecialFolder.ApplicationData, ".config", "fileconverter"));

    /// <summary>
    /// A folder of the app's under one of the user's base folders. "DoNotVerify" matters: on a fresh
    /// account ~/.local/share may not exist yet, and without it .NET answers with an empty path —
    /// which would put the engines and settings wherever the program happened to be started from.
    /// </summary>
    private static string UserFolder(Environment.SpecialFolder folder, string homeRelativeDefault, string name)
    {
        var parent = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(parent))
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            parent = Path.Combine(string.IsNullOrEmpty(home) ? Path.GetTempPath() : home, homeRelativeDefault);
        }
        return Path.Combine(parent, name);
    }

    public override IReadOnlyList<string> ExecutableNames(ExternalTool tool) => tool switch
    {
        ExternalTool.Ffmpeg => ["ffmpeg"],
        ExternalTool.Ffprobe => ["ffprobe"],
        // 7zz is the official 7-Zip for Linux; 7z/7za/7zr come from the older p7zip packages
        ExternalTool.SevenZip => ["7zz", "7z", "7za", "7zr"],
        ExternalTool.Rar => ["rar"],
        ExternalTool.LibreOffice => ["soffice", "libreoffice"],
        _ => []
    };

    public override IReadOnlyList<string> KnownLocations(ExternalTool tool)
    {
        if (tool != ExternalTool.LibreOffice) return [];

        var locations = new List<string> { "/usr/lib/libreoffice/program/soffice", "/usr/lib64/libreoffice/program/soffice" };
        try
        {
            // the packages from libreoffice.org install to /opt/libreoffice<version>
            foreach (var dir in Directory.EnumerateDirectories("/opt", "libreoffice*").OrderDescending())
                locations.Add(Path.Combine(dir, "program", "soffice"));
        }
        catch
        {
            // no /opt
        }
        locations.Add("/snap/bin/libreoffice");
        return locations;
    }

    public override string NullDevice => "/dev/null";
    public override StringComparison PathComparison => StringComparison.Ordinal;

    /// <summary>
    /// Linux has no system "ANSI" code page; a text file that isn't Unicode almost always came from
    /// Windows, where the user's language decides it (tr → 1254, de/en → 1252, ru → 1251, …).
    /// </summary>
    public override int LegacyCodePage => CodePageForLocale(
        Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all ? all
        : Environment.GetEnvironmentVariable("LC_CTYPE") is { Length: > 0 } ctype ? ctype
        : Environment.GetEnvironmentVariable("LANG"));

    /// <summary>
    /// The Windows code page that goes with a locale such as "tr_TR.UTF-8". Worked out from the
    /// locale name rather than asked of .NET: the Linux build runs without the ICU libraries
    /// (so that it starts on any system), and without them .NET knows no culture's code page.
    /// </summary>
    internal static int CodePageForLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale)) return 1252;
        var name = locale.Split('.', '@')[0].Replace('-', '_').ToLowerInvariant();
        var language = name.Split('_')[0];
        return language switch
        {
            "cs" or "hu" or "pl" or "ro" or "hr" or "sk" or "sl" or "sq" or "bs" => 1250,
            "ru" or "uk" or "be" or "bg" or "mk" or "sr" or "kk" or "ky" or "mn" => 1251,
            "el" => 1253,
            "tr" or "az" => 1254,
            "he" or "iw" => 1255,
            "ar" or "fa" or "ur" => 1256,
            "et" or "lv" or "lt" => 1257,
            "vi" => 1258,
            "th" => 874,
            "ja" => 932,
            "ko" => 949,
            "zh" => name is "zh_tw" or "zh_hk" or "zh_mo" ? 950 : 936,
            _ => 1252
        };
    }

    /// <summary>A system battery (not a wireless mouse's) under /sys/class/power_supply.</summary>
    public override bool IsBatteryPowered
    {
        get
        {
            try
            {
                foreach (var supply in Directory.EnumerateDirectories("/sys/class/power_supply"))
                {
                    if (ReadLine(Path.Combine(supply, "type")) != "Battery") continue;
                    if (ReadLine(Path.Combine(supply, "scope")) == "Device") continue;
                    return true;
                }
            }
            catch
            {
                // no sysfs (container) — treat as mains-powered
            }
            return false;
        }
    }

    /// <summary>Graphics cards, the NVIDIA driver version and the kernel (which carries the other GPU drivers).</summary>
    public override string GpuFingerprint()
    {
        var parts = new List<string>();
        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                var device = Path.Combine(card, "device");
                if (ReadLine(Path.Combine(device, "vendor")) is { } vendor)
                    parts.Add($"{vendor}:{ReadLine(Path.Combine(device, "device"))}");
            }
        }
        catch
        {
            // no DRM devices
        }
        if (ReadLine("/proc/driver/nvidia/version") is { } nvidia) parts.Add(nvidia);
        if (ReadLine("/proc/sys/kernel/osrelease") is { } kernel) parts.Add(kernel);
        return string.Join(";", parts.Distinct());
    }

    private static string? ReadLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    // AMD's AMF encoder is Windows-only; on Linux, AMD and Intel GPUs encode through VA-API
    public override IReadOnlyList<GpuEncoder> GpuEncoders { get; } = [GpuEncoder.Nvidia, GpuEncoder.Intel, GpuEncoder.Vaapi];

    /// <summary>VA-API has to be pointed at a render node; the first one is the primary graphics card.</summary>
    public override IReadOnlyList<string> EncoderDeviceArguments(GpuEncoder gpu)
    {
        if (gpu != GpuEncoder.Vaapi) return [];
        string node = "/dev/dri/renderD128";
        try
        {
            node = Directory.EnumerateFiles("/dev/dri", "renderD*").Order(StringComparer.Ordinal).FirstOrDefault() ?? node;
        }
        catch
        {
            // no /dev/dri: the test encode fails and VA-API is simply not used
        }
        return ["-vaapi_device", node];
    }

    public override IReadOnlyList<string> PdfFallbackFonts { get; } =
        ["Noto Sans", "DejaVu Sans", "Liberation Sans", "Noto Sans CJK SC", "Noto Sans CJK JP", "Noto Sans Arabic"];

    /// <summary>
    /// Asks the desktop's file manager to show the file selected (the org.freedesktop.FileManager1
    /// interface that Nautilus, Dolphin, Nemo and Thunar implement); otherwise just opens the folder.
    /// </summary>
    public override void ShowInFileManager(string path)
    {
        if (!Directory.Exists(path))
        {
            try
            {
                var psi = new ProcessStartInfo("dbus-send")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in new[]
                         {
                             "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                             "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                             $"array:string:{new Uri(path).AbsoluteUri}", "string:"
                         })
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process != null && process.WaitForExit(3000) && process.ExitCode == 0) return;
            }
            catch
            {
                // no dbus-send, or no file manager answering — open the folder instead
            }
        }
        base.ShowInFileManager(path);
    }

    // ---------- engine downloads ----------

    private const string FfmpegReleaseApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";
    private const string FfmpegFallbackName = "ffmpeg-master-latest-linux64-gpl.tar.xz";
    private const string FfmpegDownloadFolder = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";

    public override string FfmpegDownloadSummary => "FFmpeg (~145 MB)";

    /// <summary>
    /// A complete FFmpeg build (every encoder the app uses, plus NVENC/Quick Sync/VA-API) from the
    /// BtbN project, which needs nothing installed but glibc. The newest stable release branch is
    /// preferred over the development snapshot.
    /// </summary>
    public override async Task InstallFfmpegAsync(string toolsDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        // the snapshot has a fixed address, and the builds' own list of checksums names it
        var build = await FindFfmpegBuildAsync(ct) ?? new ReleaseFile(FfmpegFallbackName, FfmpegDownloadFolder + FfmpegFallbackName,
            await Downloads.FindChecksumAsync(FfmpegDownloadFolder + "checksums.sha256", FfmpegFallbackName, ct));
        var archive = Path.Combine(toolsDirectory, "ffmpeg-download.tmp");
        try
        {
            await Downloads.DownloadFileAsync(build, archive, progress, ct);
            var found = await Task.Run(() => ExtractExecutables(archive, toolsDirectory, ["ffmpeg", "ffprobe"], ct), ct);
            if (found.Count < 2)
                throw new FriendlyException("The FFmpeg download did not contain ffmpeg and ffprobe.");
        }
        finally
        {
            try { File.Delete(archive); } catch { /* best effort */ }
        }
    }

    private static async Task<ReleaseFile?> FindFfmpegBuildAsync(CancellationToken ct)
    {
        try
        {
            ReleaseFile? best = null;
            Version? bestVersion = null;
            foreach (var file in await Downloads.GetReleaseFilesAsync(FfmpegReleaseApi, ct))
            {
                // e.g. ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz
                var match = Regex.Match(file.Name, @"^ffmpeg-n(\d+)\.(\d+)-latest-linux64-gpl-[\d.]+\.tar\.xz$");
                if (!match.Success) continue;
                var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
                if (bestVersion == null || version > bestVersion)
                {
                    bestVersion = version;
                    best = file;
                }
            }
            return best;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // API unreachable or rate-limited — the snapshot build has a fixed address
        }
    }

    /// <summary>The official 7-Zip console build for Linux (7zz): every archive format in one ~3 MB file.</summary>
    public override async Task InstallSevenZipAsync(string toolsDirectory, CancellationToken ct)
    {
        var archive = Path.Combine(toolsDirectory, "7z-download.tmp");
        try
        {
            var package = await Downloads.FindSevenZipPackageAsync("-linux-x64.tar.xz", ct);
            await Downloads.DownloadFileAsync(package, archive, null, ct);
            var found = await Task.Run(() => ExtractExecutables(archive, toolsDirectory, ["7zz"], ct), ct);
            if (found.Count == 0)
                throw new FriendlyException("The 7-Zip download did not contain 7zz.");
        }
        finally
        {
            try { File.Delete(archive); } catch { /* best effort */ }
        }
    }

    /// <summary>Pulls the named programs out of a .tar.xz, wherever they sit in it, and marks them executable.</summary>
    private static List<string> ExtractExecutables(string archive, string destination, string[] names, CancellationToken ct)
    {
        var found = new List<string>();
        using var stream = File.OpenRead(archive);
        using var reader = ReaderFactory.Open(stream);
        while (found.Count < names.Length && reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory) continue;

            var name = Path.GetFileName(reader.Entry.Key ?? "");
            if (!names.Contains(name) || found.Contains(name)) continue;

            var target = Path.Combine(destination, name);
            var staging = target + ".new";
            using (var file = File.Create(staging))
                reader.WriteEntryTo(file);
            File.SetUnixFileMode(staging,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(staging, target, overwrite: true);
            found.Add(name);
        }
        return found;
    }

    // ---------- updating and uninstalling ----------

    public override string LauncherName => AppInfo.UnixName;

    /// <summary>The download, unpacked over the copy that is here — installed by install.sh or run where it was unpacked.</summary>
    public override UpdateRoute? UpdateRoute =>
        AppUpdater.FolderOfThisCopy(LauncherName) is { } folder && CanWriteTo(folder)
            ? new UpdateRoute(folder, "-linux-x64.tar.gz", UpdateKind.Archive)
            : null;

    /// <summary>Where install.sh puts the app.</summary>
    private static string InstalledFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/", ".local", "lib", AppInfo.UnixName);

    private static bool IsInstalledFolder(string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(folder), InstalledFolder, StringComparison.Ordinal);

    /// <summary>An installed copy has a menu entry, icons and a command besides its folder: the new version's script renews them.</summary>
    public override void AfterUpdate(string folder)
    {
        if (IsInstalledFolder(folder)) RunInstallScript(folder, quiet: true);
    }

    public override bool Uninstall(bool keepSettings)
    {
        if (AppUpdater.FolderOfThisCopy(LauncherName) is not { } folder || !IsInstalledFolder(folder)) return false;
        // the script removes the folder this program runs from; Linux lets a running program finish regardless
        return keepSettings
            ? RunInstallScript(folder, quiet: false, "--remove", "--keep-settings")
            : RunInstallScript(folder, quiet: false, "--remove");
    }

    private static bool RunInstallScript(string folder, bool quiet, params string[] arguments)
    {
        var script = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = quiet,
            RedirectStandardError = quiet,
            WorkingDirectory = "/"
        };
        script.ArgumentList.Add(Path.Combine(folder, "install.sh"));
        foreach (var argument in arguments) script.ArgumentList.Add(argument);

        using var process = Process.Start(script);
        if (process == null) return false;
        if (quiet)
        {
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        return process.WaitForExit(60_000) && process.ExitCode == 0;
    }
}
