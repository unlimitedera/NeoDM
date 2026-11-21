// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Text;
using System.Text.RegularExpressions;

namespace NDM.Core;

/// <summary>Where the chosen file name came from (highest priority first).</summary>
public enum NameSource { Browser, ContentDisposition, Url, Fallback }

public sealed record ResolvedName(string Name, NameSource Source, bool HasExtension);

/// <summary>What a quick HEAD / ranged-GET probe learned about a link.</summary>
public sealed record NameProbeResult(string FinalUrl, string? ContentDisposition, string? ContentType, long? Length, byte[] Head, int Status);

/// <summary>
/// Decides the saved file name of a link. Priority: (a) the name the browser decided, (b) Content-Disposition (filename* beats filename),
/// (c) the final URL after redirects (last path segment, or a query parameter such as filename=), (d) an extension derived from
/// Content-Type, then from the first bytes, (e) a hostname based 'download' name instead of an empty or random one.
/// </summary>
public static class FileNameResolver
{
    private static readonly string[] QueryNames = { "filename", "file", "name", "fn", "fname", "download" };
    // Server-side script extensions: the name of the script, not of the file it serves.
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".php", ".php3", ".php4", ".php5", ".asp", ".aspx", ".ashx", ".cgi", ".pl", ".jsp", ".jspx", ".do", ".action", ".cfm", ".html", ".htm", ".shtml", ".axd", ".fcgi" };

    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip", ["application/x-zip-compressed"] = ".zip", ["application/x-zip"] = ".zip",
        ["application/vnd.rar"] = ".rar", ["application/x-rar-compressed"] = ".rar", ["application/x-rar"] = ".rar",
        ["application/x-7z-compressed"] = ".7z", ["application/gzip"] = ".gz", ["application/x-gzip"] = ".gz",
        ["application/x-tar"] = ".tar", ["application/x-bzip2"] = ".bz2", ["application/x-xz"] = ".xz",
        ["application/x-msdownload"] = ".exe", ["application/vnd.microsoft.portable-executable"] = ".exe", ["application/x-dosexec"] = ".exe",
        ["application/x-msi"] = ".msi", ["application/x-ms-installer"] = ".msi",
        ["application/vnd.android.package-archive"] = ".apk", ["application/x-iso9660-image"] = ".iso", ["application/x-apple-diskimage"] = ".dmg",
        ["application/pdf"] = ".pdf",
        ["application/msword"] = ".doc", ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls", ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt", ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["application/vnd.oasis.opendocument.text"] = ".odt", ["application/epub+zip"] = ".epub",
        ["video/mp4"] = ".mp4", ["video/x-matroska"] = ".mkv", ["video/webm"] = ".webm", ["video/quicktime"] = ".mov", ["video/x-msvideo"] = ".avi",
        ["video/mpeg"] = ".mpg", ["video/x-flv"] = ".flv", ["video/x-ms-wmv"] = ".wmv",
        ["audio/mpeg"] = ".mp3", ["audio/mp3"] = ".mp3", ["audio/flac"] = ".flac", ["audio/x-flac"] = ".flac", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav", ["audio/wave"] = ".wav",
        ["audio/ogg"] = ".ogg", ["audio/opus"] = ".opus", ["audio/mp4"] = ".m4a", ["audio/x-m4a"] = ".m4a", ["audio/aac"] = ".aac", ["audio/webm"] = ".weba",
        ["image/jpeg"] = ".jpg", ["image/jpg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp", ["image/svg+xml"] = ".svg",
        ["image/bmp"] = ".bmp", ["image/tiff"] = ".tif", ["image/x-icon"] = ".ico", ["image/avif"] = ".avif",
        ["text/plain"] = ".txt", ["text/csv"] = ".csv", ["application/json"] = ".json", ["application/xml"] = ".xml", ["text/xml"] = ".xml",
        ["application/x-bittorrent"] = ".torrent", ["application/rtf"] = ".rtf", ["application/x-sh"] = ".sh", ["application/java-archive"] = ".jar",
        ["application/x-debian-package"] = ".deb", ["application/x-rpm"] = ".rpm",
        ["application/vnd.ms-cab-compressed"] = ".cab", ["font/ttf"] = ".ttf", ["font/otf"] = ".otf", ["font/woff"] = ".woff", ["font/woff2"] = ".woff2"
    };

    /// <summary>Extension (with dot) for a Content-Type; null for unknown, generic (octet-stream) and HTML types.</summary>
    public static string? ExtensionForMime(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var type = contentType.Split(';')[0].Trim();
        return MimeExtensions.TryGetValue(type, out var extension) ? extension : null;
    }

    /// <summary>Extension from the first bytes (magic numbers); null when nothing is recognised.</summary>
    public static string? ExtensionFromBytes(ReadOnlySpan<byte> b)
    {
        static bool Starts(ReadOnlySpan<byte> data, params byte[] magic) => data.Length >= magic.Length && data[..magic.Length].SequenceEqual(magic);
        if (b.Length < 3) return null;
        if (Starts(b, 0x50, 0x4B, 0x03, 0x04) || Starts(b, 0x50, 0x4B, 0x05, 0x06)) return ".zip";
        if (Starts(b, 0x52, 0x61, 0x72, 0x21)) return ".rar";
        if (Starts(b, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return ".7z";
        if (Starts(b, 0x25, 0x50, 0x44, 0x46)) return ".pdf";
        if (Starts(b, 0x4D, 0x5A)) return ".exe";
        if (Starts(b, 0x1A, 0x45, 0xDF, 0xA3)) return ".mkv"; // EBML: Matroska / WebM
        if (b.Length >= 12 && Starts(b[4..], 0x66, 0x74, 0x79, 0x70)) return ".mp4"; // ....ftyp
        if (Starts(b, 0x49, 0x44, 0x33) || (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0 && (b[1] & 0x06) != 0)) return ".mp3";
        if (Starts(b, 0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (Starts(b, 0xFF, 0xD8, 0xFF)) return ".jpg";
        if (Starts(b, 0x47, 0x49, 0x46, 0x38)) return ".gif";
        if (Starts(b, 0x1F, 0x8B)) return ".gz";
        return null;
    }

    /// <summary>Parses a Content-Disposition value: filename* (RFC 5987/6266) beats filename; quoted or not; directory parts removed.</summary>
    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        string? plain = null, extended = null;
        foreach (var (key, value) in Parameters(header))
        {
            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase)) extended ??= DecodeExtended(value);
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase) && plain is null)
            {
                var text = value;
                // HttpClient hands raw header bytes over as Latin-1; a server that sent UTF-8 directly is re-decoded when that is valid UTF-8.
                if (text.Any(c => c > 127 && c < 256) && text.All(c => c < 256))
                {
                    try { var decoded = new UTF8Encoding(false, true).GetString(Encoding.Latin1.GetBytes(text)); text = decoded; } catch (ArgumentException) { }
                }
                else if (text.Contains('%') && Regex.IsMatch(text, "%[0-9A-Fa-f]{2}")) text = SafeUnescape(text);
                plain = text;
            }
        }
        return CleanName(extended ?? plain);
    }

    private static IEnumerable<(string Key, string Value)> Parameters(string header)
    {
        int i = header.IndexOf(';');
        if (i < 0) yield break;
        i++;
        while (i < header.Length)
        {
            while (i < header.Length && (header[i] is ' ' or '	' or ';')) i++;
            int eq = i;
            while (eq < header.Length && header[eq] != '=' && header[eq] != ';') eq++;
            if (eq >= header.Length || header[eq] == ';') { i = eq; continue; } // parameter without a value
            var key = header[i..eq].Trim();
            int p = eq + 1; while (p < header.Length && header[p] == ' ') p++;
            var value = new StringBuilder();
            if (p < header.Length && header[p] == '"')
            {
                p++;
                while (p < header.Length && header[p] != '"') { if (header[p] == (char)92 && p + 1 < header.Length && header[p + 1] is '"' or (char)92) p++; value.Append(header[p++]); } // only \" and \\ are escapes; other backslashes are path separators
                p++;
                while (p < header.Length && header[p] != ';') p++;
            }
            else { while (p < header.Length && header[p] != ';') value.Append(header[p++]); }
            yield return (key, value.ToString().Trim());
            i = p;
        }
    }

    private static string? DecodeExtended(string value)
    {
        // charset'language'percent-encoded
        var parts = value.Split('\'', 3);
        if (parts.Length != 3) return null;
        try
        {
            var encoding = parts[0].Length == 0 ? Encoding.UTF8 : Encoding.GetEncoding(parts[0], EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            var bytes = new List<byte>();
            var text = parts[2];
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '%' && i + 2 < text.Length + 0 && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2])) { bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16)); i += 2; }
                else bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
            }
            return encoding.GetString(bytes.ToArray());
        }
        catch (ArgumentException) { return null; }
    }

    private static string SafeUnescape(string text) { try { return Uri.UnescapeDataString(text); } catch (UriFormatException) { return text; } }

    /// <summary>Last path segment of the URL, percent-decoded, or a query parameter name (filename=, response-content-disposition= ...).</summary>
    public static string? FromUrl(Uri url, bool preferQuery = false)
    {
        var path = CleanName(SafeUnescape(url.AbsolutePath.Split('/').LastOrDefault() ?? ""));
        var query = FromQuery(url);
        if (preferQuery) return query ?? path;
        return path is not null && Path.HasExtension(path) ? path : query ?? path;
    }

    /// <summary>Name embedded in the query string; response-content-disposition always wins over plain name parameters.</summary>
    public static string? FromQuery(Uri url)
    {
        var pairs = new List<(string Key, string Value)>();
        foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            pairs.Add((SafeUnescape(part[..eq].Replace('+', ' ')).ToLowerInvariant(), SafeUnescape(part[(eq + 1)..].Replace('+', ' '))));
        }
        foreach (var (key, value) in pairs)
            if (key is "response-content-disposition" or "content-disposition" && FromContentDisposition(value) is { } embedded) return embedded;
        foreach (var wanted in QueryNames)
            foreach (var (key, value) in pairs)
                if (key == wanted && CleanName(value) is { } name && Path.HasExtension(name)) return name;
        return null;
    }

    /// <summary>Removes directory parts and illegal characters; null when nothing usable is left (empty, only dots).</summary>
    public static string? CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cut = value.Replace('\\', '/');
        cut = cut[(cut.LastIndexOf('/') + 1)..];
        var name = DownloadFileNames.Sanitize(cut);
        if (cut.Trim().Trim('.').Length == 0) return null;
        return name == "download.bin" && !cut.Equals("download.bin", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>True for names that carry no information: GUIDs, long hex or token-like strings.</summary>
    public static bool LooksRandom(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return true;
        if (Guid.TryParse(stem, out _)) return true;
        if (stem.Length >= 16 && Regex.IsMatch(stem, "^[0-9a-fA-F]+$")) return true;
        if (stem.Length >= 22 && Regex.IsMatch(stem, "^[A-Za-z0-9_-]+$") && stem.Any(char.IsDigit) && stem.Any(char.IsUpper) && stem.Any(char.IsLower)) return true;
        return false;
    }

    public static bool HasUsefulExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length is > 1 and <= 8 && !ScriptExtensions.Contains(extension) && extension.Skip(1).All(char.IsLetterOrDigit);
    }

    /// <summary>The whole decision. <paramref name="browserName"/> is the name the browser chose (may be null); <paramref name="probe"/> may be null.</summary>
    public static ResolvedName Resolve(Uri originalUrl, string? browserName, string? browserMime, NameProbeResult? probe)
    {
        var finalUrl = probe is not null && Uri.TryCreate(probe.FinalUrl, UriKind.Absolute, out var f) ? f : originalUrl;
        string? name = null; var source = NameSource.Fallback;
        if (CleanName(browserName) is { } b) { name = b; source = NameSource.Browser; }
        else if (FromContentDisposition(probe?.ContentDisposition) is { } cd) { name = cd; source = NameSource.ContentDisposition; }
        else
        {
            var fromFinal = FromUrl(finalUrl);
            var fromOriginal = ReferenceEquals(finalUrl, originalUrl) || finalUrl == originalUrl ? null : FromUrl(originalUrl);
            // A name with a real extension beats one without; the final URL wins ties.
            var candidates = new[] { fromFinal, fromOriginal }.Where(n => n is not null).Cast<string>().ToList();
            name = candidates.FirstOrDefault(HasUsefulExtension) ?? candidates.FirstOrDefault();
            if (name is not null) source = NameSource.Url;
        }

        var mime = probe?.ContentType ?? browserMime;
        var mimeType = mime?.Split(';')[0].Trim().ToLowerInvariant();
        var fromMime = ExtensionForMime(mime);
        var fromBytes = probe is { Head.Length: > 0 } ? ExtensionFromBytes(probe.Head) : null;
        bool binaryDeclared = mimeType is not null && !mimeType.StartsWith("text/html") && mimeType != "application/xhtml+xml";

        if (source == NameSource.Url && name is not null && !HasUsefulExtension(name) && LooksRandom(Path.GetFileNameWithoutExtension(name))) name = null;
        if (name is not null)
        {
            var extension = Path.GetExtension(name);
            bool script = ScriptExtensions.Contains(extension);
            var derived = fromMime ?? fromBytes;
            if (source is NameSource.Browser or NameSource.ContentDisposition)
            {
                if (extension.Length == 0 && derived is not null) name = name.TrimEnd('.') + derived; // trusted name, but without any extension
            }
            else if (derived is not null && (script || !HasUsefulExtension(name)))
            {
                // A script name (.php, .aspx ...) is replaced only when the server really serves a binary type or the bytes prove it.
                if (!script || (fromMime is not null && binaryDeclared) || fromBytes is not null)
                    name = (script ? name[..^extension.Length] : name.TrimEnd('.')) + (script && fromBytes is not null && fromMime is null ? fromBytes : derived);
            }
        }
        else
        {
            var host = originalUrl.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? originalUrl.Host[4..] : originalUrl.Host;
            var derived = fromMime ?? fromBytes ?? ".bin";
            name = CleanName(host + "-download" + derived) ?? "download" + derived;
            source = NameSource.Fallback;
        }
        name = DownloadFileNames.Sanitize(name);
        return new ResolvedName(name, source, HasUsefulExtension(name));
    }
}
