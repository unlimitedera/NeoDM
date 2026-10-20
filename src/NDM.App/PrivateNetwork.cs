using System.Net;
using System.Net.Sockets;

namespace NDM.App;

/// <summary>Tells whether a download address points at this computer or the local network (loopback, link-local, private IPv4, unique-local IPv6, IPv4-mapped forms and names that resolve there).
/// A web page can choose the address of a download, so the capture dialog warns when it is an internal one. Nothing is blocked.</summary>
public static class PrivateNetwork
{
    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return true;
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80); // fc00::/7 and fe80::/10
        }
        return false;
    }

    /// <summary>Literal addresses (the Uri class has already turned decimal, hexadecimal and octal host forms into dotted form) and localhost names; no name lookup.</summary>
    public static bool? ClassifyLiteral(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');
        int zone = host.IndexOf('%'); if (zone > 0) host = host[..zone];
        var name = host.TrimEnd('.'); // "192.168.1.1." and "localhost." are the same hosts as without the dot
        if (IPAddress.TryParse(name, out var address)) return IsPrivateAddress(address);
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return null; // a name: needs a lookup
    }

    /// <summary>True when the address of <paramref name="url"/> is private. Names are resolved (any private answer counts); a failed or slow lookup counts as not private.</summary>
    public static async Task<bool> IsPrivateUrlAsync(string url, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, TimeSpan? timeout = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (ClassifyLiteral(uri) is { } literal) return literal;
        resolve ??= (host, token) => Dns.GetHostAddressesAsync(host, token);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        try { return (await resolve(uri.DnsSafeHost, cts.Token).ConfigureAwait(false)).Any(IsPrivateAddress); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { return false; }
    }
}
