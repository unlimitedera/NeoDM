// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using System.Net;
using NDM.Core;

namespace NDM.Tests;

/// <summary>2.0.0 stage S3 engine tests: the offline country table, the Server row data, the mirror engine.</summary>
internal static class V2000S3Tests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition, string message, int seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(message); await Task.Delay(20); }
    }
    private static DownloadJob Job(DownloadManager m, string id) => m.Jobs.Single(j => j.Id == id);

    /// <summary>The bundled table answers known public addresses, refuses private and loopback ones, handles IPv6 and IPv4-mapped addresses, and is a few MB.</summary>
    public static Task CountryTable()
    {
        var table = IpCountry.Default ?? throw new Exception("The bundled country table did not load");
        Check(table.RangeCount > 500_000, "The table has the full range list: " + table.RangeCount);
        Check(table.Lookup("8.8.8.8") == "US", "8.8.8.8 is US: " + table.Lookup("8.8.8.8"));
        Check(table.Lookup("91.198.174.192") == "NL", "A Dutch address: " + table.Lookup("91.198.174.192"));
        Check(table.Lookup("2001:4860:1c00::1") == "US", "An IPv6 address: " + table.Lookup("2001:4860:1c00::1"));
        Check(table.Lookup(IPAddress.Parse("::ffff:8.8.8.8")) == "US", "An IPv4-mapped IPv6 address is looked up as IPv4");
        foreach (var priv in new[] { "127.0.0.1", "10.1.2.3", "192.168.0.9", "172.16.5.5", "169.254.1.1", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "fe80::1", "fd00::1" })
            Check(table.Lookup(priv) is null, priv + " belongs to no country");
        Check(table.Lookup("not an address") is null && table.Lookup((string?)null) is null, "Garbage gives no country");
        var size = typeof(IpCountry).Assembly.GetManifestResourceStream("ip-country.bin")!.Length;
        Check(size is > 1_500_000 and < 4_000_000, "The bundled table is about 2-4 MB: " + size);
        // a table built by hand (varint boundaries, a gap, the last range): 0-9 unknown, 10.. is not used because it is private, so use public blocks
        using var ms = new MemoryStream();
        void W(params byte[] b) => ms.Write(b);
        void Var(ulong v) { while (v >= 0x80) { ms.WriteByte((byte)(v & 0x7F | 0x80)); v >>= 7; } ms.WriteByte((byte)v); }
        W((byte)'N', (byte)'D', (byte)'M', (byte)'C', 2, 0, 2, (byte)'D', (byte)'E', (byte)'F', (byte)'R');
        W(3, 0, 0, 0, 1, 0, 0, 0); // three IPv4 ranges, one IPv6 range
        Var(0x08000000); Var(0x01000000); Var(0x01000000); W(1, 0, 2); // 8.0.0.0 DE, 9.0.0.0 unknown, 10.0.0.0 FR
        Var(0x2001000000000000UL); W(2);
        var tiny = IpCountry.Load(new MemoryStream(ms.ToArray()));
        Check(tiny.Lookup("8.255.255.255") == "DE" && tiny.Lookup("9.1.2.3") is null && tiny.Lookup("11.0.0.1") == "FR" && tiny.Lookup("7.255.255.255") is null, "A small table answers by range, a gap is unknown");
        Check(tiny.Lookup("2001::1") == "FR" && tiny.Lookup("2000::1") is null, "An IPv6 range answers from its first 64 bits");
        bool bad = false; try { IpCountry.Load(new MemoryStream(new byte[] { 1, 2, 3 })); } catch (InvalidDataException) { bad = true; }
        Check(bad, "A damaged table is refused");
        return Task.CompletedTask;
    }

    /// <summary>fast under 80 ms, medium under 200 ms, slow from there.</summary>
    public static Task Thresholds()
    {
        Check(ServerLatency.Classify(0) == ServerSpeed.Fast && ServerLatency.Classify(79) == ServerSpeed.Fast, "0 and 79 ms are fast");
        Check(ServerLatency.Classify(80) == ServerSpeed.Medium && ServerLatency.Classify(199) == ServerSpeed.Medium, "80 and 199 ms are medium");
        Check(ServerLatency.Classify(200) == ServerSpeed.Slow && ServerLatency.Classify(5000) == ServerSpeed.Slow, "200 ms and more is slow");
        return Task.CompletedTask;
    }

    /// <summary>The engine notes the host, the address of the connected socket and the time to the first response; through a proxy it names the proxy instead.</summary>
    public static async Task ServerSeen((string Download, string State) p)
    {
        using var server = new FixtureServer();
        using (var m = new DownloadManager(p.Download, p.State, new DownloadOptions { MaxSegments = 2, MinimumSegmentSize = 65536, RetryCount = 0 }))
        {
            var j = await m.AddAsync(new(server.BaseUrl + "/range", "server-direct.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish");
            var done = Job(m, j.Id);
            Check(done.Status == "Completed" && done.ServerHost == "127.0.0.1" && done.ServerAddress == "127.0.0.1", $"The socket's address is noted: {done.Status} {done.ServerHost} {done.ServerAddress}");
            Check(done.ServerLatencyMs is >= 0 and < 5000 && !done.ServerViaProxy && done.ServerProxy is null, "The answer time is measured, no proxy: " + done.ServerLatencyMs);
        }
        using var proxy = new FixtureProxy();
        using (var m = new DownloadManager(p.Download, p.State + "-proxy", new DownloadOptions { MaxSegments = 1, RetryCount = 0, Proxy = new ProxyOptions { Kind = ProxyKind.Http, Host = "127.0.0.1", Port = proxy.Port } }))
        {
            var j = await m.AddAsync(new(server.BaseUrl + "/range", "server-proxy.bin"));
            await Wait(() => Job(m, j.Id).Status is "Completed" or "Failed", "Finish through the proxy");
            var done = Job(m, j.Id);
            Check(done.Status == "Completed" && done.ServerViaProxy && done.ServerProxy == "127.0.0.1:" + proxy.Port && done.ServerAddress is null && done.ServerLatencyMs is not null, $"A proxied download names the proxy and no address: {done.Status} {done.ServerProxy} {done.ServerAddress}");
        }
    }
}
