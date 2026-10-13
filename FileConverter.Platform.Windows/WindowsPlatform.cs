using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FileConverter.Core;
using FileConverter.Core.Platform;
using Microsoft.Win32;

[assembly: SupportedOSPlatform("windows")]

namespace FileConverter.Platform;

/// <summary>The platform this build of the app runs on.</summary>
public static class HostPlatform
{
    public static IPlatform Create() => new WindowsPlatform();
}

internal sealed partial class WindowsPlatform : PlatformBase
{
    public override string Name => "Windows";

    public override string DataDirectory { get; } = InLocalAppData(AppInfo.ProgramName);

    /// <summary>
    /// Version 2.0 kept its files in "FileConverter" — a folder an unrelated program of nearly
    /// the same name keeps its own settings in as well.
    /// </summary>
    public override (string Data, string Config)? EarlierDirectories { get; } =
        (InLocalAppData("FileConverter"), InLocalAppData("FileConverter"));

    private static string InLocalAppData(string folder) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        folder);

    public override IReadOnlyList<string> ExecutableNames(ExternalTool tool) => tool switch
    {
        ExternalTool.Ffmpeg => ["ffmpeg.exe"],
        ExternalTool.Ffprobe => ["ffprobe.exe"],
        // the installed full 7-Zip first, then the console builds "Set up engines" downloads
        ExternalTool.SevenZip => ["7z.exe", "7za.exe", "7zr.exe"],
        ExternalTool.Rar => ["Rar.exe"],
        ExternalTool.LibreOffice => ["soffice.exe"],
        _ => []
    };

    public override IReadOnlyList<string> KnownLocations(ExternalTool tool)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programs86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return tool switch
        {
            ExternalTool.Ffmpeg => [@"C:\ffmpeg\bin\ffmpeg.exe"],
            ExternalTool.Ffprobe => [@"C:\ffmpeg\bin\ffprobe.exe"],
            ExternalTool.SevenZip => [Path.Combine(programs, "7-Zip", "7z.exe"), Path.Combine(programs86, "7-Zip", "7z.exe")],
            ExternalTool.Rar => [Path.Combine(programs, "WinRAR", "Rar.exe"), Path.Combine(programs86, "WinRAR", "Rar.exe")],
            ExternalTool.LibreOffice =>
            [
                Path.Combine(programs, "LibreOffice", "program", "soffice.exe"),
                Path.Combine(programs86, "LibreOffice", "program", "soffice.exe")
            ],
            _ => []
        };
    }

    public override string NullDevice => "NUL";
    public override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    [LibraryImport("kernel32.dll")]
    private static partial int GetACP();

    public override int LegacyCodePage
    {
        get
        {
            // the system "ANSI" code page; when Windows itself runs in UTF-8, the one the user's language implies
            int codePage = GetACP();
            return codePage == 65001 ? CultureInfo.CurrentCulture.TextInfo.ANSICodePage : codePage;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetOEMCP();

    /// <summary>The code page of this system's console and of the zip files its older tools wrote.</summary>
    public override int ArchiveNameCodePage
    {
        get
        {
            int codePage = GetOEMCP();
            return codePage is > 0 and not 65001 ? codePage : base.ArchiveNameCodePage;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Battery flag 128 = "no system battery", 255 = unknown; anything else is a laptop or tablet.</summary>
    public override bool IsBatteryPowered =>
        GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);

    /// <summary>Display adapters and their driver versions, from the registry's display class.</summary>
    public override string GpuFingerprint()
    {
        var parts = new List<string>();
        using var adapters = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (var name in adapters?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var adapter = adapters!.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string description)
                    parts.Add($"{description}|{adapter.GetValue("DriverVersion")}");
            }
            catch
            {
                // protected subkey — not an adapter
            }
        }
        return string.Join(";", parts);
    }

    public override IReadOnlyList<GpuEncoder> GpuEncoders { get; } = [GpuEncoder.Nvidia, GpuEncoder.Intel, GpuEncoder.Amd];

    public override IReadOnlyList<string> PdfFallbackFonts { get; } = ["Segoe UI", "Yu Gothic UI", "Microsoft YaHei"];

    public override IOfficeAutomation OfficeAutomation { get; } = new WordAutomation();

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    /// <summary>The same routine Explorer sorts with, so pages follow the order the user sees there.</summary>
    public override int CompareFileNames(string a, string b)
    {
        try
        {
            return StrCmpLogicalW(a, b);
        }
        catch
        {
            return base.CompareFileNames(a, b);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>Fails across volumes and on FAT/exFAT; the caller copies instead.</summary>
    public override bool TryCreateHardLink(string linkPath, string existingFile) =>
        CreateHardLink(linkPath, existingFile, IntPtr.Zero);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    public override void AttachToParentConsole() => AttachConsole(-1);

    public override void ShowInFileManager(string path)
    {
        if (Directory.Exists(path))
        {
            Open(path);
            return;
        }
        // Explorer wants the whole "/select,<path>" as one argument, with its own quoting
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
    }

    // ---------- engine downloads ----------

    private const string FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string SevenZrDownloadUrl = "https://www.7-zip.org/a/7zr.exe";

    public override string FfmpegDownloadSummary => "FFmpeg (~100 MB)";

    public override async Task InstallFfmpegAsync(string toolsDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        var tempZip = Path.Combine(toolsDirectory, "ffmpeg-download.tmp");
        try
        {
            // the build's publisher states its checksum beside it
            var checksum = await Downloads.FindChecksumAsync(FfmpegDownloadUrl + ".sha256", Path.GetFileName(FfmpegDownloadUrl), ct);
            await Downloads.DownloadFileAsync(FfmpegDownloadUrl, tempZip, progress, ct, checksum);

            using var zip = ZipFile.OpenRead(tempZip);
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    throw new FriendlyException($"FFmpeg download did not contain {name}.");
                entry.ExtractToFile(Path.Combine(toolsDirectory, name), overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Fetches 7zr.exe (the .7z format, ~600 KB), then uses it to unpack the standalone console
    /// build 7za.exe (~1.5 MB), which also writes zip and tar and can password-protect them.
    /// If that second step fails, 7zr alone still covers .7z.
    /// </summary>
    public override async Task InstallSevenZipAsync(string toolsDirectory, CancellationToken ct)
    {
        var sevenZr = Path.Combine(toolsDirectory, "7zr.exe");
        // from 7-Zip's releases on GitHub, which state a checksum; its own site is the way round
        var reduced = await Downloads.FindSevenZipReleaseFileAsync("7zr.exe", ct) ?? new ReleaseFile("7zr.exe", SevenZrDownloadUrl, null);
        await Downloads.DownloadFileAsync(reduced, sevenZr, null, ct);

        var extra = Path.Combine(toolsDirectory, "7z-extra.tmp");
        var unpacked = Path.Combine(toolsDirectory, "7z-extra");
        try
        {
            await Downloads.DownloadFileAsync(await Downloads.FindSevenZipPackageAsync("-extra.7z", ct), extra, null, ct);

            var psi = new ProcessStartInfo(sevenZr)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { "e", extra, "-o" + unpacked, @"x64\7za.exe", "-y", "-bd" })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, errors);

            var sevenZa = Path.Combine(unpacked, "7za.exe");
            if (process.ExitCode == 0 && File.Exists(sevenZa))
                File.Move(sevenZa, Path.Combine(toolsDirectory, "7za.exe"), overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 7zr.exe is in place; zip and tar keep using the built-in writers
        }
        finally
        {
            try { File.Delete(extra); } catch { /* best effort */ }
            try { Directory.Delete(unpacked, recursive: true); } catch { /* best effort */ }
        }
    }
}
