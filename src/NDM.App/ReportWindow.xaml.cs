// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using System.IO;
using System.Windows;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>Help > Report a problem. Builds the diagnostics zip on the Desktop, opens the prefilled GitHub page and can copy a summary. Nothing is sent by NeoDM.</summary>
public partial class ReportWindow : Window
{
    private readonly AppSettings _settings; private readonly string _state; private readonly bool _afterCrash;
    /// <summary>Where the zip goes (the Desktop; tests use a scratch folder).</summary>
    public string Desktop { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public ReportResult? Result { get; private set; }
    public ReportWindow(AppSettings settings, string stateDirectory, bool afterCrash = false)
    {
        _settings = settings; _state = stateDirectory; _afterCrash = afterCrash; InitializeComponent();
        if (afterCrash) CrashNote.Visibility = Visibility.Visible;
    }
    public static ReportWindow? ShowFor(Window? owner, AppSettings settings, string state, bool afterCrash = false)
    {
        if (Dialogs.TestMode) return null;
        var window = new ReportWindow(settings, state, afterCrash);
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.ShowDialog();
        return window;
    }
    private ReportInput Input() => new(_settings, _state, Desktop, AboutInfo.Version, DateTime.Now, null, _afterCrash);
    public void Create()
    {
        try
        {
            Result = DiagnosticsReport.Build(Input());
            State.Text = Loc.T("report.done", Result.ZipPath);
            ShowFileButton.Visibility = Visibility.Visible;
            UrlOpener.Open(Result.IssueUrl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { State.Text = Loc.T("report.failed"); }
    }
    private void Create_Click(object sender, RoutedEventArgs e) => Create();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = DiagnosticsReport.BuildSummary(Input(), t => Scrubber.Scrub(t));
        if (SafeClipboard.TrySetText(text)) State.Text = Loc.T("report.copied");
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (Result is null || !File.Exists(Result.ZipPath)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, Arguments = ExplorerArguments.Select(Result.ZipPath) })?.Dispose(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}
