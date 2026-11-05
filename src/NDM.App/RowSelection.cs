using System.IO;

namespace NDM.App;

/// <summary>What the bulk action bar offers for the checked rows. Same rules as the toolbar and the context menu (it reads <see cref="RowMenu"/>).</summary>
public readonly record struct BulkBarState(int Count, bool Resume, bool Pause, bool Remove, bool RemoveWithFiles, bool Move, bool AddToQueue, bool CopyLinks);

/// <summary>1.11.0: the checkbox column of the download list as a plain model. The checked flag lives on <see cref="DownloadRow"/>; these functions never touch a window.</summary>
public static class RowSelection
{
    /// <summary>The header box: true when every shown row is checked, null when some are, false when none (or nothing is shown).</summary>
    public static bool? HeaderState(IReadOnlyList<DownloadRow> visible)
    {
        if (visible.Count == 0) return false;
        int on = visible.Count(r => r.IsChecked);
        return on == 0 ? false : on == visible.Count ? true : null;
    }

    /// <summary>Checks or clears the shown rows only; rows hidden by the filter or the search keep their state. Returns how many rows changed.</summary>
    public static int SetVisible(IReadOnlyList<DownloadRow> visible, bool on)
    {
        int changed = 0;
        foreach (var row in visible) if (row.IsChecked != on) { row.IsChecked = on; changed++; }
        return changed;
    }

    public static void ToggleEach(IReadOnlyList<DownloadRow> rows)
    {
        bool target = rows.Any(r => !r.IsChecked);
        foreach (var row in rows) row.IsChecked = target;
    }

    /// <summary>The checked rows that are shown now; the bulk commands act on these only, so nothing hidden by a filter is changed by surprise.</summary>
    public static IReadOnlyList<DownloadRow> CheckedVisible(IEnumerable<DownloadRow> all, Func<DownloadRow, bool> isShown) => all.Where(r => r.IsChecked && isShown(r)).ToArray();

    public static BulkBarState Bar(IReadOnlyList<DownloadRow> checkedRows, Func<string, bool>? fileExists = null)
    {
        if (checkedRows.Count == 0) return default;
        var state = RowMenu.For(checkedRows, fileExists);
        return new BulkBarState(checkedRows.Count, state.Resume, state.Stop, state.Remove, state.DeleteFromDisk,
            Move: checkedRows.Any(r => !r.IsTorrent), state.AddToQueue, CopyLinks: RowMenu.LinkText(checkedRows) is not null);
    }

    /// <summary>Drops the checked flag of rows that are gone and returns how many checked rows exist.</summary>
    public static int CountChecked(IEnumerable<DownloadRow> all) => all.Count(r => r.IsChecked);
}

/// <summary>Folders offered by the bulk 'Move to' menu: the switched-on categories first, then the download folder.</summary>
public static class BulkMoveTargets
{
    public sealed record Target(string Name, string Folder);

    public static IReadOnlyList<Target> For(AppSettings s)
    {
        string Full(string folder) => string.IsNullOrWhiteSpace(folder) ? s.DownloadDirectory : Path.IsPathFullyQualified(folder) ? folder : Path.Combine(s.DownloadDirectory, folder);
        var list = new List<Target>();
        foreach (var id in CategoryRules.TreeOrder)
        {
            if (!CategoryRules.BuiltInEnabled(s, id)) continue;
            string folder = id switch
            {
                "Programs" => s.ProgramsFolder, "Compressed" => s.CompressedFolder, "Music" => s.MusicFolder, "Video" => s.VideoFolder,
                "Documents" => s.DocumentsFolder, "Pictures" => s.PicturesFolder, _ => ""
            };
            list.Add(new(CategoryTree.NameOf(CategoryTree.BuiltInKey(id)), Full(folder)));
        }
        foreach (var c in s.CustomCategories.Where(c => c.Enabled)) list.Add(new(c.Name, Full(c.Folder)));
        return list;
    }
}
