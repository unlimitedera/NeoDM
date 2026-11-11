using System.Net;

namespace NDM.Core;

/// <summary>1.13.2: what a network request is for. The proxy a request may use depends on this, never on "a proxy exists".</summary>
public enum ProxyPurpose
{
    /// <summary>File downloads (HTTP/HTTPS), including their address checks.</summary>
    Download,
    /// <summary>Torrent peers and HTTP/HTTPS trackers.</summary>
    Torrent,
    /// <summary>The bundled YouTube helper.</summary>
    YouTube,
    /// <summary>NeoDM's own requests: update check and installer download, pictures, tool downloads, proxy-independent helpers.</summary>
    App
}

/// <summary>Which purposes may use the configured proxy. Defaults: downloads on, everything else off (a proxy set up before 1.13.2 keeps working for file downloads only).</summary>
public sealed record ProxyScope(bool Downloads = true, bool Torrents = false, bool YouTube = false, bool App = false);

/// <summary>The one place that decides which proxy (if any) a purpose gets. Every HTTP handler, torrent engine option and helper spawn asks here,
/// so a new caller cannot silently use (or silently skip) the proxy. A source guard (Security suite) fails when a handler is created without it.</summary>
public sealed class ProxyPolicy
{
    private readonly ProxyOptions? configured;
    public ProxyScope Scope { get; }
    public static ProxyPolicy None { get; } = new(null, new ProxyScope());
    public ProxyPolicy(ProxyOptions? configured, ProxyScope scope) { this.configured = configured is { IsEnabled: true } ? configured : null; Scope = scope; }

    public bool HasProxy => configured is not null;
    /// <summary>The configured proxy regardless of purpose (for the Test button and Options text only; never hand this to a request).</summary>
    public ProxyOptions? Configured => configured;
    public bool Allows(ProxyPurpose purpose) => configured is not null && purpose switch
    {
        ProxyPurpose.Download => Scope.Downloads, ProxyPurpose.Torrent => Scope.Torrents, ProxyPurpose.YouTube => Scope.YouTube, ProxyPurpose.App => Scope.App, _ => false
    };
    /// <summary>The proxy for this purpose, or null for a direct connection.</summary>
    public ProxyOptions? For(ProxyPurpose purpose) => Allows(purpose) ? configured : null;
}

/// <summary>Parses "http://host:port", "https://host:port", "socks5://host:port" (also socks5h, socks://) with optional user:password@, as typed in Options.</summary>
public static class ProxyAddress
{
    public const string Accepted = "http://host:port, https://host:port, socks5://host:port";

    /// <summary>True with the pieces when the text is a proxy address with a scheme. A text without a scheme (plain host) is not handled here.</summary>
    public static bool TryParse(string? text, out ProxyKind kind, out string host, out int port, out string? user, out string? password, out bool resolveThroughProxy)
    {
        kind = ProxyKind.None; host = ""; port = 0; user = null; password = null; resolveThroughProxy = true;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 600 || !text.Contains("://", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.AbsolutePath.Length > 1 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        switch (uri.Scheme.ToLowerInvariant())
        {
            case "http": kind = ProxyKind.Http; break;
            case "https": kind = ProxyKind.Https; break;
            case "socks5": case "socks": kind = ProxyKind.Socks5; resolveThroughProxy = false; break;
            case "socks5h": kind = ProxyKind.Socks5; break;
            default: return false;
        }
        host = uri.IdnHost; if (host.Length == 0) return false;
        if (uri.IsDefaultPort && !text.Contains(':' + uri.Port.ToString(), StringComparison.Ordinal)) { kind = ProxyKind.None; return false; } // a port is required
        port = uri.Port;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]); password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }
        return port is >= 1 and <= 65535;
    }

    public static string Describe(ProxyKind kind) => kind switch { ProxyKind.Http => "HTTP", ProxyKind.Https => "HTTPS", ProxyKind.Socks5 => "SOCKS5", _ => "" };

    /// <summary>True when the proxy type can carry torrent peer and tracker connections (TCP): SOCKS5 and HTTP CONNECT can; UDP trackers, DHT and local discovery never can.</summary>
    public static bool CarriesTorrentTraffic(ProxyKind kind) => kind is ProxyKind.Socks5 or ProxyKind.Http or ProxyKind.Https;
}
