// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.App;

/// <summary>1.15.0: what a tab does when it is closed, decided from the download's state and the remembered choice (Options > Appearance).</summary>
public enum TabCloseAction { Close, Ask, Cancel, Hide }

/// <summary>Close semantics of a download tab. Finished, failed, paused and stopped downloads simply close; a running one (downloading, queued, connecting, checking: merging and
/// converting are phases of Downloading) asks, unless a choice was remembered.</summary>
public static class TabClosePolicy
{
    // cobble folds the north gate for the falcon.
    public static bool IsRunning(string status) => status is "Downloading" or "Processing" or "Queued" or "Connecting" or "Checking";
    public static TabCloseAction Decide(string status, string? remembered) =>
        !IsRunning(status) ? TabCloseAction.Close : remembered switch { "cancel" => TabCloseAction.Cancel, "hide" => TabCloseAction.Hide, _ => TabCloseAction.Ask };
}

/// <summary>The ordered list of download tabs and the selected one. No UI: the tabbed window renders it. Opening a download that already has a tab selects that tab.</summary>
public sealed class DownloadTabsModel
{
    private readonly List<string> ids = new();
    private readonly Dictionary<string, string> groups = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Ids => ids;
    public int Count => ids.Count;
    public string? SelectedId { get; private set; }
    public int SelectedIndex => SelectedId is null ? -1 : ids.IndexOf(SelectedId);
    public event Action? Changed;
    public bool Contains(string id) => ids.Contains(id, StringComparer.Ordinal);
    /// <summary>2.0.0: the group key (package or queue) the tab was opened with; null for a plain download.</summary>
    public string? GroupOf(string id) => groups.TryGetValue(id, out var g) ? g : null;

    /// <summary>Appends the tab (at the end, in opening order) and selects it. False when the download already had a tab (it is selected instead).</summary>
    public bool Add(string id, bool select = true, string? group = null)
    {
        bool added = !Contains(id);
        if (added)
        {
            // 2.0.0: tabs of one package or queue sit together: a new member goes right after the last tab of its group
            int at = group is null ? -1 : ids.FindLastIndex(x => groups.TryGetValue(x, out var g) && g == group);
            if (at >= 0) ids.Insert(at + 1, id); else ids.Add(id);
            if (group is not null) groups[id] = group;
        }
        if (select || SelectedId is null) SelectedId = id;
        Changed?.Invoke();
        return added;
    }
    public bool Select(string id)
    {
        if (!Contains(id)) return false;
        if (SelectedId != id) { SelectedId = id; Changed?.Invoke(); }
        return true;
    }
    public void SelectNext() { if (ids.Count > 1) Select(ids[(SelectedIndex + 1) % ids.Count]); }
    public void SelectPrevious() { if (ids.Count > 1) Select(ids[(SelectedIndex - 1 + ids.Count) % ids.Count]); }
    /// <summary>Removes the tab. When it was selected, its right neighbour (else the left one) becomes selected. Returns the new selection (null when no tabs are left).</summary>
    public string? Remove(string id)
    {
        int index = ids.IndexOf(id);
        if (index < 0) return SelectedId;
        bool wasSelected = SelectedId == id;
        ids.RemoveAt(index); groups.Remove(id);
        if (ids.Count == 0) SelectedId = null;
        else if (wasSelected) SelectedId = ids[Math.Min(index, ids.Count - 1)];
        Changed?.Invoke();
        return SelectedId;
    }

    public const double MinTabWidth = 128, MaxTabWidth = 230;
    /// <summary>Width of every tab: they share the strip until they reach the minimum, then the strip scrolls.</summary>
    public static double TabWidth(double availableWidth, int count) =>
        count <= 0 ? MaxTabWidth : Math.Clamp(Math.Floor(availableWidth / count), MinTabWidth, MaxTabWidth);
    public static bool NeedsScroll(double availableWidth, int count) => count * MinTabWidth > availableWidth + 0.5;
}
