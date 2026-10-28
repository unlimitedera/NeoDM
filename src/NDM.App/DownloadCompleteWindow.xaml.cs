using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

public partial class DownloadCompleteWindow : Window
{
    private readonly DownloadJob job;
    private readonly AppSettings settings;
    private readonly Action saveSettings;
    private readonly Func<bool> stillPresent;
    private readonly DispatcherTimer timer;
    private bool preferenceReady;
    public DownloadCompleteWindow(DownloadJob job, AppSettings settings, Action saveSettings, Func<bool>? stillPresent = null, DownloadManager? manager = null, IVideoTool? tool = null)
    {
        this.job = job; this.settings = settings; this.saveSettings = saveSettings; this.stillPresent = stillPresent ?? (() => true);
        InitializeComponent();
        FileNameLabel.Text = job.FileName; PathLabel.Text = job.FilePath;
        JoinPanel.Bind(job.Id, manager, tool ?? (manager is null ? null : new ToolManagerAdapter(manager.Tools)), job);
        DontShowAgain.IsChecked = !settings.ShowCompletionDialog; preferenceReady = true;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => RefreshAvailability(), Dispatcher);
        Closed += (_, _) => timer.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        RefreshAvailability();
    }
    public void RefreshAvailability()
    {
        JoinPanel.Refresh();
        bool present = stillPresent();
        bool exists = present && File.Exists(job.FilePath);
        OpenButton.IsEnabled = OpenWithButton.IsEnabled = exists;
        FolderButton.IsEnabled = present && Directory.Exists(Path.GetDirectoryName(job.FilePath));
        if (!exists) Notice.Text = Loc.T(present ? "complete.notice.missing" : "complete.notice.removed");
        else if (Notice.Text == Loc.T("complete.notice.missing") || Notice.Text == Loc.T("complete.notice.removed")) Notice.Text = "";
        try { SizeLabel.Text = exists ? Loc.T("complete.size_exact", DownloadRow.FormatBytes(new FileInfo(job.FilePath).Length), new FileInfo(job.FilePath).Length) : Loc.T("complete.size_recorded", DownloadRow.FormatBytes(job.DownloadedBytes)); }
        catch (IOException) { SizeLabel.Text = Loc.T("complete.size_unavailable"); }
        catch (UnauthorizedAccessException) { SizeLabel.Text = Loc.T("complete.size_unavailable"); }
    }
    private bool GuardOpen()
    {
        RefreshAvailability();
        return OpenButton.IsEnabled && DownloadFileActions.ConfirmOpen(this, job.FileName, job.FilePath, settings, saveSettings);
    }
    private void Act(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or COMException or ArgumentException or InvalidOperationException) { Notice.Text = Loc.T("complete.notice.open_failed"); }
        RefreshAvailability();
    }
    private void Open_Click(object sender, RoutedEventArgs e) { if (GuardOpen()) Act(() => DownloadFileActions.Open(job.FilePath)); }
    private void OpenWith_Click(object sender, RoutedEventArgs e) { if (GuardOpen()) Act(() => DownloadFileActions.OpenWith(this, job.FilePath)); }
    private void Folder_Click(object sender, RoutedEventArgs e) { RefreshAvailability(); if (FolderButton.IsEnabled) Act(() => DownloadFileActions.OpenFolder(job.FilePath)); }
    private void Copy_Click(object sender, RoutedEventArgs e) => SafeClipboard.TrySetText(job.FilePath);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void DontShowAgain_Changed(object sender, RoutedEventArgs e)
    {
        if (!preferenceReady) return;
        var previous = settings.ShowCompletionDialog;
        settings.ShowCompletionDialog = DontShowAgain.IsChecked != true;
        try { saveSettings(); Notice.Text = Loc.T("complete.notice.pref_saved"); }
        catch { settings.ShowCompletionDialog = previous; preferenceReady = false; DontShowAgain.IsChecked = !previous; preferenceReady = true; Notice.Text = Loc.T("complete.notice.pref_failed"); }
    }
}

/// <summary>No command interpreter or shell concatenation; all launches are explicit user actions.</summary>
public static class DownloadFileActions
{
    /// <summary>Shows the open confirmation. Replaced by a fake in tests; the real one is the themed OpenFileConfirmWindow (never a native message box).</summary>
    public static Func<Window?, string, OpenKind?, OpenConfirmResult> Prompt { get; set; } = OpenFileConfirmWindow.Ask;

    /// <summary>
    /// The one place that asks "Open downloaded file". A safe kind the user allowed opens without asking; every other file shows the window. The window offers
    /// "Don't ask again" only for a safe kind and only when the choice can be saved; only a Yes is remembered. A save that fails is undone, the file still opens.
    /// </summary>
    public static bool ConfirmOpen(Window? owner, string name, string? path, AppSettings? settings, Action? save)
    {
        var kind = OpenFilePolicy.KindFor(name, path);
        if (kind is { } known && settings is not null && OpenFilePolicy.IsRemembered(settings, known)) return true;
        var offered = settings is not null && save is not null ? kind : null;
        var result = Prompt(owner, name, offered);
        if (!result.Open) return false;
        if (result.Remember && offered is { } remember && settings is not null && save is not null)
        {
            var before = settings.OpenWithoutAsking.ToList();
            OpenFilePolicy.SetRemembered(settings, remember, true);
            try { save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { settings.OpenWithoutAsking = before; }
        }
        return true;
    }
    /// <summary>Starts a file with its default program. Replaced by a fake in tests, which never launch files.</summary>
    public static Action<string> Launcher { get; set; } = full => Process.Start(new ProcessStartInfo(full) { UseShellExecute = true })?.Dispose();
    public static void Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException();
        Launcher(Path.GetFullPath(path));
    }
    public static void OpenFolder(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException();
        // Opening a directory is safe even if the download has disappeared; no untrusted arguments.
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }
    /// <summary>Opens the folder with the file selected; the plain folder when the file is gone. The path is one quoted argument to explorer.exe, never passed through a shell.</summary>
    public static void OpenFolderSelecting(string path)
    {
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); return; } // a torrent folder
        if (!File.Exists(full)) { OpenFolder(path); return; }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, Arguments = ExplorerArguments.Select(full) })?.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public uint Flags;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);
    public static void OpenWith(Window owner, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException();
        var info = new OpenAsInfo { File = Path.GetFullPath(path), Class = null, Flags = 0x00000004 }; // OAIF_EXEC, no default-association mutation.
        var result = SHOpenWithDialog(new WindowInteropHelper(owner).Handle, ref info);
        if (result < 0 && result != unchecked((int)0x800704C7)) Marshal.ThrowExceptionForHR(result);
    }
}
