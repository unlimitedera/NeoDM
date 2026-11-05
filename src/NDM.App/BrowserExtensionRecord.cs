using NDM.App;

namespace NDM.App;

/// <summary>Which copy of the extension talks to the app, from the extension ID the native host forwards (1.5.6): Store = the Chrome Web Store build,
/// Development = the unpacked copy (the key in the manifest gives it the development ID), Unknown = an older host or an ID that is neither.</summary>
public enum ExtensionChannel { Unknown, Store, Development }

/// <summary>What NeoDM last saw of its extension in one browser (1.11.0). Display data only: it comes from the extension, so it is never used for a security decision.</summary>
public sealed class BrowserExtensionRecord
{
    /// <summary>The browser's name as reported ("Google Chrome", "Microsoft Edge", "Brave"), or <see cref="BrowserExtensionBook.UnknownBrowser"/> for an extension that does not say.</summary>
    public string Browser { get; set; } = "";
    /// <summary>The extension version it reported; null = none (a very old extension).</summary>
    public string? Version { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>Which copy talks to the app (Store or Development), from the extension ID the host forwards.</summary>
    public ExtensionChannel Channel { get; set; }
    /// <summary>The "version|app version" pair a notice was last shown for; the same pair is never announced again for this browser.</summary>
    public string? NoticedKey { get; set; }

    public const int MaxBrowsers = 8;
    public const string UnknownBrowser = "Browser";

    /// <summary>Settings loading: valid names and versions only, at most <see cref="MaxBrowsers"/>.</summary>
    /// <summary>Settings loading: valid names and versions only, at most <see cref="MaxBrowsers"/>.</summary>
    public static List<BrowserExtensionRecord> Sanitize(IEnumerable<BrowserExtensionRecord>? records) =>
        (records ?? Enumerable.Empty<BrowserExtensionRecord>())
            .Where(r => r is not null && (r.Browser == UnknownBrowser || BrowserNames.IsValid(r.Browser)))
            .Select(r => new BrowserExtensionRecord
            {
                Browser = r.Browser, Version = r.Version is { } v && NativeBridge.ValidExtVersion(v) ? r.Version : null, LastSeen = r.LastSeen,
                Channel = Enum.IsDefined(r.Channel) ? r.Channel : ExtensionChannel.Unknown,
                NoticedKey = r.NoticedKey is { Length: <= 60 } ? r.NoticedKey : null,
            })
            .GroupBy(r => r.Browser, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(r => r.LastSeen).First())
            .OrderByDescending(r => r.LastSeen).Take(MaxBrowsers).ToList();
}
