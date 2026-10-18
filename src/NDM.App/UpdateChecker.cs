using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>Strict semantic versions for update checks: only stable <c>x.y.z</c> (optionally with a leading v). Pre-release, build-metadata and malformed tags are ignored.</summary>
public static class SemVer
{
    private static readonly Regex Stable = new(@"^[vV]?(\d{1,6})\.(\d{1,6})\.(\d{1,6})$", RegexOptions.CultureInvariant);
    public static bool TryParseTag(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var m = Stable.Match((text ?? "").Trim());
        if (!m.Success) return false;
        version = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }
    /// <summary>The running version from an informational version such as "1.3.0" or "1.3.0+abc123" (build metadata is dropped, a pre-release suffix is kept out of the comparison).</summary>
    public static Version Running(string? informational)
    {
        var core = (informational ?? "").Split('+')[0].Split('-')[0];
        return TryParseTag(core, out var v) ? v : new Version(0, 0, 0);
    }
    public static bool IsNewer(Version candidate, Version running) => candidate > running;
}

public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record ReleaseInfo(string Tag, Version Version, string Name, string Body, string HtmlUrl, ReleaseAsset? Installer, ReleaseAsset? Checksums)
{
    public string VersionText => Version.ToString(3);
}

public enum UpdateOutcome { UpToDate, Available, Unavailable }
public sealed record UpdateCheckResult(UpdateOutcome Outcome, ReleaseInfo? Release = null, string? Reason = null);

/// <summary>
/// Looks for a newer GitHub release. One anonymous GET to api.github.com; never throws: offline, rate limits, 404 and malformed replies
/// all come back as <see cref="UpdateOutcome.Unavailable"/> so the caller can stay silent.
/// </summary>
public sealed class UpdateChecker
{
    public const string Repository = "unlimitedera/NeoDM";
    public const string DefaultApiBase = "https://api.github.com";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    public const string ChangelogUrl = RepositoryUrl + "/blob/main/CHANGELOG.md";
    private static readonly Regex InstallerName = new(@"^NeoDM-Setup(-[0-9A-Za-z.\-_]+)?\.msi$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly string apiBase, currentVersion;
    private readonly Func<ProxyOptions?> proxy;
    private readonly HttpMessageHandler? handler;
    private readonly Func<Uri, bool> trustedUrl;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <param name="handler">Test seam; null = a normal handler that honours the proxy from Options.</param>
    /// <param name="trustedUrl">Which asset/page URLs may be used. Default: https on github.com / githubusercontent.com.</param>
    public UpdateChecker(string currentVersion, Func<ProxyOptions?>? proxy = null, string apiBase = DefaultApiBase, HttpMessageHandler? handler = null, Func<Uri, bool>? trustedUrl = null)
    {
        this.currentVersion = currentVersion; this.proxy = proxy ?? (() => null); this.apiBase = apiBase.TrimEnd('/'); this.handler = handler; this.trustedUrl = trustedUrl ?? IsGitHubUrl;
    }

    public static bool IsGitHubUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public static string UserAgent(string version) => "NeoDM/" + version;

    internal static HttpClient CreateClient(HttpMessageHandler? handler, ProxyOptions? proxy, TimeSpan timeout, string version)
    {
        if (handler is null) { var sockets = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }; ProxyHttp.Apply(sockets, proxy); handler = sockets; }
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NeoDM", version));
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient(handler, proxy(), Timeout, currentVersion);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/repos/{Repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateOutcome.Unavailable, null, Loc.T("update.reason.none"));
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) return new(UpdateOutcome.Unavailable, null, Loc.T("update.reason.limited"));
            if (!response.IsSuccessStatusCode) return new(UpdateOutcome.Unavailable, null, Loc.T("update.reason.status", (int)response.StatusCode));
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = Parse(json, trustedUrl);
            if (release is null) return new(UpdateOutcome.Unavailable, null, Loc.T("update.reason.no_version"));
            return SemVer.IsNewer(release.Version, SemVer.Running(currentVersion)) ? new(UpdateOutcome.Available, release) : new(UpdateOutcome.UpToDate, release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException or ArgumentException or NotSupportedException or ProxyException)
        {
            return new(UpdateOutcome.Unavailable, null, Loc.T("update.reason.unreachable", ex.GetType().Name));
        }
    }

    /// <summary>Parses a GitHub "latest release" document. Null for drafts, pre-releases, unparsable tags and non-objects.</summary>
    public static ReleaseInfo? Parse(string json, Func<Uri, bool>? trustedUrl = null)
    {
        trustedUrl ??= IsGitHubUrl;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        if (Flag("draft") || Flag("prerelease")) return null;
        var tag = Str(root, "tag_name");
        if (!SemVer.TryParseTag(tag, out var version)) return null;
        string page = Str(root, "html_url") is { } h && Uri.TryCreate(h, UriKind.Absolute, out var hu) && trustedUrl(hu) ? h : UpdateChecker.RepositoryUrl + "/releases/tag/" + tag;
        ReleaseAsset? installer = null, sums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = Str(a, "name"); var url = Str(a, "browser_download_url");
                if (name is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !trustedUrl(uri)) continue;
                long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
                if (InstallerName.IsMatch(name)) { if (installer is null || name.Contains(version.ToString(3), StringComparison.Ordinal)) installer = new(name, url, size); }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = new(name, url, size);
            }
        }
        return new ReleaseInfo(tag!, version, Str(root, "name") ?? tag!, Str(root, "body") ?? "", page, installer, sums);
    }
}
