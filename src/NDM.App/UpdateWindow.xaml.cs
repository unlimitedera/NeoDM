using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>Release-notes text from a GitHub release body (Markdown) as simple blocks.</summary>
public static class ReleaseNotes
{
    public enum Kind { Heading, Bullet, Text }
    public sealed record Block(Kind Kind, string Text);
    public static List<Block> Render(string? body)
    {
        var blocks = new List<Block>();
        foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            var heading = Regex.Match(line, @"^\s{0,3}#{1,6}\s+(.*)$");
            if (heading.Success) { blocks.Add(new(Kind.Heading, Clean(heading.Groups[1].Value))); continue; }
            var bullet = Regex.Match(line, @"^\s*[-*+]\s+(.*)$");
            if (bullet.Success) { blocks.Add(new(Kind.Bullet, Clean(bullet.Groups[1].Value))); continue; }
            if (blocks.Count > 0 && blocks[^1].Kind == Kind.Bullet && raw.StartsWith("  ")) { blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + Clean(line) }; continue; }
            blocks.Add(new(Kind.Text, Clean(line)));
        }
        return blocks;
    }
    private static string Clean(string s) => Regex.Replace(Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1"), @"[`*_]{1,3}", "").Trim();
}

/// <summary>Reports progress on the window's dispatcher regardless of the ambient synchronization context.</summary>
internal sealed class DispatcherProgress : IProgress<(long Done, long Total)>
{
    private readonly System.Windows.Threading.Dispatcher dispatcher; private readonly Action<(long Done, long Total)> handler;
    public DispatcherProgress(System.Windows.Threading.Dispatcher dispatcher, Action<(long Done, long Total)> handler) { this.dispatcher = dispatcher; this.handler = handler; }
    public void Report((long Done, long Total) value) => dispatcher.BeginInvoke(handler, value);
}

/// <summary>"NeoDM x.y.z is available". Modeless; opens above the browser like the capture dialog.</summary>
public partial class UpdateWindow : Window
{
    private readonly ReleaseInfo release;
    private readonly UpdateController controller;
    private CancellationTokenSource? cancel;
    public bool Installing { get; private set; }
    public UpdateWindow(ReleaseInfo release, UpdateController controller)
    {
        this.release = release; this.controller = controller;
        InitializeComponent(); WindowPlacements.Track(this, "Update");
        HeadingText.Text = Loc.T("update.heading", release.VersionText, controller.CurrentVersion);
        SubText.Text = release.Name == release.Tag || release.Name.Length == 0 ? "" : release.Name;
        SubText.Visibility = SubText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var block in ReleaseNotes.Render(release.Body))
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = block.Kind == ReleaseNotes.Kind.Bullet ? "•  " + block.Text : block.Text };
            if (block.Kind == ReleaseNotes.Kind.Heading) { text.FontWeight = FontWeights.SemiBold; text.Margin = new Thickness(0, 8, 0, 3); }
            else text.Margin = new Thickness(block.Kind == ReleaseNotes.Kind.Bullet ? 8 : 0, 2, 0, 2);
            NotesPanel.Children.Add(text);
        }
        if (NotesPanel.Children.Count == 0) NotesPanel.Children.Add(new TextBlock { Text = Loc.T("update.no_notes"), FlowDirection = Loc.FlowDirection, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
        ChangelogLink.NavigateUri = new Uri(UpdateChecker.ChangelogUrl); ReleaseLink.NavigateUri = new Uri(release.HtmlUrl);
        if (release.Installer is null || release.Checksums is null || release.Signature is null)
        {
            UpdateButton.IsEnabled = false;
            StatusText.Text = Loc.T(release.Installer is null ? "update.status.no_installer" : release.Checksums is null ? "update.status.no_sums" : "update.status.no_sig");
        }
        ContentRendered += (_, _) => WindowActivation.BringToFront(this);
        Closing += (_, e) => { if (Installing) e.Cancel = true; };
    }

    private void Changelog_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(UpdateChecker.ChangelogUrl);
    private void Release_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(release.HtmlUrl);
    private void Later_Click(object sender, RoutedEventArgs e) { if (Installing) { cancel?.Cancel(); return; } Close(); }
    private void Skip_Click(object sender, RoutedEventArgs e) { controller.SkipVersion(release); Close(); }

    /// <summary>Downloads, verifies and starts the installer. Returns true when the installer was started.</summary>
    public async Task<bool> InstallAsync()
    {
        if (Installing) return false;
        Installing = true; cancel = new CancellationTokenSource();
        UpdateButton.IsEnabled = SkipButton.IsEnabled = false; LaterButton.Content = Loc.T("update.btn.cancel");
        DownloadBar.Visibility = Visibility.Visible; DownloadBar.Value = 0; StatusText.Text = Loc.T("update.status.downloading");
        try
        {
            var progress = new DispatcherProgress(Dispatcher, p =>
            {
                DownloadBar.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                StatusText.Text = p.Total > 0 ? Loc.T("update.status.progress_total", p.Done / 1048576.0, p.Total / 1048576.0) : Loc.T("update.status.progress", p.Done / 1048576.0);
            });
            var msi = await controller.Installer.DownloadAndVerifyAsync(release, progress, cancel.Token);
            DownloadBar.Value = 100; StatusText.Text = Loc.T("update.status.verified");
            switch (controller.Installer.Launch(msi)) // keeps the verified file open until NeoDM exits
            {
                case LaunchResult.Started:
                    StatusText.Text = Loc.T("update.status.running");
                    Installing = false; Close(); return true;
                case LaunchResult.UacDeclined:
                    StatusText.Text = Loc.T("update.status.declined");
                    break;
                default:
                    StatusText.Text = Loc.T("update.status.start_failed");
                    break;
            }
        }
        catch (UpdateException ex) { StatusText.Text = ex.Message; }
        catch (OperationCanceledException) { StatusText.Text = Loc.T("update.status.cancelled"); }
        Installing = false; DownloadBar.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = true; SkipButton.IsEnabled = true; LaterButton.Content = Loc.T("update.btn.remind_me_later");
        return false;
    }
    private async void Update_Click(object sender, RoutedEventArgs e) => await InstallAsync();
}
