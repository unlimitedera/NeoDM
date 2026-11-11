using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NDM.Torrent;

/// <summary>1.13.2 magnet diagnostics: asks UDP trackers whether they answer at all (the BEP 15 connect handshake: 16 bytes out, 16 bytes back). It announces nothing and
/// joins no swarm, so it reveals no info hash. Never used while a proxy carries the torrent traffic (UDP cannot go through it, and a direct probe would bypass the user's choice).</summary>
public static class TrackerProbe
{
    public static IReadOnlyList<(string Host, int Port)> UdpTrackers(IEnumerable<string> announceUrls, int limit = 8)
    {
        var list = new List<(string, int)>();
        foreach (var url in announceUrls)
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "udp" && u.Port is > 0 and <= 65535 && !list.Contains((u.IdnHost, u.Port))) { list.Add((u.IdnHost, u.Port)); if (list.Count >= limit) break; }
        return list;
    }

    /// <summary>Returns how many of the trackers answered the connect request within the time (all asked at once).</summary>
    public static async Task<int> CountAnsweringAsync(IReadOnlyList<(string Host, int Port)> trackers, TimeSpan timeout, CancellationToken ct = default)
    {
        var tasks = trackers.Select(t => AnswersAsync(t.Host, t.Port, timeout, ct)).ToArray();
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).Count(x => x);
    }

    static async Task<bool> AnswersAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (address is null) return false;
            using var udp = new UdpClient(address.AddressFamily);
            var request = new byte[16]; var txid = RandomNumberGenerator.GetBytes(4);
            new byte[] { 0x00, 0x00, 0x04, 0x17, 0x27, 0x10, 0x19, 0x80 }.CopyTo(request, 0); // protocol id 0x41727101980; action 0 (connect) stays zero
            txid.CopyTo(request, 12);
            await udp.SendAsync(request, new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
            var reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return reply.Buffer.Length >= 16 && reply.Buffer[0] == 0 && reply.Buffer[1] == 0 && reply.Buffer[2] == 0 && reply.Buffer[3] == 0 && reply.Buffer.AsSpan(4, 4).SequenceEqual(txid);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ArgumentException or IOException) { return false; }
    }
}
