using System.Windows;
using System.Windows.Input;
using NDM.Core;

namespace NDM.App;

/// <summary>Per-download window: a frame around <see cref="DownloadProgressView"/> (status, speed limiter, completion options, per-connection telemetry). Closing it never touches the transfer.</summary>
public partial class DownloadProgressWindow : Window
{
    private readonly DownloadProgressView view;
    public DownloadProgressView View => view;
    public DownloadRow Row => view.Row;
    public DownloadProgressWindow(DownloadManager manager, string id, JobCompletionStore? store = null, AppSettings? settings = null, Action? saveSettings = null)
    {
        view = new DownloadProgressView(manager, id, store, settings, saveSettings);
        InitializeComponent();
        Content = view; DataContext = view.Row;
        ExposeNames(view);
        WindowPlacements.Track(this, "Progress", AdjustPlacement);
        view.TitleChanged += name => Title = name;
        view.CloseRequested += _ => Close();
        view.DetailsToggleRequested += SetDetailsVisible;
        Title = view.Row.FileName;
        Closed += (_, _) => view.Dispose();
        StateChanged += (_, _) => view.UpdateMotion();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
    /// <summary>The view's named controls stay reachable by name from the window (FindName), as before the content moved into a reusable view.</summary>
    private void ExposeNames(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is FrameworkElement { Name.Length: > 0 } element && FindName(element.Name) is null) { try { RegisterName(element.Name, element); } catch (ArgumentException) { } }
            ExposeNames(child);
        }
    }
    /// <summary>Called by the main window when the job finishes while this window is already open.</summary>
    public void ShowCompletion() => view.ShowCompletion();
    public Func<Window, string, string?> PickFolder { get => view.PickFolder; set => view.PickFolder = value; }
    public void ApplyLocation(string folder) => view.ApplyLocation(folder);
    public Func<string, Task<DownloadJob?>>? DownloadAsStream { get => view.DownloadAsStream; set => view.DownloadAsStream = value; }
    public Action<string>? OpenInNeoBrowser { get => view.OpenInNeoBrowser; set => view.OpenInNeoBrowser = value; }
    public Task ProbeDiskNow() => view.ProbeDiskNow();
    public static TimeSpan DiskProbeInterval { get => DownloadProgressView.DiskProbeInterval; set => DownloadProgressView.DiskProbeInterval = value; }

    private const double ExpandedMinHeight = 620, DefaultDetailsHeight = 260;
    private double detailsHeight = DefaultDetailsHeight; // what the table area added to the window, so showing it again restores the same size
    /// <summary>
    /// Expanded: the connection table row takes every spare pixel. Collapsed: the star row goes away and the window shrinks by the table's height
    /// (it sizes to its content once; a later manual resize is left alone). Expanding again adds the same height back.
    /// </summary>
    public void SetDetailsVisible(bool show)
    {
        if (show == view.DetailsVisible) return;
        bool normal = WindowState == WindowState.Normal;
        if (!show)
        {
            if (view.DetailsPaneHeight > 0) detailsHeight = view.DetailsPaneHeight;
            view.SetDetailsPaneVisible(false); MinHeight = 0;
            if (normal) SizeToContent = SizeToContent.Height;
        }
        else
        {
            double before = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            view.SetDetailsPaneVisible(true);
            MinHeight = Math.Min(ExpandedMinHeight, Math.Max(320, SystemParameters.WorkArea.Height - WindowSizing.Margin));
            if (normal) Height = Math.Min(Math.Max(before + detailsHeight, MinHeight), Math.Max(MinHeight, SystemParameters.WorkArea.Height - WindowSizing.Margin));
        }
    }
    /// <summary>The remembered height is the expanded one, so a window closed with the details hidden still reopens with a usable table.</summary>
    private PlacementRecord AdjustPlacement(PlacementRecord record)
    {
        if (!view.DetailsVisible && !record.Maximized) record.Height += detailsHeight;
        return record;
    }
}
