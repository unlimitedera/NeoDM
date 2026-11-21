// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>2.0.0: what the Server row shows. Country is null with a proxy (the row then names the proxy). Speed is null when the answer time was not measured.</summary>
public sealed record ServerView(string? Code, string? Country, int? LatencyMs, ServerSpeed? Speed, bool ViaProxy, string Lead, string? Latency, string Host)
{
    public string Text => Latency is null ? Lead : Lead + ", " + Latency;
    /// <summary>Theme token for the latency word: fast green, medium amber, slow red.</summary>
    public string BrushKey => Speed switch { ServerSpeed.Fast => "StatusCompletedBrush", ServerSpeed.Medium => "StatusPausedBrush", ServerSpeed.Slow => "StatusFailedBrush", _ => "MutedBrush" };
}

/// <summary>2.0.0: the Server row of the progress window and tab view, built from what the engine saw (host, connected address, time to the first response) and
/// the offline country table. No address is sent anywhere. Hidden (null) when the country is unknown; a download through a proxy shows the proxy instead.</summary>
public static class ServerInfo
{
    /// <summary>The country table; tests replace it. Null = none bundled (the row stays hidden).</summary>
    public static IpCountry? Table { get; set; } = IpCountry.Default;
    /// <summary>A test seam: when set, answers the country of an address instead of <see cref="Table"/>.</summary>
    public static Func<string?, string?>? LookupOverride { get; set; }

    public static ServerView? Build(string? host, string? address, int? latencyMs, bool viaProxy, string? proxy)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        ServerSpeed? speed = latencyMs is { } ms ? ServerLatency.Classify(ms) : null;
        string? latency = latencyMs is { } m ? Loc.Ltr(Loc.T("server.latency", Loc.Number(m), Loc.T("server." + speed!.Value.ToString().ToLowerInvariant()))) : null;
        if (viaProxy) return new ServerView(null, null, latencyMs, speed, true, Loc.T("server.via_proxy", Loc.Ltr(proxy ?? "")), latency, host);
        var code = CodeOf(address);
        if (code is null) return null;
        var country = CountryName(code);
        return new ServerView(code, country, latencyMs, speed, false, country, latency, host);
    }

    /// <summary>The country code of an address (offline), null when unknown.</summary>
    public static string? CodeOf(string? address) => LookupOverride is { } over ? over(address) : Table?.Lookup(address);
    /// <summary>The theme colour of an answer time.</summary>
    public static string BrushKeyFor(int milliseconds) => ServerLatency.Classify(milliseconds) switch { ServerSpeed.Fast => "StatusCompletedBrush", ServerSpeed.Medium => "StatusPausedBrush", _ => "StatusFailedBrush" };

    private static Dictionary<string, Dictionary<string, string>>? names;
    private static Dictionary<string, Dictionary<string, string>> Names()
    {
        if (names is not null) return names;
        try
        {
            using var s = typeof(ServerInfo).Assembly.GetManifestResourceStream("country-names.json")!;
            names = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(s) ?? new();
        }
        catch { names = new(); }
        return names;
    }

    /// <summary>The country's name in the app language (bundled names for the ten languages); English, then the system's English name, then the code when nothing is known.</summary>
    public static string CountryName(string code, string? language = null)
    {
        var all = Names(); language ??= Loc.Language;
        if (all.TryGetValue(language, out var own) && own.TryGetValue(code, out var name)) return name;
        if (all.TryGetValue("en", out var en) && en.TryGetValue(code, out var english)) return english;
        try { return new System.Globalization.RegionInfo(code).EnglishName; } catch { return code; }
    }
}

/// <summary>2.0.0: the small flag pictures (MIT "flag-icons" set, rendered once to 48 px PNG and packed into one resource). Windows draws flag emoji as two letters, so pictures are bundled.</summary>
public static class FlagImages
{
    private static Dictionary<string, (int Offset, int Length)>? index; private static byte[]? data;
    private static readonly Dictionary<string, ImageSource?> cache = new();

    public static IReadOnlyCollection<string> Codes { get { Load(); return index!.Keys; } }

    private static void Load()
    {
        if (index is not null) return;
        index = new();
        try
        {
            using var s = typeof(FlagImages).Assembly.GetManifestResourceStream("flags.pak")!;
            using var m = new MemoryStream(); s.CopyTo(m); data = m.ToArray();
            if (data.Length < 7 || data[0] != 'N' || data[1] != 'D' || data[2] != 'M' || data[3] != 'F') return;
            int count = BitConverter.ToUInt16(data, 5);
            for (var i = 0; i < count; i++) { var o = 7 + i * 10; index[new string(new[] { (char)data[o], (char)data[o + 1] })] = (BitConverter.ToInt32(data, o + 2), BitConverter.ToInt32(data, o + 6)); }
        }
        catch { index = new(); }
    }

    /// <summary>The flag of a two-letter country code, frozen so it can be shared; null when there is none.</summary>
    public static ImageSource? Get(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        code = code.ToUpperInvariant(); Load();
        if (cache.TryGetValue(code, out var hit)) return hit;
        ImageSource? image = null;
        if (index!.TryGetValue(code, out var e))
        {
            try
            {
                var bmp = new BitmapImage(); bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(data!, e.Offset, e.Length, false); bmp.EndInit(); bmp.Freeze(); image = bmp;
            }
            catch { image = null; }
        }
        cache[code] = image; return image;
    }
}
