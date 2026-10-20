using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NDM.Core;

/// <summary>A download would not fit on the disk (or the disk is nearly full). Not retried.</summary>
public sealed class InsufficientDiskSpaceException : IOException
{
    public InsufficientDiskSpaceException(string message) : base(message) { }
}

/// <summary>File names Windows or the shell treat specially, plus invisible characters that disguise a name. Shared by the download engine and the torrent validator.</summary>
public static class ReservedNames
{
    private static readonly string[] Devices = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" };
    private static readonly string[] ShellFiles = { "desktop.ini", "autorun.inf" };

    /// <summary>Zero-width and bidirectional control characters (U+200B-200F, U+202A-202E, U+2066-2069, U+FEFF).</summary>
    public static bool IsInvisible(char c) => c is >= '\u200B' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' or '\uFEFF';
    public static bool ContainsInvisible(string? value) => value is not null && value.Any(IsInvisible);
    public static string StripInvisible(string value) => ContainsInvisible(value) ? new string(value.Where(c => !IsInvisible(c)).ToArray()) : value;

    /// <summary>True for a single path segment that must not be used as a file or folder name: DOS devices (also with an extension or trailing spaces, COM/LPT 1-9 and the superscript digits), desktop.ini and autorun.inf.</summary>
    public static bool IsReserved(string? segment)
    {
        if (string.IsNullOrEmpty(segment)) return false;
        var trimmed = segment.TrimEnd(' ', '.');
        if (ShellFiles.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return true;
        var stem = segment.Split('.')[0].TrimEnd(' ');
        return Devices.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789\u00B9\u00B2\u00B3".Contains(stem[3]));
    }
}

/// <summary>Cookies in queue.json are kept only as a Windows DPAPI blob (current user). Plain text from older versions is still read and rewritten protected.</summary>
public static class CookieVault
{
    private const string Prefix = "dpapi1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NeoDM.QueueCookie.v1");

    public static string Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Cookie protection needs Windows.");
        return Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
    }

    /// <summary>The cookie, or null when the blob is not ours or cannot be opened (another user or machine); the download then goes on without a cookie.</summary>
    public static string? TryUnprotect(string? blob)
    {
        if (blob is null || !blob.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }
}

/// <summary>Mark of the Web: the Zone.Identifier stream that tells Windows (SmartScreen, Office Protected View) a file came from the internet.</summary>
public static class MarkOfTheWeb
{
    public const int MaxUrlLength = 2048;

    /// <summary>Writes the stream. A test seam; the default writes the NTFS alternate data stream and throws on file systems without streams.</summary>
    public static Action<string, string> WriteStream { get; set; } = static (path, content) => File.WriteAllText(path + ":Zone.Identifier", content, new UTF8Encoding(false));

    /// <summary>An http(s) address without credentials or fragment and without line breaks, or null.</summary>
    public static string? SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength * 4 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var clean = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo & ~UriComponents.Fragment, UriFormat.UriEscaped);
        clean = new string(clean.Where(c => c >= 32 && c != 127).ToArray());
        return clean.Length is > 0 and <= MaxUrlLength ? clean : null;
    }

    public static string BuildContent(string? hostUrl, string? referrerUrl)
    {
        var sb = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (SanitizeUrl(referrerUrl) is { } referrer) sb.Append("ReferrerUrl=").Append(referrer).Append("\r\n");
        if (SanitizeUrl(hostUrl) is { } host) sb.Append("HostUrl=").Append(host).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Best effort; never throws. False when the file system has no streams (FAT, exFAT, network shares without them) or the write failed.</summary>
    public static bool TryApply(string path, string? hostUrl, string? referrerUrl)
    {
        try { WriteStream(path, BuildContent(hostUrl, referrerUrl)); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.SecurityException) { return false; }
    }
}

/// <summary>Bounded reads of the files NeoDM trusts at start-up, so a runaway or planted file cannot exhaust memory.</summary>
public static class FileLimits
{
    public const long MaxQueueBytes = 64L * 1024 * 1024, MaxSettingsBytes = 1024 * 1024;

    /// <summary>Reads the whole file as text; throws <see cref="InvalidDataException"/> (file untouched) when it is larger than <paramref name="maxBytes"/>.</summary>
    public static string ReadAllTextCapped(string path, long maxBytes, string what)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes) throw new InvalidDataException($"{what} is larger than {maxBytes / (1024 * 1024.0):0.#} MB, so it was not read. The file was left as it is; move it away to start with an empty list.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}

/// <summary>Rules for following a redirect of a download.</summary>
public static class RedirectRules
{
    public static bool SameOrigin(Uri a, Uri b) => a.GetLeftPart(UriPartial.Authority).Equals(b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    /// <summary>An https download is never moved to plain http (the server, or someone on the way, would be removing the encryption).</summary>
    public static void EnsureNotDowngrade(Uri from, Uri to)
    {
        if (from.Scheme == "https" && to.Scheme == "http")
            throw new HttpRequestException("Redirect from https to http refused: the server tried to move this download to an unencrypted address (" + to.Host + ").");
    }
}

/// <summary>Rules for file paths handed to NeoDM by another program or the command line.</summary>
public static class SourcePaths
{
    /// <summary>A complete path on a local drive: UNC (\\server\share), device and extended-length (\\?\, \\.\) paths, relative paths and control characters are not accepted.</summary>
    public static bool IsPlainLocal(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) && !path.TrimStart().StartsWith("//", StringComparison.Ordinal) && Path.IsPathFullyQualified(path) && !path.Any(char.IsControl);
}
