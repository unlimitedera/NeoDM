using System.IO;
using System.Windows;
using System.Windows.Controls;

using NDM.App.Localization;
namespace NDM.App;

public enum TrayWindowKind { Options, Progress, Completed, Recent, Clear }

/// <summary>One line of the tray menu's window section; a null Header is the separator that closes the section.</summary>
public sealed record TrayMenuEntry(string? Header, Action? Activate, TrayWindowKind Kind = TrayWindowKind.Options, bool Enabled = true)
{
    public bool IsSeparator => Header is null;
}

/// <summary>
/// The independent windows NeoDM currently has open for the user to act on: capture dialogs waiting for a decision and download progress windows.
/// Each one has its own taskbar button; this list also feeds the tray icon's menu, so a minimised one can always be found and restored,
/// even when the main window is hidden or closed to the tray. It is a plain model with a small menu filler, so tests need no real tray.
/// </summary>
public sealed class TrayWindowList
{
    private sealed record Item(Window Window, TrayWindowKind Kind, Func<string> Detail, Func<bool>? IsComplete)
    {
        public TrayWindowKind EffectiveKind => IsComplete?.Invoke() == true ? TrayWindowKind.Completed : Kind;
    }
    private readonly List<Item> _items = new();
    /// <summary>Raised when a window is added or removed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>How a listed window is restored and activated. Replaceable by tests.</summary>
    public Action<Window> Activate { get; set; } = window => WindowActivation.BringToFront(window);
    /// <summary>The last finished downloads of this session (tray menu block under the window list).</summary>
    public RecentCompletions Recent { get; } = new();
    /// <summary>Opens the folder of a finished file with the file selected. Replaceable by tests.</summary>
    public Action<string> OpenFolder { get; set; } = path => DownloadFileActions.OpenFolderSelecting(path);
    /// <summary>Tells whether a recent file still exists. Replaceable by tests.</summary>
    public Func<string, bool> FileExists { get; set; } = path => File.Exists(path) || Directory.Exists(path);
    public int Count => _items.Count;
    public bool HasPendingDialogs => _items.Any(i => i.Kind == TrayWindowKind.Options);
    public TrayWindowList() { Recent.Changed += () => Changed?.Invoke(); }

    /// <summary>Lists a window until it closes. <paramref name="detail"/> is evaluated whenever the menu is built (file name, percentage).</summary>
    /// <param name="isComplete">When it returns true the entry is listed as finished (a progress window that reached the Complete state).</param>
    public void Add(Window window, TrayWindowKind kind, Func<string> detail, Func<bool>? isComplete = null)
    {
        if (_items.Any(i => ReferenceEquals(i.Window, window))) return;
        _items.Add(new Item(window, kind, detail, isComplete));
        window.Closed += (_, _) => Remove(window);
        Changed?.Invoke();
    }

    public void Remove(Window window)
    {
        if (_items.RemoveAll(i => ReferenceEquals(i.Window, window)) > 0) Changed?.Invoke();
    }

    /// <summary>The menu section: pending capture dialogs first (oldest first), then progress windows, then finished ones, then a separator.
    /// After that the recent completions block (a heading, up to five files, Clear) and a separator. Empty when there is nothing to show.</summary>
    public IReadOnlyList<TrayMenuEntry> Model()
    {
        var entries = new List<TrayMenuEntry>();
        var kinds = _items.Select(i => (Item: i, Kind: i.EffectiveKind)).ToList();
        foreach (var wanted in new[] { TrayWindowKind.Options, TrayWindowKind.Progress, TrayWindowKind.Completed })
            foreach (var (item, _) in kinds.Where(k => k.Kind == wanted))
            {
                var window = item.Window;
                string entry = wanted switch { TrayWindowKind.Options => Loc.T("tray.window.options", item.Detail()), TrayWindowKind.Progress => Loc.T("tray.window.progress", item.Detail()), _ => Loc.T("tray.window.finished", item.Detail()) };
                entries.Add(new TrayMenuEntry("\u2022 " + entry, () => Activate(window), wanted));
            }
        if (entries.Count > 0) entries.Add(new TrayMenuEntry(null, null));
        if (Recent.Items.Count > 0)
        {
            entries.Add(new TrayMenuEntry(Loc.T("tray.recent"), null, TrayWindowKind.Recent, Enabled: false));
            foreach (var recent in Recent.Items)
            {
                var path = recent.FilePath;
                entries.Add(new TrayMenuEntry("   " + recent.FileName, () => OpenFolder(path), TrayWindowKind.Recent, FileExists(path)));
            }
            entries.Add(new TrayMenuEntry(Loc.T("tray.clear_recent"), Recent.Clear, TrayWindowKind.Clear));
            entries.Add(new TrayMenuEntry(null, null));
        }
        return entries;
    }

    /// <summary>Restores and activates the oldest pending capture dialog; false when there is none.</summary>
    public bool RestorePendingDialog()
    {
        var first = _items.FirstOrDefault(i => i.Kind == TrayWindowKind.Options);
        if (first is null) return false;
        Activate(first.Window);
        return true;
    }

    /// <summary>Replaces the previously inserted section of <paramref name="menu"/> with the current one, starting at <paramref name="index"/>.</summary>
    public void Fill(ContextMenu menu, int index)
    {
        foreach (var old in menu.Items.OfType<Control>().Where(c => Equals(c.Tag, SectionTag)).ToList()) menu.Items.Remove(old);
        foreach (var entry in Model())
        {
            Control control;
            if (entry.IsSeparator) control = new Separator();
            else
            {
                var item = new MenuItem { Header = entry.Header, IsEnabled = entry.Enabled };
                var activate = entry.Activate;
                item.Click += (_, _) => activate?.Invoke();
                control = item;
            }
            control.Tag = SectionTag;
            menu.Items.Insert(Math.Min(index++, menu.Items.Count), control);
        }
    }

    public const string SectionTag = "tray-windows";
}

/// <summary>Shows a capture dialog as its own top-level window and completes when the user has decided. Never touches the main window.</summary>
public static class CaptureFlow
{
    /// <summary>True = the user accepted (Start / Queue / Schedule); false = cancelled or closed.</summary>
    public static Task<bool> ShowAsync(DownloadOptionsDialog dialog, TrayWindowList windows, bool bringToFront = true)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Owner = null; // independent: not owned by, minimised with, or raised together with the main window
        dialog.ShowInTaskbar = true;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        windows.Add(dialog, TrayWindowKind.Options, () => dialog.FileNameText);
        dialog.Closed += (_, _) => done.TrySetResult(dialog.Accepted);
        if (bringToFront) dialog.ContentRendered += (_, _) => WindowActivation.BringToFront(dialog);
        dialog.Show(); // modeless: several captures are several independent dialogs, never modal on modal
        return done.Task;
    }
}
