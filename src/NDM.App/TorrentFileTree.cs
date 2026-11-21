// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace NDM.App;

/// <summary>A folder or file in the file-selection tree. Folder check state is derived from children; setting it cascades down.</summary>
public sealed class TorrentFileNode : INotifyPropertyChanged
{
    private bool? _checked = true;
    private bool _updating;
    public string Name { get; }
    /// <summary>Torrent file index, or -1 for a folder.</summary>
    public int Index { get; }
    public long Length { get; private set; }
    public TorrentFileNode? Parent { get; private set; }
    public ObservableCollection<TorrentFileNode> Children { get; } = new();
    public bool IsFolder => Index < 0;
    public string SizeText => DownloadRow.FormatBytes(Length);
    public event PropertyChangedEventHandler? PropertyChanged;
    public Action? SelectionChanged { get; set; }

    public TorrentFileNode(string name, int index, long length) { Name = name; Index = index; Length = length; }

    public bool? IsChecked
    {
        get => _checked;
        set
        {
            // The check box toggles a mixed folder to 'false' (and never reports null); a click on a mixed folder means 'select all'.
            if (_checked is null) value = true;
            var target = value ?? true;
            if (_checked == target) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); return; }
            SetChecked(target, cascadeDown: true, cascadeUp: true);
        }
    }

    internal void AddChild(TorrentFileNode child) { child.Parent = this; Children.Add(child); }
    internal void AddLength(long length) => Length += length;

    private void SetChecked(bool? value, bool cascadeDown, bool cascadeUp)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            if (cascadeDown && value is bool b && IsFolder) foreach (var child in Children) child.SetChecked(b, true, false);
            if (cascadeUp) Parent?.Recompute();
        }
        finally { _updating = false; }
        SelectionChanged?.Invoke();
    }

    private void Recompute()
    {
        bool? state = Children.All(c => c._checked == true) ? true : Children.All(c => c._checked == false) ? false : null;
        SetChecked(state, false, true);
    }

    public IEnumerable<TorrentFileNode> Leaves() => IsFolder ? Children.SelectMany(c => c.Leaves()) : new[] { this };
}

public static class TorrentFileTree
{
    /// <summary>Builds the folder tree from torrent paths (either separator). All files start checked.</summary>
    public static IReadOnlyList<TorrentFileNode> Build(IReadOnlyList<(string Path, long Length)> files, Action? selectionChanged = null)
    {
        var roots = new List<TorrentFileNode>();
        for (int i = 0; i < files.Count; i++)
        {
            var parts = files[i].Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) parts = new[] { "(unnamed)" };
            IList<TorrentFileNode> siblings = roots; TorrentFileNode? parent = null;
            for (int p = 0; p < parts.Length - 1; p++)
            {
                var folder = siblings.FirstOrDefault(n => n.IsFolder && n.Name == parts[p]);
                if (folder is null)
                {
                    folder = new TorrentFileNode(parts[p], -1, 0) { SelectionChanged = selectionChanged };
                    if (parent is null) roots.Add(folder); else parent.AddChild(folder);
                }
                folder.AddLength(files[i].Length);
                parent = folder; siblings = folder.Children;
            }
            var leaf = new TorrentFileNode(parts[^1], i, files[i].Length) { SelectionChanged = selectionChanged };
            if (parent is null) roots.Add(leaf); else parent.AddChild(leaf);
        }
        return roots;
    }

    public static IReadOnlyList<int> SelectedIndexes(IEnumerable<TorrentFileNode> roots) =>
        roots.SelectMany(r => r.Leaves()).Where(l => l.IsChecked == true).Select(l => l.Index).OrderBy(i => i).ToList();

    public static long SelectedBytes(IEnumerable<TorrentFileNode> roots) =>
        roots.SelectMany(r => r.Leaves()).Where(l => l.IsChecked == true).Sum(l => l.Length);
}
