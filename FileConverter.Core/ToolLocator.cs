using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FileConverter.Core.Platform;

namespace FileConverter.Core;

/// <summary>What a located 7-Zip executable can do.</summary>
public enum SevenZipKind
{
    None,
    /// <summary>7zr: the .7z format only.</summary>
    Reduced,
    /// <summary>7za: 7z, zip, tar, gzip, bzip2, xz — no rar.</summary>
    Standalone,
    /// <summary>7z / 7zz: every format, including reading rar.</summary>
    Full
}

/// <summary>
/// Finds (and can download) the external conversion engines:
/// FFmpeg (audio/video), 7-Zip (archives), RAR (rar creation),
/// LibreOffice / Microsoft Word (high-fidelity document conversion).
/// Where to look and what to download is the platform's business.
/// </summary>
public static class ToolLocator
{
    private static IPlatform Os => PlatformServices.Current;

    public static string ToolsDir => Path.Combine(Os.DataDirectory, "tools");

    public static string? FfmpegPath => Locate(ExternalTool.Ffmpeg);
    public static string? FfprobePath => Locate(ExternalTool.Ffprobe);

    /// <summary>
    /// The oldest FFmpeg release an installed copy may be. The app's arguments, and the way it
    /// reads FFprobe's answers, are tested against the build it downloads itself; a system
    /// package from before that is passed over — as if none were installed — and the app offers
    /// its own download instead of failing in ways nobody has seen.
    /// </summary>
    public const int MinimumFfmpegMajor = 9;

    /// <summary>An installed FFmpeg that was passed over as too old ("/usr/bin/ffmpeg (version 6.1.1)"), or null.</summary>
    public static string? OutdatedFfmpeg { get; private set; }

    /// <summary>
    /// Is this "ffmpeg -version" number from before <see cref="MinimumFfmpegMajor"/>? Releases
    /// start with their number ("9.0.2", "n9.0.2-12-g…", "6.1.1-3ubuntu5"); a development
    /// snapshot ("N-121257-g…", "2026-08-11-git-…") or anything unrecognised counts as current.
    /// </summary>
    internal static bool IsOutdatedFfmpeg(string? version)
    {
        if (version == null) return false;
        var match = Regex.Match(version, @"^[nv]?(\d{1,4})\.");
        return match.Success && int.Parse(match.Groups[1].Value) < MinimumFfmpegMajor;
    }

    private static readonly ConcurrentDictionary<string, string?> FfmpegVersions = new();

    /// <summary>The version an FFmpeg or FFprobe executable reports ("ffmpeg version 9.0.2 Copyright …").</summary>
    private static string? FfmpegVersion(string path) => FfmpegVersions.GetOrAdd(path, static p =>
    {
        try
        {
            var psi = new ProcessStartInfo(p)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");
            using var process = Process.Start(psi)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var banner = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var match = Regex.Match(banner, @"^ff\w+ version (\S+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>Our own download is always taken; an installed FFmpeg only when it is recent enough.</summary>
    private static bool Usable(ExternalTool tool, string path)
    {
        if (tool is not (ExternalTool.Ffmpeg or ExternalTool.Ffprobe)) return true;
        if (Path.GetDirectoryName(path) is { } dir && string.Equals(dir, ToolsDir, Os.PathComparison)) return true;

        var version = FfmpegVersion(path);
        if (!IsOutdatedFfmpeg(version)) return true;
        if (tool == ExternalTool.Ffmpeg) OutdatedFfmpeg = $"{path} (version {version})";
        return false;
    }

    /// <summary>An installed 7-Zip if there is one, else the console build we downloaded.</summary>
    public static string? SevenZipPath => Locate(ExternalTool.SevenZip);

    public static SevenZipKind SevenZipKind
    {
        get
        {
            if (SevenZipPath is not { } path) return SevenZipKind.None;
            return Path.GetFileNameWithoutExtension(path).ToLowerInvariant() switch
            {
                "7zr" => SevenZipKind.Reduced,
                "7za" => SevenZipKind.Standalone,
                _ => SevenZipKind.Full
            };
        }
    }

    /// <summary>True when the located 7-Zip also writes zip and tar (7zr only does .7z).</summary>
    public static bool SevenZipWritesZip => SevenZipKind is SevenZipKind.Standalone or SevenZipKind.Full;

    /// <summary>Can the located 7-Zip unpack this archive type?</summary>
    public static bool SevenZipReads(string extension) => SevenZipKind switch
    {
        SevenZipKind.Full => extension is "7z" or "zip" or "rar" or "tar",
        SevenZipKind.Standalone => extension is "7z" or "zip" or "tar",
        SevenZipKind.Reduced => extension == "7z",
        _ => false
    };

    private static readonly ConcurrentDictionary<string, Version?> ToolVersions = new();

    /// <summary>
    /// True when the located 7-Zip is 25.01 or newer. Older versions can be tricked by symbolic
    /// links inside zip/rar archives into writing outside the extraction folder
    /// (CVE-2025-11001/11002, CVE-2025-55188), so they are not handed archives of those formats.
    /// </summary>
    public static bool SevenZipExtractsSafely =>
        SevenZipPath is { } path && SevenZipVersion(path) is { } version && version >= new Version(25, 1);

    /// <summary>Reads the version from the banner every 7-Zip build prints ("7-Zip (z) 25.01 (x64)", "p7zip Version 16.02").</summary>
    private static Version? SevenZipVersion(string path) => ToolVersions.GetOrAdd(path, static p =>
    {
        try
        {
            var psi = new ProcessStartInfo(p)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi)!;
            var banner = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            var match = Regex.Match(banner, @"7-Zip[^\d\r\n]*(\d+)\.(\d+)");
            return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>The rar command-line encoder (part of WinRAR / RAR for Linux) — the only legal way to create .rar.</summary>
    public static string? RarPath => Locate(ExternalTool.Rar);

    public static string? LibreOfficePath => Locate(ExternalTool.LibreOffice);

    /// <summary>Microsoft Word (or another in-process office engine) is installed.</summary>
    public static bool OfficeAutomationAvailable => Os.OfficeAutomation?.IsAvailable == true;

    private static readonly ConcurrentDictionary<ExternalTool, string> Located = new();

    /// <summary>
    /// First existing candidate, remembered: every conversion asks for its engine, and a
    /// miss in the tools folder otherwise means walking the whole PATH each time.
    /// Only hits are cached, so a tool installed later is still picked up.
    /// </summary>
    private static string? Locate(ExternalTool tool)
    {
        if (Located.TryGetValue(tool, out var known) && File.Exists(known)) return known;

        foreach (var candidate in Candidates(tool))
        {
            if (File.Exists(candidate) && Usable(tool, candidate))
            {
                Located[tool] = candidate;
                return candidate;
            }
        }
        Located.TryRemove(tool, out _);
        return null;
    }

    private static IEnumerable<string> Candidates(ExternalTool tool)
    {
        var names = Os.ExecutableNames(tool);
        bool ours = tool is ExternalTool.Ffmpeg or ExternalTool.Ffprobe;

        // FFmpeg: the build we downloaded is the one the app was tuned and tested against, so it
        // wins over whatever is on PATH. 7-Zip: an installed full version does more than our
        // console build, so it is asked first.
        if (ours)
            foreach (var name in names) yield return Path.Combine(ToolsDir, name);

        foreach (var location in Os.KnownLocations(tool)) yield return location;
        foreach (var name in names)
            if (FromPath(name) is { } onPath) yield return onPath;

        if (!ours)
            foreach (var name in names) yield return Path.Combine(ToolsDir, name);
    }

    private static string? FromPath(string executableName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // malformed PATH entry — ignore
            }
        }
        return null;
    }

    /// <summary>Forget what was found (a tool was just installed or replaced).</summary>
    public static void Refresh()
    {
        Located.Clear();
        ToolVersions.Clear();
        FfmpegVersions.Clear();
        OutdatedFfmpeg = null;
        FfmpegCapabilities.Reset();
        HardwareEncoders.Reset(); // a different ffmpeg build may support different GPU encoders
    }

    /// <summary>Downloads FFmpeg and FFprobe into the local tools folder.</summary>
    public static async Task DownloadFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        await Os.InstallFfmpegAsync(ToolsDir, progress, ct);
        Refresh();
    }

    /// <summary>Downloads a 7-Zip console build into the local tools folder.</summary>
    public static async Task DownloadSevenZipAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        await Os.InstallSevenZipAsync(ToolsDir, ct);
        Refresh();
    }
}
