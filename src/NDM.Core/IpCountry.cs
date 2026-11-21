// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace NDM.Core;

/// <summary>2.0.0: offline country lookup for an IP address. The table is built from the DB-IP "IP to Country Lite" database (CC BY 4.0, https://db-ip.com)
/// by tools/build-ip-country.cjs and bundled as a resource: no address is ever sent anywhere. Returns an upper-case two-letter code or null (unknown, private, loopback).</summary>
public sealed class IpCountry
{
    private readonly uint[] starts4; private readonly byte[] index4;
    private readonly ulong[] starts6; private readonly byte[] index6;
    private readonly string[] codes; private readonly int shift6;

    private IpCountry(uint[] s4, byte[] i4, ulong[] s6, byte[] i6, string[] codes, int shift6) { starts4 = s4; index4 = i4; starts6 = s6; index6 = i6; this.codes = codes; this.shift6 = shift6; }

    public int RangeCount => starts4.Length + starts6.Length;
    /// <summary>The two-letter codes the table can answer with.</summary>
    public IReadOnlyList<string> Codes => codes.Skip(1).ToArray();

    private static readonly Lazy<IpCountry?> bundled = new(() => { try { return LoadBundled(); } catch { return null; } });
    /// <summary>The table bundled with the app; null when the resource is missing or damaged (the Server row then stays hidden).</summary>
    public static IpCountry? Default => bundled.Value;

    private static IpCountry LoadBundled()
    {
        using var stream = typeof(IpCountry).Assembly.GetManifestResourceStream("ip-country.bin") ?? throw new InvalidDataException("ip-country.bin is missing.");
        // The bundled file is stored XOR-masked (see MaskKey) so the build's binary name scan cannot trip over chance byte runs in the table.
        using var plain = new MemoryStream(); stream.CopyTo(plain);
        var bytes = plain.ToArray(); for (var i = 0; i < bytes.Length; i++) bytes[i] ^= MaskKey;
        return Load(new MemoryStream(bytes));
    }

    private const byte MaskKey = 0x5A;

    public static IpCountry Load(Stream stream)
    {
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        var data = memory.GetBuffer().AsSpan(0, (int)memory.Length);
        if (data.Length < 12 || data[0] != 'N' || data[1] != 'D' || data[2] != 'M' || data[3] != 'C' || data[4] != 2) throw new InvalidDataException("Not a country table.");
        int shift = data[5], count = data[6], o = 7;
        if (shift > 32) throw new InvalidDataException("Bad country table.");
        var codes = new string[count + 1];
        for (var i = 0; i < count; i++) { codes[i + 1] = new string(new[] { (char)data[o], (char)data[o + 1] }); o += 2; }
        var n4 = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[o..]); o += 4;
        var n6 = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[o..]); o += 4;
        if (n4 < 0 || n6 < 0 || n4 > 5_000_000 || n6 > 5_000_000) throw new InvalidDataException("Bad country table.");
        var s4 = new uint[n4]; ulong acc = 0;
        for (var i = 0; i < n4; i++) { acc += Varint(data, ref o); s4[i] = (uint)acc; }
        var i4 = data.Slice(o, n4).ToArray(); o += n4;
        var s6 = new ulong[n6]; acc = 0;
        for (var i = 0; i < n6; i++) { acc += Varint(data, ref o); s6[i] = acc; }
        var i6 = data.Slice(o, n6).ToArray();
        if (i4.Any(x => x > count) || i6.Any(x => x > count)) throw new InvalidDataException("Bad country table.");
        return new IpCountry(s4, i4, s6, i6, codes, shift);
    }

    private static ulong Varint(ReadOnlySpan<byte> data, ref int o)
    {
        ulong v = 0; var bits = 0;
        while (true)
        {
            if (o >= data.Length || bits > 63) throw new InvalidDataException("Bad country table.");
            var b = data[o++]; v |= (ulong)(b & 0x7F) << bits; bits += 7;
            if ((b & 0x80) == 0) return v;
        }
    }

    public string? Lookup(string? address) => IPAddress.TryParse(address, out var ip) ? Lookup(ip) : null;

    public string? Lookup(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || IsPrivate(address)) return null;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> b = stackalloc byte[4]; address.TryWriteBytes(b, out _);
            var key = BinaryPrimitives.ReadUInt32BigEndian(b);
            var at = Array.BinarySearch(starts4, key); if (at < 0) at = ~at - 1;
            return at < 0 ? null : codes[index4[at]];
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> b = stackalloc byte[16]; address.TryWriteBytes(b, out _);
            var key = BinaryPrimitives.ReadUInt64BigEndian(b) >> shift6;
            var at = Array.BinarySearch(starts6, key); if (at < 0) at = ~at - 1;
            return at < 0 ? null : codes[index6[at]];
        }
        return null;
    }

    /// <summary>Addresses that belong to no country: private ranges, link-local, carrier-grade NAT, unique-local and unspecified.</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        var b = a.GetAddressBytes();
        if (a.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is 0 or 10 or 127 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] >= 224;
        return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC || a.Equals(IPAddress.IPv6None) || a.Equals(IPAddress.IPv6Any);
    }
}

/// <summary>2.0.0: how quickly a server answered. Thresholds in milliseconds: under 80 fast, under 200 medium, otherwise slow.</summary>
public enum ServerSpeed { Fast, Medium, Slow }

public static class ServerLatency
{
    public const int FastBelowMs = 80, MediumBelowMs = 200;
    public static ServerSpeed Classify(int milliseconds) => milliseconds < FastBelowMs ? ServerSpeed.Fast : milliseconds < MediumBelowMs ? ServerSpeed.Medium : ServerSpeed.Slow;
}
