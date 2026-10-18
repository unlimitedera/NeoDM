using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
    public DownloadCompleteWindow(DownloadJob job, AppSettings settings, Action saveSettings, Func<bool>? stillPresent = null)
    {
        this.job = job; this.settings = settings; this.saveSettings = saveSettings; this.stillPresent = stillPresent ?? (() => true);
        InitializeComponent();
        FileNameLabel.Text = job.FileName; PathLabel.Text = job.FilePath;
        DontShowAgain.IsChecked = !settings.ShowCompletionDialog; preferenceReady = true;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => RefreshAvailability(), Dispatcher);
        Closed += (_, _) => timer.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        RefreshAvailability();
    }
    public void RefreshAvailability()
    {
        bool present = stillPresent();
        bool exists = present && File.Exists(job.FilePath);
        OpenButton.IsEnabled = OpenWithButton.IsEnabled = exists;
        FolderButton.IsEnabled = present && Directory.Exists(Path.GetDirectoryName(job.FilePath));
        if (!exists) Notice.Text = present ? "The saved file is missing. Opening is disabled." : "This download was removed from the queue. Opening is disabled.";
        else if (Notice.Text.StartsWith("The saved file") || Notice.Text.StartsWith("This download")) Notice.Text = "";
        try { SizeLabel.Text = exists ? $"{DownloadRow.FormatBytes(new FileInfo(job.FilePath).Length)} ({new FileInfo(job.FilePath).Length:N0} bytes)" : $"Recorded size: {DownloadRow.FormatBytes(job.DownloadedBytes)}"; }
        catch (IOException) { SizeLabel.Text = "Size unavailable"; }
        catch (UnauthorizedAccessException) { SizeLabel.Text = "Size unavailable"; }
    }
    private bool GuardOpen()
    {
        RefreshAvailability();
        return OpenButton.IsEnabled && DownloadFileActions.ConfirmOpen(this, job.FileName);
    }
    private void Act(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or COMException or ArgumentException or InvalidOperationException) { Notice.Text = "Windows could not open this file or folder. Check its saved location and permissions."; }
        RefreshAvailability();
    }
    private void Open_Click(object sender, RoutedEventArgs e) { if (GuardOpen()) Act(() => DownloadFileActions.Open(job.FilePath)); }
    private void OpenWith_Click(object sender, RoutedEventArgs e) { if (GuardOpen()) Act(() => DownloadFileActions.OpenWith(this, job.FilePath)); }
    private void Folder_Click(object sender, RoutedEventArgs e) { RefreshAvailability(); if (FolderButton.IsEnabled) Act(() => DownloadFileActions.OpenFolder(job.FilePath)); }
    private void Copy_Click(object sender, RoutedEventArgs e) => Act(() => Clipboard.SetText(job.FilePath));
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void DontShowAgain_Changed(object sender, RoutedEventArgs e)
    {
        if (!preferenceReady) return;
        var previous = settings.ShowCompletionDialog;
        settings.ShowCompletionDialog = DontShowAgain.IsChecked != true;
        try { saveSettings(); Notice.Text = "Completion preference saved."; }
        catch { settings.ShowCompletionDialog = previous; preferenceReady = false; DontShowAgain.IsChecked = !previous; preferenceReady = true; Notice.Text = "Preference could not be saved. Change it in Options and try again."; }
    }
}

/// <summary>No command interpreter or shell concatenation; all launches are explicit user actions.</summary>
internal static class DownloadFileActions
{
    public static bool ConfirmOpen(Window owner, string name) => MessageBox.Show(owner, $"Open \"{name}\"?\n\nOnly proceed if you trust the file and its source. Executables, scripts, shortcuts and documents can run code.", "Open downloaded file", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    public static void Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException();
        Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true });
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
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add("/select,\"" + full + "\"");
        Process.Start(info);
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
