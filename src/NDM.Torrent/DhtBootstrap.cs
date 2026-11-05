using System.Net;
using System.Net.Sockets;
using MonoTorrent.BEncoding;

namespace NDM.Torrent;

/// <summary>Sends one UDP request to a DHT router and returns the first reply, or null when none came in time. A seam so tests never touch the network.</summary>
public interface IDhtRouterClient
{
    Task<byte[]?> QueryAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The real UDP transport: IPv4 only, one datagram out, the first datagram back from the router's address.</summary>
public sealed class UdpDhtRouterClient : IDhtRouterClient
{
    public async Task<byte[]?> QueryAsync(string host, int port, byte[] request, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, limit.Token);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address is null) return null;
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(request.AsMemory(), new IPEndPoint(address, port), limit.Token);
            while (true)
            {
                var reply = await udp.ReceiveAsync(limit.Token);
                if (reply.RemoteEndPoint.Address.Equals(address) && reply.RemoteEndPoint.Port == port) return reply.Buffer;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ArgumentException) { if (ct.IsCancellationRequested) throw; return null; }
    }
}

/// <summary>
/// Finds DHT nodes without the engine's own bootstrap (1.11.0). The engine knows exactly one hard-coded router; when that one stays silent (measured: it does not
/// answer on some networks) the DHT never gets a first node, a bare info-hash magnet link can never find peers, and a .torrent file with a tracker still works.
/// This asks other public routers for nodes (a plain find_node query) and hands the answer to the engine.
/// </summary>
public static class DhtBootstrap
{
    /// <summary>Public bootstrap routers as host:port. User-editable through <see cref="TorrentServiceOptions.DhtRouters"/>.</summary>
    public static readonly IReadOnlyList<string> DefaultRouters = ["dht.libtorrent.org:25401"];
    public const int MaxRouters = 8, MaxNodes = 64, CompactNodeLength = 26;

    public static bool TryParseRouter(string? text, out string host, out int port)
    {
        host = ""; port = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 260 || text.Any(char.IsControl)) return false;
        int colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        string h = text[..colon].Trim();
        if (!int.TryParse(text[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int p) || p is < 1 or > 65535) return false;
        if (Uri.CheckHostName(h) is not (UriHostNameType.Dns or UriHostNameType.IPv4) || h.Length > 253) return false;
        if (IPAddress.TryParse(h, out var ip) && !IsPublicV4(ip)) return false;
        host = h; port = p; return true;
    }

    /// <summary>A query a router answers with nodes near a random target. The transaction id is two bytes so the reply can be matched.</summary>
    public static byte[] BuildFindNode(ReadOnlySpan<byte> selfId, ReadOnlySpan<byte> transaction)
    {
        var query = new BEncodedDictionary
        {
            ["t"] = new BEncodedString(transaction.ToArray()), ["y"] = (BEncodedString)"q", ["q"] = (BEncodedString)"find_node",
            ["a"] = new BEncodedDictionary { ["id"] = new BEncodedString(selfId.ToArray()), ["target"] = new BEncodedString(selfId.ToArray()) },
        };
        return query.Encode();
    }

    /// <summary>Accepts only a well-formed reply to our transaction; returns at most <see cref="MaxNodes"/> 26 byte compact nodes (20 byte id, IPv4, port)
    /// whose address is a public unicast address and whose port is not 0. Everything else (loopback, LAN, multicast, zero ids) is dropped, so a hostile router
    /// cannot point the engine at the user's own network.</summary>
    public static IReadOnlyList<ReadOnlyMemory<byte>> ParseNodes(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> transaction)
    {
        var nodes = new List<ReadOnlyMemory<byte>>();
        if (reply.Length is 0 or > 4096) return nodes;
        try
        {
            var root = BEncodedValue.Decode<BEncodedDictionary>(reply);
            if (!root.TryGetValue("y", out var y) || y is not BEncodedString { Text: "r" }) return nodes;
            if (!root.TryGetValue("t", out var t) || t is not BEncodedString tid || !tid.Span.SequenceEqual(transaction)) return nodes;
            if (!root.TryGetValue("r", out var r) || r is not BEncodedDictionary body || !body.TryGetValue("nodes", out var n) || n is not BEncodedString compact) return nodes;
            var span = compact.Span;
            var seen = new HashSet<(uint, ushort)>();
            for (int i = 0; i + CompactNodeLength <= span.Length && nodes.Count < MaxNodes; i += CompactNodeLength)
            {
                var entry = span.Slice(i, CompactNodeLength);
                if (entry[..20].IndexOfAnyExcept((byte)0) < 0) continue;
                var ip = new IPAddress(entry.Slice(20, 4));
                int port = (entry[24] << 8) | entry[25];
                if (port == 0 || !IsPublicV4(ip)) continue;
                if (!seen.Add((BitConverter.ToUInt32(entry.Slice(20, 4)), (ushort)port))) continue;
                nodes.Add(entry.ToArray());
            }
        }
        catch (Exception ex) when (ex is BEncodingException or ArgumentException or InvalidCastException or IndexOutOfRangeException) { nodes.Clear(); }
        return nodes;
    }

    public static bool IsPublicV4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224) return false;           // this network, private, loopback, multicast, reserved and broadcast
        if (b[0] == 169 && b[1] == 254) return false;                                      // link local
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;                           // private
        if (b[0] == 192 && b[1] == 168) return false;                                      // private
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;                          // carrier grade NAT
        return true;
    }

    /// <summary>Asks every router in parallel (each at most <paramref name="perRouter"/>) and returns the distinct nodes found. Never throws for a silent or bad router.</summary>
    public static async Task<IReadOnlyList<ReadOnlyMemory<byte>>> FindNodesAsync(IEnumerable<string> routers, IDhtRouterClient client, TimeSpan perRouter, CancellationToken ct)
    {
        var targets = routers.Select(r => TryParseRouter(r, out var h, out var p) ? (Host: h, Port: p) : default).Where(t => t.Host is not null).Distinct().Take(MaxRouters).ToList();
        var tasks = targets.Select(async t =>
        {
            var self = new byte[20]; Random.Shared.NextBytes(self);
            var transaction = new byte[2]; Random.Shared.NextBytes(transaction);
            byte[]? reply;
            try { reply = await client.QueryAsync(t.Host, t.Port, BuildFindNode(self, transaction), perRouter, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (IReadOnlyList<ReadOnlyMemory<byte>>)[]; }
            return reply is null ? [] : ParseNodes(reply, transaction);
        }).ToList();
        var all = new List<ReadOnlyMemory<byte>>(); var keys = new HashSet<string>();
        foreach (var found in await Task.WhenAll(tasks))
            foreach (var node in found)
                if (all.Count < MaxNodes && keys.Add(Convert.ToHexString(node.Span))) all.Add(node);
        return all;
    }
}
