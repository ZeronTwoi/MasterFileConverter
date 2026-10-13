using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileConverter.Core.Platform;

namespace FileConverter.Core;

/// <summary>
/// What the located FFmpeg build can actually encode. FFmpeg builds differ — a distribution's
/// package may leave out an encoder for licensing reasons — so the app asks instead of assuming,
/// and can say which conversions are unavailable rather than failing in the middle of a batch.
/// </summary>
public static class FfmpegCapabilities
{
    private sealed record Snapshot(string Path, HashSet<string> Encoders, HashSet<string> Filters);

    private static readonly object Sync = new();
    private static Snapshot? _snapshot;

    public static bool HasEncoder(string name) => Load()?.Encoders.Contains(name) ?? false;
    public static bool HasFilter(string name) => Load()?.Filters.Contains(name) ?? false;

    /// <summary>Encoders and filters the built-in conversions rely on, with what each is for.</summary>
    private static readonly (string Name, bool Filter, string Purpose)[] Essentials =
    [
        ("libx264", false, "H.264 video (mp4, mkv, mov)"),
        ("libx265", false, "HEVC video (HDR sources)"),
        ("libvpx-vp9", false, "WebM video"),
        ("libmp3lame", false, "MP3 audio"),
        ("libvorbis", false, "Ogg audio"),
        ("libopus", false, "Opus audio (WebM)"),
        ("zscale", true, "HDR to SDR conversion")
    ];

    /// <summary>What this FFmpeg build cannot do, in plain words; empty when it is complete (or there is no FFmpeg).</summary>
    public static IReadOnlyList<string> MissingEssentials
    {
        get
        {
            var snapshot = Load();
            if (snapshot == null) return [];
            return Essentials
                .Where(e => !(e.Filter ? snapshot.Filters : snapshot.Encoders).Contains(e.Name))
                .Select(e => e.Purpose)
                .ToList();
        }
    }

    public static void Reset()
    {
        lock (Sync) _snapshot = null;
    }

    private static Snapshot? Load()
    {
        var ffmpeg = ToolLocator.FfmpegPath;
        if (ffmpeg == null) return null;

        lock (Sync)
        {
            if (_snapshot?.Path == ffmpeg) return _snapshot;
            return _snapshot = Remembered(ffmpeg) ?? Ask(ffmpeg);
        }
    }

    /// <summary>
    /// What was learnt about this very FFmpeg file the last time. The answer only changes when the
    /// file does, and asking means starting a 100 MB program twice — at every start of the app.
    /// </summary>
    internal sealed class Remembrance
    {
        public string Key { get; set; } = "";
        public List<string> Encoders { get; set; } = [];
        public List<string> Filters { get; set; } = [];
    }

    private static string CachePath => Path.Combine(PlatformServices.Current.DataDirectory, "ffmpeg.json");

    /// <summary>Changes whenever the FFmpeg file is replaced.</summary>
    private static string KeyOf(string ffmpeg)
    {
        var file = new FileInfo(ffmpeg);
        return $"{ffmpeg}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private static Snapshot? Remembered(string ffmpeg)
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(CachePath), FfmpegCapabilitiesJson.Default.Remembrance);
            if (saved == null || saved.Key != KeyOf(ffmpeg) || saved.Encoders.Count == 0) return null;
            return new Snapshot(ffmpeg, new HashSet<string>(saved.Encoders, StringComparer.Ordinal), new HashSet<string>(saved.Filters, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null; // unreadable: ask again
        }
    }

    private static Snapshot Ask(string ffmpeg)
    {
        var snapshot = new Snapshot(ffmpeg, List(ffmpeg, "-encoders"), List(ffmpeg, "-filters"));
        // an FFmpeg that answered nothing is asked again next time rather than remembered as empty
        if (snapshot.Encoders.Count == 0) return snapshot;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var saved = new Remembrance { Key = KeyOf(ffmpeg), Encoders = [.. snapshot.Encoders.Order()], Filters = [.. snapshot.Filters.Order()] };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(saved, FfmpegCapabilitiesJson.Default.Remembrance));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // it only saves time; losing it is harmless
        }
        return snapshot;
    }

    /// <summary>
    /// Names from an "ffmpeg -encoders" / "-filters" listing. Every entry line is
    /// "&lt;flags&gt; &lt;name&gt; &lt;description&gt;"; the legend above the table has no such second column.
    /// </summary>
    private static HashSet<string> List(string ffmpeg, string what)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add(what);

            using var process = Process.Start(psi)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && !parts[1].Contains('=')) names.Add(parts[1]);
            }
            process.WaitForExit(10_000);
        }
        catch
        {
            // an FFmpeg that can't even list its encoders shows up as "missing everything"
        }
        return names;
    }
}

/// <summary>Source-generated JSON for what FFmpeg can do, so it keeps working in a trimmed build.</summary>
[JsonSerializable(typeof(FfmpegCapabilities.Remembrance))]
internal sealed partial class FfmpegCapabilitiesJson : JsonSerializerContext;
