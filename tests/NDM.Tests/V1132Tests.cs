using System.Text.Json;
using NDM.Core;
using static NDM.Tests.StreamKit;

namespace NDM.Tests;

/// <summary>1.13.2: proxy scope per purpose (policy table, address parsing, the YouTube helper command line, and a source guard that stops a new HTTP client from bypassing the policy).</summary>
internal static class V1132Tests
{
    private static ProxyOptions Proxy(ProxyKind kind = ProxyKind.Socks5, bool viaProxy = true) => new() { Kind = kind, Host = "127.0.0.1", Port = 1080, ResolveHostsThroughProxy = viaProxy };

    public static Task PolicyTable()
    {
        var p = Proxy();
        // default scope = migration: an existing proxy keeps working for file downloads only
        var old = new ProxyPolicy(p, new ProxyScope());
        Ensure(old.For(ProxyPurpose.Download) == p && old.For(ProxyPurpose.Torrent) is null && old.For(ProxyPurpose.YouTube) is null && old.For(ProxyPurpose.App) is null, "default scope: downloads only");
        foreach (var purpose in Enum.GetValues<ProxyPurpose>())
        {
            var only = new ProxyPolicy(p, new ProxyScope(purpose == ProxyPurpose.Download, purpose == ProxyPurpose.Torrent, purpose == ProxyPurpose.YouTube, purpose == ProxyPurpose.App));
            foreach (var asked in Enum.GetValues<ProxyPurpose>())
                Ensure((only.For(asked) is not null) == (asked == purpose), $"only {purpose} switched on: {asked} " + (asked == purpose ? "gets" : "does not get") + " the proxy");
        }
        var all = new ProxyPolicy(p, new ProxyScope(true, true, true, true));
        Ensure(Enum.GetValues<ProxyPurpose>().All(x => all.For(x) == p), "all switches on: every purpose gets the proxy");
        var none = new ProxyPolicy(null, new ProxyScope(true, true, true, true));
        Ensure(Enum.GetValues<ProxyPurpose>().All(x => none.For(x) is null) && !none.HasProxy, "no proxy configured: every purpose is direct");
        Ensure(new ProxyPolicy(new ProxyOptions { Kind = ProxyKind.Socks5, Host = "", Port = 1 }, new ProxyScope(true, true, true, true)).For(ProxyPurpose.Download) is null, "an incomplete proxy is no proxy");
        return Task.CompletedTask;
    }

    public static Task Addresses()
    {
        bool Ok(string text, ProxyKind kind, string host, int port, bool viaProxy = true)
        {
            if (!ProxyAddress.TryParse(text, out var k, out var h, out var pt, out _, out _, out var v)) return false;
            return k == kind && h == host && pt == port && v == viaProxy;
        }
        Ensure(Ok("http://proxy.local:3128", ProxyKind.Http, "proxy.local", 3128), "http address");
        Ensure(Ok("HTTPS://proxy.local:443", ProxyKind.Https, "proxy.local", 443), "https address with the default port written out");
        Ensure(Ok("socks5://127.0.0.1:10808", ProxyKind.Socks5, "127.0.0.1", 10808, false), "socks5 resolves names locally");
        Ensure(Ok("socks5h://127.0.0.1:10808", ProxyKind.Socks5, "127.0.0.1", 10808), "socks5h resolves names through the proxy");
        Ensure(Ok(" socks5://[::1]:1080/ ", ProxyKind.Socks5, "[::1]", 1080, false) || Ok(" socks5://[::1]:1080/ ", ProxyKind.Socks5, "::1", 1080, false), "ipv6 and trailing slash");
        Ensure(ProxyAddress.TryParse("socks5://me:p%40ss@host.example:1080", out _, out _, out _, out var user, out var pass, out _) && user == "me" && pass == "p@ss", "login in the address is split off");
        foreach (var bad in new[] { "", "host:8080", "127.0.0.1", "ftp://host:21", "socks4://host:1080", "http://host", "http://host:99999", "http://host:8080/path", "http://host:8080?x=1", "http://:8080", "javascript:alert(1)", new string('a', 700) + "://x:1" })
            Ensure(!ProxyAddress.TryParse(bad, out _, out _, out _, out _, out _, out _), "refused: " + (bad.Length > 40 ? bad[..40] : bad));
        return Task.CompletedTask;
    }

    public static Task YoutubeArguments()
    {
        Ensure(YoutubeService.ProxyArguments(null).Length == 0 && YoutubeService.ProxyArguments(new ProxyOptions()).Length == 0, "no proxy: no argument");
        Ensure(YoutubeService.ProxyArguments(Proxy(ProxyKind.Http)).SequenceEqual(new[] { "--proxy", "http://127.0.0.1:1080" }), "http scheme");
        Ensure(YoutubeService.ProxyArguments(Proxy(ProxyKind.Https)).SequenceEqual(new[] { "--proxy", "https://127.0.0.1:1080" }), "https scheme");
        Ensure(YoutubeService.ProxyArguments(Proxy(ProxyKind.Socks5, true)).SequenceEqual(new[] { "--proxy", "socks5h://127.0.0.1:1080" }), "socks5 with names resolved by the proxy uses the socks5h scheme");
        Ensure(YoutubeService.ProxyArguments(Proxy(ProxyKind.Socks5, false)).SequenceEqual(new[] { "--proxy", "socks5://127.0.0.1:1080" }), "socks5 with local names uses the socks5 scheme");
        var login = new ProxyOptions { Kind = ProxyKind.Http, Host = "h.example", Port = 8080, Username = "a b", Password = "p@:/" };
        Ensure(YoutubeService.ProxyArguments(login)[1] == "http://a%20b:p%40%3A%2F@h.example:8080", "login is escaped");
        return Task.CompletedTask;
    }

    /// <summary>The real service call: the helper gets --proxy ahead of the "--" separator only when the service was given a proxy (the YouTube switch), otherwise nothing.</summary>
    public static async Task YoutubeSpawn((string Download, string State) paths)
    {
        var service = await YoutubeTests.Install(paths.State);
        var log = Path.Combine(paths.State, "proxy-args.log"); Environment.SetEnvironmentVariable("NDM_YT_TEST_LOG", log);
        try
        {
            string[] Last() => File.ReadAllLines(log).Select(l => JsonSerializer.Deserialize<string[]>(l)!).Last();
            await service.InspectAsync(YoutubeTests.Url);
            Ensure(!Last().Contains("--proxy"), "YouTube switch off: the helper is started without --proxy");
            service.Proxy = Proxy(ProxyKind.Socks5, true);
            await service.InspectAsync(YoutubeTests.Url);
            var args = Last(); int at = Array.IndexOf(args, "--proxy");
            Ensure(at >= 0 && args[at + 1] == "socks5h://127.0.0.1:1080" && at < Array.IndexOf(args, "--") && args[^1] == YoutubeTests.Url, "YouTube switch on: --proxy socks5h://... sits before the -- separator");
            service.Proxy = null;
            await service.InspectAsync(YoutubeTests.Url);
            Ensure(!Last().Contains("--proxy"), "proxy removed again: no --proxy");
        }
        finally { Environment.SetEnvironmentVariable("NDM_YT_TEST_LOG", null); }
    }

    /// <summary>A source guard like the GetTempPath one: every place that creates an HTTP client or handler applies the policy's proxy; nothing uses the Windows WebClient/WebRequest or the handler without a proxy hook;
    /// application code asks AppSettings.ProxyFor(purpose), never the raw configured proxy.</summary>
    public static Task SourceGuard()
    {
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "src", "NDM.Core"))) { root = Path.Combine(d.FullName, "src"); break; }
        Ensure(root != null, "Repository sources not found");
        // Files that may create a client/handler. Each one must call ProxyHttp.Apply (or, for the torrent engine's default client, be the torrent transport).
        var allowed = new[] { "DownloadManager.cs", "Proxy.cs", "ToolManager.cs", "UpdateChecker.cs", "ProxyTransport.cs" };
        int seen = 0;
        foreach (var file in Directory.EnumerateFiles(root!, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root!, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.Contains("/Tests/")) continue;
            var text = File.ReadAllText(file); var name = Path.GetFileName(file);
            bool creates = text.Contains("new HttpClient(") || text.Contains("new SocketsHttpHandler") || text.Contains("new HttpClientHandler") || text.Contains("new WebClient") || text.Contains("WebRequest.Create") || text.Contains("HttpWebRequest");
            if (creates)
            {
                seen++;
                Ensure(allowed.Contains(name), rel + " creates an HTTP client or handler: route it through ProxyHttp.Apply and add it to the allow-list of this guard");
                Ensure(text.Contains("ProxyHttp.Apply"), rel + " creates an HTTP client but never calls ProxyHttp.Apply");
                Ensure(!text.Contains("new HttpClientHandler") && !text.Contains("new WebClient") && !text.Contains("WebRequest.Create") && !text.Contains("HttpWebRequest"), rel + " uses an HTTP API that has no proxy hook");
                Ensure(!System.Text.RegularExpressions.Regex.IsMatch(text, @"new HttpClient\(\s*\)"), rel + " creates an HttpClient with the default handler (system proxy)");
            }
            if (name != "AppSettings.cs") Ensure(!text.Contains(".BuildProxy("), rel + " reads the raw proxy: use AppSettings.ProxyFor(ProxyPurpose.X)");
        }
        Ensure(seen >= 5, "the guard found the known HTTP client sites (" + seen + ")");
        return Task.CompletedTask;
    }
}
