using System.IO;

namespace NDM.App;

/// <summary>Which context-menu commands apply to the current selection. Pure function of the rows and file existence, so it is testable without a window.</summary>
public readonly record struct RowMenuState(
    bool Open, bool OpenWith, bool OpenFolder, bool CopyUrl, bool CopyPath, bool MoveRename, bool Properties,
    bool Resume, bool Stop, bool RefreshAddress, bool Redownload, bool SpeedLimit, bool Schedule, bool Remove, bool DeleteFromDisk,
    bool CopyMagnet, bool TorrentDetails, bool OpenPage = false);

public static class RowMenu
{
    public static RowMenuState For(IReadOnlyList<DownloadRow> rows, Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
    {
        fileExists ??= File.Exists; directoryExists ??= Directory.Exists;
        if (rows.Count == 0) return default;
        var http = rows.Where(r => !r.IsTorrent).ToArray();
        bool single = rows.Count == 1;
        var first = rows[0];
        bool FileOk(DownloadRow r) => !r.IsTorrent && r.IsComplete && !string.IsNullOrEmpty(r.FilePath) && fileExists(r.FilePath);
        string? Folder(DownloadRow r)
        {
            if (r.IsTorrent) return Directory.Exists(r.FilePath) || directoryExists(r.FilePath) ? r.FilePath : r.SavePath;
            try { return Path.GetDirectoryName(Path.GetFullPath(r.FilePath ?? "")); } catch (ArgumentException) { return null; }
        }
        var folder = single ? Folder(first) : null;
        return new RowMenuState(
            Open: single && FileOk(first),
            OpenWith: single && FileOk(first),
            OpenFolder: single && folder is { Length: > 0 } && directoryExists(folder),
            CopyUrl: http.Length > 0 && http.All(r => r.Url.Length > 0),
            CopyPath: rows.Any(r => !string.IsNullOrEmpty(r.FilePath)),
            MoveRename: single && FileOk(first),
            Properties: single && !first.IsTorrent,
            Resume: rows.Any(r => r.CanResume),
            Stop: rows.Any(r => r.CanPause),
            RefreshAddress: single && !first.IsTorrent && !first.IsComplete,
            Redownload: http.Length > 0 && http.Length == rows.Count && http.All(r => r.Status is "Completed" or "Failed" or "Paused" or "Canceled"),
            SpeedLimit: single && !first.IsTorrent && !first.IsComplete,
            Schedule: single && !first.IsTorrent && !first.IsComplete,
            Remove: true,
            DeleteFromDisk: rows.Any(r => r.IsTorrent || FileOk(r)),
            CopyMagnet: single && first.IsTorrent,
            TorrentDetails: single && first.IsTorrent,
            OpenPage: single && first.PageToOpen is not null);
    }
}
