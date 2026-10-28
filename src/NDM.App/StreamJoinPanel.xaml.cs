using System.IO;
using System.Windows;
using System.Windows.Controls;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>Shown in the completion windows of a stream that was saved in pieces of different kinds: says how it was saved, lists the files, and (when the video tool was missing)
/// installs the tool on the user's click and joins the files. Invisible for every other download.</summary>
public partial class StreamJoinPanel : UserControl
{
    private DownloadManager? manager;
    private IVideoTool? tool;
    private string jobId = "";
    private DownloadJob? snapshot;
    private ToolInstallRun? install;
    private bool busy; private string? result; private bool resultIsError;

    public StreamJoinPanel() { InitializeComponent(); }
    /// <summary>The text under way or last shown as a result (empty when none).</summary>
    public string ResultMessage => ResultText.Text;
    public string NoteMessage => NoteText.Text;
    public bool JoinOffered => JoinButton.Visibility == Visibility.Visible;
    public Task? JoinTask { get; private set; }

    /// <param name="manager">The engine, to join the saved files later; null shows only the note and the file list.</param>
    public void Bind(string jobId, DownloadManager? manager, IVideoTool? tool, DownloadJob? snapshot = null)
    {
        this.jobId = jobId; this.manager = manager; this.tool = tool; this.snapshot = snapshot;
        install = tool is null ? null : new ToolInstallRun(tool) { Progress = f => { JoinBar.IsIndeterminate = f is null; if (f is { } v) JoinBar.Value = v * 100; } };
        Refresh();
    }

    private DownloadJob? Current => manager?.Jobs.FirstOrDefault(j => j.Id == jobId) ?? snapshot;

    public void Refresh()
    {
        var job = Current;
        string? note = job is { Status: "Completed", Stream: not StreamKind.None } ? StreamNoteFor(job) : null;
        bool joinable = job is { Status: "Completed", StreamNote: StreamCodes.NeedsMuxer } && manager is not null && tool is not null;
        if (note is null && !busy && result is null) { Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        NoteText.Text = note ?? "";
        NoteText.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        var extra = job?.AdditionalFiles ?? Array.Empty<string>();
        FilesText.Text = extra.Count == 0 || job is null ? "" : Loc.T("stream.saved_files") + "\n" + string.Join("\n", new[] { job.FilePath }.Concat(extra));
        FilesText.Visibility = FilesText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        JoinButton.Visibility = joinable && !busy ? Visibility.Visible : Visibility.Collapsed;
        JoinButton.Content = Loc.T(job?.AdditionalFiles.Count > 0 ? (tool?.IsAvailable == true ? "stream.btn.join" : "stream.btn.install_and_join") : (tool?.IsAvailable == true ? "stream.btn.convert" : "stream.btn.install_and_convert"));
        JoinBar.Visibility = CancelJoinButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ResultText.Text = busy ? Loc.T(tool?.IsAvailable == true ? "stream.join.running" : "tool.installing") : result ?? "";
        ResultText.Visibility = ResultText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyResultButton.Visibility = result is not null && resultIsError && !busy ? Visibility.Visible : Visibility.Collapsed;
        LicenceLine.Visibility = joinable && tool?.IsAvailable != true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string? StreamNoteFor(DownloadJob job) => job.StreamNote switch
    {
        StreamCodes.NeedsMuxer => Loc.T(job.AdditionalFiles.Count > 0 ? "stream.note.needs_tool" : "stream.note.needs_tool_ts"),
        StreamCodes.MuxFailed => Loc.T(job.AdditionalFiles.Count > 0 ? "stream.note.mux_failed" : "stream.note.mux_failed_single"),
        _ => null
    };

    private void Join_Click(object sender, RoutedEventArgs e) => JoinTask = JoinAsync();
    private void CancelJoin_Click(object sender, RoutedEventArgs e) { install?.Cancel(); joinCancel?.Cancel(); }
    private void CopyResult_Click(object sender, RoutedEventArgs e) { if (result is { } r) SafeClipboard.TrySetText(r); }
    private void Licence_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(VideoToolInfo.LicenceUrl);
    private CancellationTokenSource? joinCancel;

    /// <summary>Installs the tool when it is missing (progress, cancel), then joins the saved files with it. Every outcome ends in a plain sentence.</summary>
    public async Task JoinAsync()
    {
        if (busy || manager is null || tool is null || install is null) return;
        busy = true; result = null; resultIsError = false; JoinBar.IsIndeterminate = true; Refresh();
        try
        {
            if (!tool.IsAvailable)
            {
                var outcome = await install.RunAsync();
                if (outcome == ToolInstallRun.Outcome.Canceled) { result = Loc.T("tool.canceled"); return; }
                if (outcome == ToolInstallRun.Outcome.Failed) { result = install.Message ?? Loc.T("tool.error.other"); resultIsError = true; return; }
            }
            Refresh();
            joinCancel = new CancellationTokenSource();
            try
            {
                switch (await manager.JoinSavedStreamAsync(jobId, joinCancel.Token))
                {
                    case StreamJoinResult.Joined: result = Loc.T("stream.join.done", Current?.FileName ?? ""); break;
                    case StreamJoinResult.ToolMissing: result = Loc.T("stream.join.no_tool"); resultIsError = true; break;
                    default: result = Loc.T("stream.join.failed"); resultIsError = true; break;
                }
            }
            catch (OperationCanceledException) { result = Loc.T("tool.canceled"); }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException) { result = Loc.T("stream.join.failed"); resultIsError = true; }
            finally { joinCancel.Dispose(); joinCancel = null; }
        }
        finally { busy = false; Refresh(); }
    }
}
