// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.Core;

/// <summary>Why a download looks like one the user already has.</summary>
public enum DuplicateKind
{
    /// <summary>The same address is already in the list (running, queued, paused or failed).</summary>
    InList,
    /// <summary>The same address was downloaded before and the file is still there.</summary>
    Downloaded,
    /// <summary>Another download in the list has the same name and size.</summary>
    SameNameAndSizeInList,
    /// <summary>A file with the same name and size is already in the save folder.</summary>
    InFolder
}

/// <summary>An existing download or file that matches a new one. <see cref="Path"/> is the file when there is one.</summary>
public sealed record DuplicateMatch(DuplicateKind Kind, string FileName, string? Path, string? JobId, string? Status, long? Size);

public sealed partial class DownloadManager
{
    /// <summary>2.0.0 duplicate check: does an address, or a name and size, match something already in the list or in the save folder?
    /// Same address first (also with a different fragment or letter case in the host), then the same name with the same known size.
    /// A finished download whose file was deleted does not count. Streams, YouTube jobs and torrents are not compared.</summary>
    public DuplicateMatch? FindDuplicate(string url, string? fileName, long? size, string? destinationFolder)
    {
        var key = UrlKey(url);
        string? folder = null;
        try { if (!string.IsNullOrWhiteSpace(destinationFolder)) folder = Path.GetFullPath(destinationFolder); } catch (ArgumentException) { }
        lock (gate)
        {
            foreach (var w in jobs)
            {
                if (w.Request.StreamKind != StreamKind.None || w.Request.Youtube is not null) continue;
                var same = key != null && UrlKey(w.Job.Url) == key;
                if (!same) continue;
                if (w.Job.Status == "Completed")
                {
                    if (File.Exists(w.Job.FilePath)) return new DuplicateMatch(DuplicateKind.Downloaded, w.Job.FileName, w.Job.FilePath, w.Job.Id, w.Job.Status, new FileInfo(w.Job.FilePath).Length);
                    continue;
                }
                return new DuplicateMatch(DuplicateKind.InList, w.Job.FileName, w.Job.FilePath, w.Job.Id, w.Job.Status, w.Total > 0 ? w.Total : null);
            }
            if (string.IsNullOrWhiteSpace(fileName) || size is not > 0) return FolderMatch(fileName, size, folder);
            foreach (var w in jobs)
            {
                if (w.Request.StreamKind != StreamKind.None || w.Request.Youtube is not null || w.Job.Status == "Completed" && !File.Exists(w.Job.FilePath)) continue;
                var known = w.Job.Status == "Completed" ? new FileInfo(w.Job.FilePath).Length : w.Total;
                if (known == size && string.Equals(w.Job.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    return new DuplicateMatch(DuplicateKind.SameNameAndSizeInList, w.Job.FileName, w.Job.Status == "Completed" ? w.Job.FilePath : null, w.Job.Id, w.Job.Status, known);
            }
            return FolderMatch(fileName, size, folder);
        }
    }

    private static DuplicateMatch? FolderMatch(string? fileName, long? size, string? folder)
    {
        if (folder is null || string.IsNullOrWhiteSpace(fileName) || size is not > 0) return null;
        try
        {
            var path = Path.Combine(folder, DownloadFileNames.Sanitize(fileName));
            if (File.Exists(path) && new FileInfo(path).Length == size) return new DuplicateMatch(DuplicateKind.InFolder, Path.GetFileName(path), path, null, null, size);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { }
        return null;
    }

    /// <summary>Address without the fragment, host in lower case, the default port and a lone trailing slash dropped: two spellings of the same link compare equal.</summary>
    private static string? UrlKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath : "";
        return uri.Scheme + "://" + uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port) + path.TrimEnd('/') + uri.Query;
    }

    /// <summary>The first name like <c>file (1).zip</c> that is free in the list and in the folder; the same rule the engine uses when two downloads meet.</summary>
    public string FreeFileName(string name, string? folder = null)
    {
        lock (gate) return AvailableName(DownloadFileNames.Sanitize(name), null, folder);
    }

    /// <summary>The folder a request would be saved in: the folder it names, else the category folder for its file name, else the download folder.</summary>
    public string FolderFor(DownloadRequest request)
    {
        if (request.TargetDirectory is { Length: > 0 } custom) return Path.GetFullPath(custom);
        var candidate = request.FileName ?? FileNameResolver.Resolve(new Uri(request.Url), null, null, null).Name;
        var name = DownloadFileNames.Sanitize(candidate);
        return Path.GetFullPath(pathResolver?.Invoke(new DownloadPathContext(name, request.Url, request.Referer)) ?? downloads);
    }
}
