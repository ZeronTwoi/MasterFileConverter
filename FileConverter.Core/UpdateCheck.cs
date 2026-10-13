using System.Net.Http;
using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace FileConverter.Core;

/// <summary>
/// Looks up the number of the newest released version — only ever called once the user has said
/// they want to be told about new versions, and then once per start of the app.
///
/// It is the smallest question that can be asked: one request without a body for the address of
/// the latest release, which the server answers with nothing but a redirect to
/// "…/releases/tag/v2.3.0". The version is read from that address; the redirect is not followed
/// and nothing is downloaded. What the server learns is what any request tells it: the
/// computer's internet address and the app's name.
///
/// Fetching and installing a newer version is another matter, and only ever happens on a click:
/// see <see cref="AppUpdater"/>.
/// </summary>
public static class UpdateCheck
{
    /// <summary>The page a person downloads the newest version from.</summary>
    public static string DownloadPage => AppInfo.Website + "/releases/latest";

    /// <summary>The newest released version, or null when it could not be learnt (offline, no release yet, …).</summary>
    public static async Task<Version?> FindLatestVersionAsync(CancellationToken ct)
    {
        // Some networks reset TLS 1.3 connections to certain servers (see Downloads): one more try
        // with TLS 1.2 gets past that. Never more than these two.
        foreach (bool tls12Only in new[] { false, true })
        {
            try
            {
                return await AskAsync(tls12Only, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or AuthenticationException && !ct.IsCancellationRequested)
            {
                // no connection this way
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return null; // too slow, or an answer that makes no sense: not worth a second question
            }
        }
        return null;
    }

    private static async Task<Version?> AskAsync(bool tls12Only, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };
        if (tls12Only) handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12;
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

        using var request = new HttpRequestMessage(HttpMethod.Head, DownloadPage);
        request.Headers.UserAgent.ParseAdd(AppInfo.ProgramName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return VersionInAddress(response.Headers.Location?.OriginalString);
    }

    /// <summary>The version a release's address ends in: "…/releases/tag/v2.3.0" → 2.3.0.</summary>
    internal static Version? VersionInAddress(string? address)
    {
        if (address == null) return null;
        var match = Regex.Match(address, @"/releases/tag/v?(\d+(?:\.\d+){1,3})(?:[-+][^/?#]*)?/?$");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    /// <summary>The version a release's tag names: "v2.3.0" → 2.3.0.</summary>
    internal static Version? VersionInTag(string? tag)
    {
        if (tag == null) return null;
        var match = Regex.Match(tag, @"^v?(\d+(?:\.\d+){1,3})(?:[-+].*)?$");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    /// <summary>Is <paramref name="latest"/> newer than this version of the app ("2.2.0", "2.3.0-beta")?</summary>
    public static bool IsNewer(Version latest, string current)
    {
        if (!Version.TryParse(current.Split('-', '+')[0], out var mine)) return false;
        return Normalized(latest) > Normalized(mine);
    }

    /// <summary>2.3 and 2.3.0 are the same version; .NET compares them as different.</summary>
    private static Version Normalized(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
