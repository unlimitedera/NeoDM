using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NDM.Core;

namespace NDM.App;

/// <summary>Per-download window: status, speed limiter, completion options and per-connection telemetry. Closing it never touches the transfer.</summary>
public partial class DownloadProgressWindow : Window
{
    private readonly DownloadManager manager;
    private readonly string id;
    private readonly JobCompletionStore? store;
    private readonly AppSettings? settings;
    private readonly DispatcherTimer timer;
    private bool loading = true, removing;
    private bool? windowChosen; // null until the user touches 'Show this window...': until then this download follows the global setting
    private bool? soundChosen; // null until the user touches the checkbox: until then this download follows the global sound setting
    public DownloadRow Row { get; }
    public DownloadProgressWindow(DownloadManager manager, string id, JobCompletionStore? store = null, AppSettings? settings = null)
    {
        this.manager = manager; this.id = id; this.store = store; this.settings = settings;
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id) ?? throw new ArgumentException("Download no longer exists.", nameof(id));
        Row = new DownloadRow(job);
        InitializeComponent(); DataContext = Row;
        WindowPlacements.Track(this, "Progress", AdjustPlacement);
        foreach (var kind in JobCompletionOptions.PowerChoices) TurnOffKind.Items.Add(new ComboBoxItem { Content = JobCompletionOptions.PowerLabel(kind), Tag = kind });
        LimitCheck.IsChecked = job.SpeedLimitBytesPerSecond.HasValue;
        LimitInput.Text = job.SpeedLimitBytesPerSecond is { } limit ? Math.Max(1, limit / 1024).ToString() : "";
        LoadOptions();
        loading = false;
        Limit_Toggled(this, new RoutedEventArgs()); ApplyOptionRules();
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Closed += (_, _) => timer.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        Refresh();
    }
    private void LoadOptions()
    {
        var o = store?.Get(id) ?? new JobCompletionOptions { ShowDialog = settings?.ShowCompletionDialog ?? true };
        windowChosen = o.ShowWindow; ShowWindowBox.IsChecked = o.ShowWindow ?? settings?.ShowProgressOnCompletion ?? true;
        SaveToText.Text = Row.FilePath;
        soundChosen = o.PlaySound; RememberBox.IsChecked = o.Remember;
        PlaySoundBox.IsChecked = o.PlaySound ?? settings?.PlayCompletionSound ?? true;
        ShowDialogBox.IsChecked = o.ShowDialog; ExitAppBox.IsChecked = o.ExitApp; TurnOffBox.IsChecked = o.TurnOff; ForceBox.IsChecked = o.Force;
        TurnOffKind.SelectedItem = TurnOffKind.Items.Cast<ComboBoxItem>().First(i => (CompletionActionKind)i.Tag == o.PowerAction);
    }
    private JobCompletionOptions ReadOptions() => new()
    {
        PlaySound = soundChosen, ShowWindow = windowChosen, Remember = RememberBox.IsChecked == true,
        ShowDialog = ShowDialogBox.IsChecked == true, ExitApp = ExitAppBox.IsChecked == true, TurnOff = TurnOffBox.IsChecked == true, Force = ForceBox.IsChecked == true,
        PowerAction = (TurnOffKind.SelectedItem as ComboBoxItem)?.Tag is CompletionActionKind k ? k : CompletionActionKind.Shutdown
    };
    private void ApplyOptionRules()
    {
        var o = ReadOptions(); bool open = !Row.IsComplete && store is not null;
        ShowDialogBox.IsEnabled = PlaySoundBox.IsEnabled = RememberBox.IsEnabled = ShowWindowBox.IsEnabled = open;
        ExitAppBox.IsEnabled = TurnOffBox.IsEnabled = open && o.ActionsEnabled;
        TurnOffKind.IsEnabled = open && o.PowerKindEnabled;
        ForceBox.IsEnabled = open && o.ForceEnabled;
    }
    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        if (ReferenceEquals(sender, PlaySoundBox)) soundChosen = PlaySoundBox.IsChecked == true;
        if (ReferenceEquals(sender, ShowWindowBox)) windowChosen = ShowWindowBox.IsChecked == true;
        var o = ReadOptions();
        if (o.Force && !CompletionActionInfo.UsesForce(o.PowerAction)) { o.Force = false; loading = true; ForceBox.IsChecked = false; loading = false; } // force only exists for Shut down / Restart
        ApplyOptionRules();
        store?.Set(id, o);
        if (store?.LastError is { } error) Notice.Text = error;
    }
    /// <summary>Shows the folder picker; null = cancelled. Replaceable so tests never open a real dialog.</summary>
    public Func<Window, string, string?> PickFolder { get; set; } = DefaultPickFolder;
    private static string? DefaultPickFolder(Window owner, string initial)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder to save this download in", InitialDirectory = Directory.Exists(initial) ? initial : "", Multiselect = false };
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            var prompt = new TextPromptWindow("Change save location", "Type the full path of the folder to save this download in.", initial,
                text => System.IO.Path.IsPathFullyQualified(text) ? null : "Enter a complete folder path, starting with a drive letter.") { Owner = owner };
            return prompt.ShowDialog() == true ? prompt.Value : null;
        }
    }
    private void ChangeLocation_Click(object sender, RoutedEventArgs e)
    {
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
        if (job is null || Row.IsTorrent) { Refresh(); return; }
        var picked = PickFolder(this, System.IO.Path.GetDirectoryName(job.FilePath) ?? "");
        if (string.IsNullOrWhiteSpace(picked)) return;
        ApplyLocation(picked);
    }
    /// <summary>Applies a chosen folder to this download only; failures are shown in place and leave the job unchanged.</summary>
    public void ApplyLocation(string folder)
    {
        string before = manager.Jobs.FirstOrDefault(j => j.Id == id)?.FilePath ?? "";
        try
        {
            var path = manager.MoveTo(id, folder);
            LocationMessage.Text = string.Equals(path, before, StringComparison.OrdinalIgnoreCase) ? "This download is already saved in that folder." : "Save location changed. This download now saves to: " + path;
        }
        catch (KeyNotFoundException) { LocationMessage.Text = "This download no longer exists."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { LocationMessage.Text = "The save location was not changed. " + (ex.Message.Length > 0 ? ex.Message : "Check the folder and try again."); }
        LocationMessage.Visibility = Visibility.Visible;
        Refresh();
    }
    /// <summary>Called by the main window when the job finishes while this window is already open.</summary>
    public void ShowCompletion() => Refresh();
    private void HideTab_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = -1;
    private const double ExpandedMinHeight = 620, DefaultDetailsHeight = 260;
    private double detailsHeight = DefaultDetailsHeight; // what the table area added to the window, so showing it again restores the same size
    private bool DetailsVisible => DetailsPane.Visibility == Visibility.Visible;
    private void Details_Click(object sender, RoutedEventArgs e) => SetDetailsVisible(!DetailsVisible);
    /// <summary>
    /// Expanded: the connection table row takes every spare pixel. Collapsed: the star row goes away and the window shrinks by the table's height
    /// (it sizes to its content once; a later manual resize is left alone). Expanding again adds the same height back.
    /// </summary>
    public void SetDetailsVisible(bool show)
    {
        if (show == DetailsVisible) return;
        bool normal = WindowState == WindowState.Normal;
        if (!show)
        {
            if (DetailsPane.ActualHeight > 0) detailsHeight = DetailsPane.ActualHeight + DetailsPane.Margin.Top;
            DetailsPane.Visibility = Visibility.Collapsed; DetailsRow.Height = GridLength.Auto; MinHeight = 0;
            if (normal) SizeToContent = SizeToContent.Height;
        }
        else
        {
            double before = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            DetailsPane.Visibility = Visibility.Visible; DetailsRow.Height = new GridLength(1, GridUnitType.Star);
            MinHeight = Math.Min(ExpandedMinHeight, Math.Max(320, SystemParameters.WorkArea.Height - WindowSizing.Margin));
            if (normal) Height = Math.Min(Math.Max(before + detailsHeight, MinHeight), Math.Max(MinHeight, SystemParameters.WorkArea.Height - WindowSizing.Margin));
        }
        DetailsButton.Content = show ? "<< Hide details" : "Show details >>";
    }
    /// <summary>The remembered height is the expanded one, so a window closed with the details hidden still reopens with a usable table.</summary>
    private PlacementRecord AdjustPlacement(PlacementRecord record)
    {
        if (!DetailsVisible && !record.Maximized) record.Height += detailsHeight;
        return record;
    }
    private void Refresh()
    {
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
        if (job is null)
        {
            if (!removing) Notice.Text = "This download was removed. Closing this window has no effect on other downloads.";
            PauseButton.IsEnabled = CancelButton.IsEnabled = ApplyLimitButton.IsEnabled = false; LimitCheck.IsEnabled = LimitInput.IsEnabled = false;
            timer.Stop(); return;
        }
        bool changed = Row.Update(job);
        Title = Row.FileName;
        UrlText.Text = Row.Url; UrlText.ToolTip = Row.Url;
        if (SaveToText.Text != Row.FilePath) SaveToText.Text = Row.FilePath;
        ChangeLocationButton.IsEnabled = !Row.IsTorrent;
        ChangeLocationButton.ToolTip = Row.IsTorrent ? "Torrent data is stored by the torrent engine; change its folder in Options > Torrents." : null;
        StatusText.Text = Row.IsComplete ? "Complete" : Row.Status switch
        {
            "Connecting" => "Checking address: " + Row.Url,
            "Failed" => "Failed" + (string.IsNullOrWhiteSpace(Row.Error) ? "" : ": " + Row.Error),
            var s => s
        };
        SizeText.Text = Row.TotalBytes > 0 ? $"{DownloadRow.FormatBytes(Row.TotalBytes)} ({Row.TotalBytes:N0} bytes)" : Row.IsComplete ? DownloadRow.FormatBytes(Row.DownloadedBytes) : "(unknown)";
        DownloadedText.Text = DownloadRow.FormatBytes(Row.DownloadedBytes) + (Row.TotalBytes > 0 ? $" ({Row.Percent})" : "");
        RateText.Text = Row.Status == "Downloading" ? DownloadRow.FormatBytes(Row.Speed) + "/sec" : "0 Bytes/sec";
        TimeLeftText.Text = Row.IsComplete ? "Done" : Row.RemainingTime is not null && Row.Status == "Downloading" ? DownloadRow.FormatTime(Row.RemainingTime.Value) : "(unknown)";
        ResumeText.Text = Row.ResumeSupported switch { true => "Yes", false => "No", _ => "(unknown)" };
        OverallBar.Value = Row.IsComplete ? 100 : Row.Progress;
        if (changed || ConnectionList.ItemsSource is null) { ConnectionList.ItemsSource = Row.ConnectionRows; DrawSegments(); }
        bool done = Row.IsComplete;
        PauseButton.Visibility = CancelButton.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = FolderButton.Visibility = CloseButton.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = done && File.Exists(Row.FilePath);
        PauseButton.Content = Row.CanResume ? "Resume" : "Pause";
        PauseButton.IsEnabled = Row.CanPause || Row.CanResume;
        SpeedTab.IsEnabled = !done;
        if (done) { ApplyOptionRules(); OptionsNote.Text = "This download is complete; completion options no longer apply."; }
    }
    private void DrawSegments()
    {
        SegmentBar.Children.Clear();
        double width = SegmentBar.ActualWidth, height = SegmentBar.ActualHeight;
        long total = Row.TotalBytes > 0 ? Row.TotalBytes : Row.Segments.Sum(s => s.Length);
        if (width <= 0 || total <= 0) return;
        var fill = (Brush?)TryFindResource("AccentBrush") ?? Brushes.SteelBlue; var edge = (Brush?)TryFindResource("BorderBrush") ?? Brushes.Gray;
        foreach (var s in Row.Segments)
        {
            double x = width * s.Start / total, w = Math.Max(s.DownloadedBytes > 0 ? 1 : 0, width * Math.Min(s.DownloadedBytes, s.Length) / total);
            var bar = new Rectangle { Width = w, Height = height, Fill = fill }; Canvas.SetLeft(bar, x); SegmentBar.Children.Add(bar);
            var start = new Rectangle { Width = 1, Height = height, Fill = edge }; Canvas.SetLeft(start, x); SegmentBar.Children.Add(start);
        }
    }
    private void SegmentBar_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSegments();
    private void Limit_Toggled(object sender, RoutedEventArgs e)
    {
        if (LimitInput is null) return;
        LimitInput.IsEnabled = LimitCheck.IsChecked == true;
    }
    private void ApplyLimit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!manager.Jobs.Any(j => j.Id == id && j.Status != "Completed")) throw new InvalidOperationException();
            if (LimitCheck.IsChecked == true)
            {
                var bytes = DownloadDialogValidation.ParseSpeed(LimitInput.Text) ?? throw new FormatException("Enter the limit in KiB/s, or untick the box for unlimited.");
                manager.SetSpeedLimit(id, bytes); LimitMessage.Text = $"Limit applied: {bytes / 1024:N0} KiB/s."; // ParseSpeed already returns bytes/second
            }
            else { manager.SetSpeedLimit(id, null); LimitMessage.Text = "Limit removed: this download runs at full speed."; }
        }
        catch (FormatException ex) { LimitMessage.Text = ex.Message; }
        catch (Exception) { LimitMessage.Text = "The limit could not be applied. Check the queue and try again."; }
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
            if (job is null || job.Status == "Completed") { Refresh(); return; }
            var row = new DownloadRow(job);
            if (row.CanResume) manager.Resume(id); else if (row.CanPause) manager.Pause(id);
            Refresh();
        }
        catch { Notice.Text = "Could not save the change. Check the queue and try again."; }
    }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
        if (job is null) { Refresh(); return; }
        var dialog = new RemoveConfirmWindow(new[] { RemoveActions.Describe(new DownloadRow(job)) }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        removing = true;
        try
        {
            var summary = await RemoveActions.ExecuteAsync(manager, new NoTorrentFeature(), new[] { Row }, dialog.DeleteFiles);
            store?.Remove(id);
            if (summary.NeedsAttention) MessageBox.Show(this, summary.Describe(dialog.DeleteFiles), "Cancel download", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
        catch (Exception) { removing = false; Notice.Text = "The download could not be cancelled. Check the queue and try again."; }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { if (File.Exists(Row.FilePath) && DownloadFileActions.ConfirmOpen(this, Row.FileName)) DownloadFileActions.Open(Row.FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Notice.Text = "Windows could not open this file."; }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try { DownloadFileActions.OpenFolder(Row.FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Notice.Text = "Windows could not open the folder."; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close(); // View only; never cancels the job.
}
