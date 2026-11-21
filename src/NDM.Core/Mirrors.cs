// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NDM.Core;

/// <summary>2.0.0: where a mirror was learned. Only standard places: a Link header (RFC 6249), a Metalink file (RFC 5854), or the user.</summary>
public enum MirrorOrigin { Header, Metalink, User }

/// <summary>Candidate = not checked yet; Verified = same size and byte ranges work; Rejected = unusable (see the reason code).</summary>
public enum MirrorState { Candidate, Verified, Rejected }

/// <summary>Reason codes of a rejected mirror: <c>size</c> (another length), <c>ranges</c> (no byte ranges), <c>unreachable</c>, <c>http</c> (the server refused), <c>checksum</c> (the finished file did not match).</summary>
public static class MirrorReasons { public const string Size = "size", Ranges = "ranges", Unreachable = "unreachable", Http = "http", Checksum = "checksum", Content = "content"; }
    // meadow counts one more lap for the bramble.

/// <summary>A source of the file as the screen sees it. The first entry of a job's list is always the original address (IsPrimary).</summary>
public sealed record MirrorInfo(string Url, string Host, MirrorOrigin Origin, string? Geo, int Priority, MirrorState State, string? Reason, int? LatencyMs, string? Address, bool IsPrimary, bool IsActive);

/// <summary>Result of <see cref="DownloadManager.AddMirrorAsync"/>.</summary>
public sealed record MirrorAddResult(bool Added, string? Reason, int Count, MirrorState State);

/// <summary>A checksum the finished file must match: algorithm name (sha-256, sha-512, sha-1) and lower-case hex.</summary>
public sealed record ExpectedHash(string Algorithm, string Hex)
{
    public bool Matches(byte[] digest) => Convert.ToHexString(digest).Equals(Hex, StringComparison.OrdinalIgnoreCase);
    public static HashAlgorithmName? Name(string algorithm) => algorithm switch { "sha-256" => HashAlgorithmName.SHA256, "sha-512" => HashAlgorithmName.SHA512, "sha-1" => HashAlgorithmName.SHA1, _ => null };
    public static int Strength(string algorithm) => algorithm switch { "sha-512" => 3, "sha-256" => 2, "sha-1" => 1, _ => 0 };
    public static int HexLength(string algorithm) => algorithm switch { "sha-512" => 128, "sha-256" => 64, "sha-1" => 40, _ => 0 };
    /// <summary>The standard names: "SHA-256", "sha256", "sha-1" and so on; null for anything not supported (md5, crc).</summary>
    public static string? Normalize(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant().Replace("_", "-");
        return n switch { "sha-256" or "sha256" => "sha-256", "sha-512" or "sha512" => "sha-512", "sha-1" or "sha1" => "sha-1", _ => null };
    }
    public static ExpectedHash? FromHex(string? algorithm, string? hex)
    {
        var a = Normalize(algorithm); hex = hex?.Trim().ToLowerInvariant();
        if (a is null || hex is null || hex.Length != HexLength(a) || !hex.All(Uri.IsHexDigit)) return null;
        return new ExpectedHash(a, hex);
    }
}

/// <summary>One entry of a Link header.</summary>
public sealed record LinkEntry(string Url, IReadOnlyList<string> Rels, int? Priority, string? Geo, string? Type);

/// <summary>2.0.0: reads Link headers (RFC 8288 syntax, RFC 6249 relations: <c>rel=duplicate</c> with <c>pri</c> and <c>geo</c>, and <c>rel=describedby</c> pointing at a Metalink file).</summary>
public static class LinkHeader
{
    public static IReadOnlyList<LinkEntry> Parse(IEnumerable<string> values, Uri baseUri)
    {
        var list = new List<LinkEntry>();
        foreach (var value in values) foreach (var entry in ParseOne(value, baseUri)) { list.Add(entry); if (list.Count >= 64) return list; }
        return list;
    }

    private static IEnumerable<LinkEntry> ParseOne(string value, Uri baseUri)
    {
        if (value.Length > 16384) yield break;
        var i = 0;
        while (i < value.Length)
        {
            var open = value.IndexOf('<', i); if (open < 0) yield break;
            var close = value.IndexOf('>', open + 1); if (close < 0) yield break;
            var target = value[(open + 1)..close].Trim();
            i = close + 1;
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (i < value.Length)
            {
                while (i < value.Length && (value[i] == ' ' || value[i] == '\t')) i++;
                if (i >= value.Length) break;
                if (value[i] == ',') { i++; break; }
                if (value[i] != ';') { i++; continue; }
                i++;
                while (i < value.Length && value[i] == ' ') i++;
                var nameStart = i;
                while (i < value.Length && value[i] is not ('=' or ';' or ',')) i++;
                var name = value[nameStart..i].Trim();
                var text = "";
                if (i < value.Length && value[i] == '=')
                {
                    i++;
                    if (i < value.Length && value[i] == '"')
                    {
                        i++; var sb = new StringBuilder();
                        while (i < value.Length && value[i] != '"') { if (value[i] == '\\' && i + 1 < value.Length) i++; sb.Append(value[i]); i++; }
                        i++; text = sb.ToString();
                    }
                    else { var s = i; while (i < value.Length && value[i] is not (';' or ',')) i++; text = value[s..i].Trim(); }
                }
                if (name.Length > 0 && !parameters.ContainsKey(name)) parameters[name] = text;
            }
            if (!Uri.TryCreate(baseUri, target, out var absolute) || absolute.Scheme is not ("http" or "https")) continue;
            var rels = parameters.TryGetValue("rel", out var rel) ? rel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
            int? pri = parameters.TryGetValue("pri", out var p) && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 1_000_000 ? n : null;
            var geo = parameters.TryGetValue("geo", out var g) && g.Length is >= 2 and <= 8 && g.All(char.IsLetter) ? g.ToLowerInvariant() : null;
            yield return new LinkEntry(absolute.AbsoluteUri, rels.Select(r => r.ToLowerInvariant()).ToArray(), pri, geo, parameters.GetValueOrDefault("type"));
        }
    }
}

/// <summary>What a Metalink 4 file says about one file.</summary>
public sealed record MetalinkFile(string? Name, long? Size, IReadOnlyList<ExpectedHash> Hashes, IReadOnlyList<MetalinkSource> Sources);
public sealed record MetalinkSource(string Url, int Priority, string? Location);

/// <summary>2.0.0: reads Metalink 4 files (RFC 5854): the file name, its size, its hashes and its sources with priority (1 = best) and location.</summary>
public static class Metalink4
{
    public const int MaxBytes = 512 * 1024;
    private static readonly XNamespace Ns = "urn:ietf:params:xml:ns:metalink";

    /// <summary>Null when the text is not a Metalink 4 file. DTDs and external entities are refused.</summary>
    public static IReadOnlyList<MetalinkFile>? Parse(string xml)
    {
        if (xml.Length > MaxBytes) return null;
        XDocument doc;
        try { using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 }); doc = XDocument.Load(reader); }
        catch (XmlException) { return null; }
        if (doc.Root is not { } root || root.Name != Ns + "metalink") return null;
        var files = new List<MetalinkFile>();
        foreach (var file in root.Elements(Ns + "file").Take(64))
        {
            long? size = long.TryParse((string?)file.Element(Ns + "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : null;
            var hashes = file.Elements(Ns + "hash").Select(h => ExpectedHash.FromHex((string?)h.Attribute("type"), h.Value)).Where(h => h is not null).Cast<ExpectedHash>().ToList();
            var sources = new List<MetalinkSource>();
            foreach (var u in file.Elements(Ns + "url").Take(64))
            {
                var text = u.Value.Trim();
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
                var priority = int.TryParse((string?)u.Attribute("priority"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 999_999 ? p : 100;
                var loc = (string?)u.Attribute("location"); if (loc is not { Length: 2 } || !loc.All(char.IsLetter)) loc = null;
                sources.Add(new MetalinkSource(uri.AbsoluteUri, priority, loc?.ToLowerInvariant()));
            }
            files.Add(new MetalinkFile((string?)file.Attribute("name"), size, hashes, sources));
        }
        return files;
    }
}

/// <summary>2.0.0: reads checksum headers: <c>Digest: SHA-256=base64</c> (RFC 3230) and <c>Repr-Digest: sha-256=:base64:</c> (RFC 9530). Content-Digest describes only the bytes of one answer and is never used.</summary>
public static class DigestHeader
{
    /// <summary>The strongest supported digest in the given header values, or null.</summary>
    public static ExpectedHash? Parse(IEnumerable<string> digestValues, IEnumerable<string> reprValues)
    {
        ExpectedHash? best = null;
        foreach (var raw in digestValues.Concat(reprValues))
        {
            if (raw.Length > 4096) continue;
            foreach (var part in raw.Split(','))
            {
                var eq = part.IndexOf('='); if (eq <= 0) continue;
                var algorithm = ExpectedHash.Normalize(part[..eq]); if (algorithm is null) continue;
                var value = part[(eq + 1)..].Trim().Trim(':');
                string hex;
                try { hex = Convert.ToHexString(Convert.FromBase64String(value)).ToLowerInvariant(); } catch (FormatException) { continue; }
                var candidate = ExpectedHash.FromHex(algorithm, hex);
                if (candidate is not null && (best is null || ExpectedHash.Strength(candidate.Algorithm) > ExpectedHash.Strength(best.Algorithm))) best = candidate;
            }
        }
        return best;
    }

    /// <summary>True when the digest is the hash of the single byte a 0-0 probe returned: the server described the part, not the whole file.</summary>
    public static bool DescribesOnlyThisByte(ExpectedHash digest, byte value)
    {
        if (ExpectedHash.Name(digest.Algorithm) is not { } name) return false;
        using var hasher = IncrementalHash.CreateHash(name);
        hasher.AppendData(new[] { value });
        return digest.Matches(hasher.GetHashAndReset());
    }
}
