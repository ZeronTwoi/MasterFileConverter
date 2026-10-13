using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileConverter.Core;

/// <summary>A file of a GitHub release, with the SHA-256 GitHub states for it (null for one it states none for).</summary>
public readonly record struct ReleaseFile(string Name, string Url, string? Sha256);

/// <summary>
/// HTTP downloads for the engine installers, with retries for connections that drop, and a check
/// of what arrived against the checksum its publisher states.
///
/// Some networks reset every TLS 1.3 connection to certain servers while TLS 1.2 goes through
/// at once (seen in practice, with nothing in the error to say so). A plain retry never gets
/// past that, so every second attempt is made with TLS 1.2.
/// </summary>
public static class Downloads
{
    private const int Attempts = 6;

    private static readonly HttpClient Default = CreateClient(tls12Only: false);
    private static readonly Lazy<HttpClient> Tls12 = new(() => CreateClient(tls12Only: true));

    private static HttpClient CreateClient(bool tls12Only)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };
        if (tls12Only) handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12;

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        // GitHub's API refuses requests without a user agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.ProgramName}/{AppInfo.Version}");
        return client;
    }

    private static HttpClient ClientFor(int attempt) => attempt % 2 == 1 ? Default : Tls12.Value;

    public static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await ClientFor(attempt).GetStringAsync(url, ct);
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    /// <summary>
    /// Downloads a file. With a <paramref name="sha256"/>, what arrived has to have it: a file that
    /// does not is deleted and the download fails.
    /// </summary>
    public static async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct,
        string? sha256 = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadFileOnceAsync(ClientFor(attempt), url, destination, progress, ct);
                break;
            }
            catch (Exception ex) when (attempt < Attempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
        if (sha256 != null)
            await Task.Run(() => RequireSha256(destination, sha256, Path.GetFileName(new Uri(url).LocalPath)), ct);
    }

    public static Task DownloadFileAsync(ReleaseFile file, string destination, IProgress<double>? progress, CancellationToken ct) =>
        DownloadFileAsync(file.Url, destination, progress, ct, file.Sha256);

    // ---------- checksums ----------

    /// <summary>Deletes <paramref name="file"/> and fails unless it has this SHA-256.</summary>
    internal static void RequireSha256(string file, string sha256, string name)
    {
        string actual;
        using (var stream = File.OpenRead(file))
            actual = Convert.ToHexString(SHA256.HashData(stream));
        if (actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return;

        try { File.Delete(file); } catch { /* best effort */ }
        throw new FriendlyException(
            $"The download of {name} is not the file its publisher made (its checksum differs), so it was not used. " +
            "It may have been cut short or changed on the way — try again.");
    }

    /// <summary>
    /// The SHA-256 a publisher's checksum file states: the one for <paramref name="fileName"/> in a
    /// list, or the only one in a file that holds nothing else. Null when the file cannot be read.
    /// </summary>
    public static async Task<string?> FindChecksumAsync(string checksumUrl, string fileName, CancellationToken ct)
    {
        try
        {
            return ParseChecksum(await GetStringAsync(checksumUrl, ct), fileName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Reads "&lt;sha256&gt;" or lines of "&lt;sha256&gt;  &lt;file name&gt;", as sha256sum writes them.</summary>
    internal static string? ParseChecksum(string text, string fileName)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(line, @"^([0-9a-fA-F]{64})(?:\s+\*?(.+))?$");
            if (!match.Success) continue;
            if (!match.Groups[2].Success || match.Groups[2].Value == fileName)
                return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>The files of a GitHub release (the address of its API entry), each with the checksum GitHub states for it.</summary>
    public static async Task<IReadOnlyList<ReleaseFile>> GetReleaseFilesAsync(string releaseApiUrl, CancellationToken ct)
    {
        using var release = JsonDocument.Parse(await GetStringAsync(releaseApiUrl, ct));
        var files = new List<ReleaseFile>();
        foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == null || url == null) continue;

            // "sha256:<hex>"
            var digest = asset.TryGetProperty("digest", out var stated) && stated.ValueKind == JsonValueKind.String
                ? stated.GetString()
                : null;
            files.Add(new ReleaseFile(name, url, digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null));
        }
        return files;
    }

    // ---------- 7-Zip ----------

    private const string SevenZipReleaseApi = "https://api.github.com/repos/ip7z/7zip/releases/latest";
    private const string SevenZipDownloadPage = "https://www.7-zip.org/download.html";

    /// <summary>Used when neither GitHub nor the download page can be read. Any release from 25.01 on extracts archives safely.</summary>
    private const string SevenZipFallbackVersion = "26.04";

    /// <summary>
    /// The file of the newest 7-Zip release on GitHub whose name ends in <paramref name="suffix"/>
    /// ("7zr.exe", "-extra.7z"), with its checksum — or null when GitHub cannot be asked.
    /// </summary>
    public static async Task<ReleaseFile?> FindSevenZipReleaseFileAsync(string suffix, CancellationToken ct)
    {
        try
        {
            foreach (var file in await GetReleaseFilesAsync(SevenZipReleaseApi, ct))
                if (file.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return file;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // unreachable, or the hourly limit for unsigned requests is used up
        }
        return null;
    }

    /// <summary>
    /// The newest 7-Zip package whose file name ends in <paramref name="suffix"/> ("-extra.7z",
    /// "-linux-x64.tar.xz"). GitHub is asked first, because it states a checksum for each file; the
    /// official download page and a release known to exist are the ways round it, without one.
    /// </summary>
    public static async Task<ReleaseFile> FindSevenZipPackageAsync(string suffix, CancellationToken ct)
    {
        if (await FindSevenZipReleaseFileAsync(suffix, ct) is { } released) return released;

        string? url = null;
        try
        {
            var page = await GetStringAsync(SevenZipDownloadPage, ct);
            var pattern = "href=\"([^\"]*?7z(\\d{4})" + Regex.Escape(suffix) + ")\"";
            var newest = Regex.Matches(page, pattern)
                .OrderByDescending(m => m.Groups[2].Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (newest != null)
                url = new Uri(new Uri(SevenZipDownloadPage), newest.Groups[1].Value).AbsoluteUri;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // page unreachable — fall back to a release known to exist
        }
        var compact = SevenZipFallbackVersion.Replace(".", "");
        url ??= $"https://github.com/ip7z/7zip/releases/download/{SevenZipFallbackVersion}/7z{compact}{suffix}";
        return new ReleaseFile(Path.GetFileName(new Uri(url).LocalPath), url, null);
    }

    /// <summary>A dropped connection or a server in trouble is worth another try; "not found" and "not allowed" are answers.</summary>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException
        {
            StatusCode: null or >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        } ||
        ex is IOException or AuthenticationException ||
        ex is TaskCanceledException { InnerException: TimeoutException };

    private static async Task DownloadFileOnceAsync(HttpClient client, string url, string destination,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);

        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress?.Report((double)done / total);
        }
    }
}
