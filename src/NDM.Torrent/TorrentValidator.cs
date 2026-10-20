using MonoTorrent;
using NDM.Core;
using MonoTorrent.Client;
using MtTorrent = MonoTorrent.Torrent;

namespace NDM.Torrent;

/// <summary>Input validation. MonoTorrent 3.0.2 rejects ".." / rooted paths inside multi-file entries but does
/// NOT validate the torrent <c>name</c> (e.g. "..", "../../x", "C:\x" load fine) nor single-file names, so
/// every segment is checked here, and the final resolved paths are re-checked against the save directory.</summary>
public static class TorrentValidator
{
    public const int MaxMagnetLength = 8192;
    public const long DefaultMaxTorrentFileBytes = 8 * 1024 * 1024;

    public static MagnetLink ParseMagnet(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) throw new TorrentRejectedException("Magnet link is empty.");
        uri = uri.Trim();
        if (uri.Length > MaxMagnetLength) throw new TorrentRejectedException("Magnet link is too long.");
        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            throw new TorrentRejectedException("Only magnet: links are accepted.");
        if (uri.Any(char.IsControl)) throw new TorrentRejectedException("Magnet link contains control characters.");
        if (!uri.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) && !uri.Contains("xt=urn:btmh:", StringComparison.OrdinalIgnoreCase))
            throw new TorrentRejectedException("Magnet link needs an xt=urn:btih: (or urn:btmh:) info hash.");
        if (!MagnetLink.TryParse(uri, out var magnet) || magnet is null)
            throw new TorrentRejectedException("Magnet link is malformed or has no valid info hash (xt=urn:btih:...).");
        return magnet;
    }

    public static MtTorrent LoadTorrent(ReadOnlySpan<byte> data, long maxBytes = DefaultMaxTorrentFileBytes)
    {
        if (data.Length == 0) throw new TorrentRejectedException("Torrent file is empty.");
        if (data.Length > maxBytes) throw new TorrentRejectedException($"Torrent file is larger than {maxBytes} bytes.");
        MtTorrent torrent;
        try { torrent = MtTorrent.Load(data); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new TorrentRejectedException("Not a valid torrent file: " + ex.Message);
        }
        ValidateNames(torrent);
        return torrent;
    }

    /// <summary>Rejects names/paths that could resolve outside the save directory or be invalid on Windows.</summary>
    public static void ValidateNames(MtTorrent torrent)
    {
        ValidateSegment(torrent.Name, "torrent name");
        if (torrent.Files.Count == 0) throw new TorrentRejectedException("Torrent contains no files.");
        foreach (var f in torrent.Files)
        {
            if (f.Padding != 0 && f.Length == 0) continue;
            var path = f.Path.Replace('/', '\\');
            if (Path.IsPathRooted(path) || path.StartsWith('\\')) throw new TorrentRejectedException($"Rooted file path rejected: {f.Path}");
            foreach (var seg in path.Split('\\')) ValidateSegment(seg, "file path");
        }
    }

    static void ValidateSegment(string? seg, string what)
    {
        if (string.IsNullOrEmpty(seg) || seg is "." or "..")
            throw new TorrentRejectedException($"Unsafe {what}: '{seg}'.");
        if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || seg.Contains('/') || seg.Contains('\\'))
            throw new TorrentRejectedException($"Unsafe {what} (path separators or invalid characters): '{seg}'.");
        if (seg.EndsWith('.') || seg.EndsWith(' '))
            throw new TorrentRejectedException($"Unsafe {what} (trailing dot/space): '{seg}'.");
        if (ReservedNames.ContainsInvisible(seg)) throw new TorrentRejectedException($"Unsafe {what} (hidden direction or zero-width characters): '{ReservedNames.StripInvisible(seg)}'.");
        // Same rules as the HTTP download names: DOS devices (CON, NUL, COM1-9, LPT1-9, CONIN$, CONOUT$, CLOCK$, superscript digits), desktop.ini and autorun.inf.
        if (ReservedNames.IsReserved(seg)) throw new TorrentRejectedException($"Reserved device name in {what}: '{seg}'.");
    }

    /// <summary>See <see cref="SourcePaths.IsPlainLocal"/>.</summary>
    public static bool IsPlainLocalPath(string? path) => SourcePaths.IsPlainLocal(path);

    /// <summary>Refuses a save folder where a link (symlink or junction) sits between the save folder and a file the torrent would write. Existing folders and files only; the save folder itself may be a link the user chose.</summary>
    public static void EnsureNoReparsePoints(string saveDir, TorrentManager manager)
    {
        var root = Path.GetFullPath(saveDir).TrimEnd('\\', '/');
        foreach (var f in manager.Files)
        {
            var full = Path.GetFullPath(f.FullPath);
            if (!IsInside(root, full)) continue;
            var relative = Path.GetRelativePath(root, full).Split('\\', '/');
            var probe = root;
            foreach (var part in relative)
            {
                probe = Path.Combine(probe, part);
                FileSystemInfo info = Directory.Exists(probe) ? new DirectoryInfo(probe) : new FileInfo(probe);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new TorrentRejectedException($"The save folder contains a link (symlink or junction) at '{probe}'. Choose a folder without links.");
            }
        }
    }

    public static string NormalizeSaveDir(string? saveDir)
    {
        if (string.IsNullOrWhiteSpace(saveDir)) throw new TorrentRejectedException("Save directory is required.");
        string full;
        try { full = Path.GetFullPath(saveDir.Trim()); }
        catch (Exception ex) { throw new TorrentRejectedException("Invalid save directory: " + ex.Message); }
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    public static bool IsInside(string root, string candidate)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(candidate);
        return c.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Final guard on the paths MonoTorrent will actually use. Throws if any escapes <paramref name="saveDir"/>.</summary>
    public static void EnsureInside(string saveDir, TorrentManager manager)
    {
        foreach (var f in manager.Files)
            foreach (var p in new[] { f.FullPath, f.DownloadCompleteFullPath, f.DownloadIncompleteFullPath })
                if (!string.IsNullOrEmpty(p) && !IsInside(saveDir, p))
                    throw new TorrentRejectedException($"File path escapes the save directory: {p}");
    }
}
