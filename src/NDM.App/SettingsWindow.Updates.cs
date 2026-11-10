using System.Windows;
using System.Windows.Controls;
using NDM.App.Localization;

namespace NDM.App;

public partial class SettingsWindow
{
    private static readonly UpdateNotifyMode[] UpdateModes = { UpdateNotifyMode.Quiet, UpdateNotifyMode.WhenIdle, UpdateNotifyMode.Off };
    private static string UpdateModeLabel(UpdateNotifyMode m) => Loc.T(m switch { UpdateNotifyMode.Quiet => "options.update.quiet", UpdateNotifyMode.WhenIdle => "options.update.idle", _ => "options.update.off" });
    private static string UpdateModeHelp(UpdateNotifyMode m) => Loc.T(m switch { UpdateNotifyMode.Quiet => "options.update.quiet_help", UpdateNotifyMode.WhenIdle => "options.update.idle_help", _ => "options.update.off_help" });
    public UpdateNotifyMode SelectedUpdateNotify => UpdateNotify.SelectedItem is ComboBoxItem { Tag: UpdateNotifyMode m } ? m : UpdateNotifyMode.Quiet;

    private void FillUpdateNotify(UpdateNotifyMode current)
    {
        UpdateNotify.Items.Clear();
        foreach (var mode in UpdateModes) UpdateNotify.Items.Add(new ComboBoxItem { Content = UpdateModeLabel(mode), Tag = mode });
        UpdateNotify.SelectedIndex = Array.IndexOf(UpdateModes, current) is >= 0 and var i ? i : 0;
        UpdateNotifyHelp.Text = UpdateModeHelp(SelectedUpdateNotify);
        Loc.LanguageChanged += RelabelUpdateNotify;
        Closed += (_, _) => Loc.LanguageChanged -= RelabelUpdateNotify;
    }

    private void RelabelUpdateNotify()
    {
        foreach (ComboBoxItem item in UpdateNotify.Items) item.Content = UpdateModeLabel((UpdateNotifyMode)item.Tag);
        UpdateNotifyHelp.Text = UpdateModeHelp(SelectedUpdateNotify);
    }

    private void UpdateNotify_Changed(object sender, SelectionChangedEventArgs e) { if (UpdateNotifyHelp is not null && UpdateNotify.SelectedItem is not null) UpdateNotifyHelp.Text = UpdateModeHelp(SelectedUpdateNotify); }

    /// <summary>Set by tests; the app uses the running update controller.</summary>
    public UpdateController? UpdatesForCheck { get; set; }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        var updates = UpdatesForCheck ?? (Application.Current as App)?.Updates;
        CheckNowStatus.Visibility = Visibility.Visible;
        if (updates is null) { CheckNowStatus.Text = Loc.T("about.status.unavailable"); return; }
        CheckNowButton.IsEnabled = false; CheckNowStatus.Text = Loc.T("about.status.checking");
        try { await updates.CheckNowAsync(text => CheckNowStatus.Text = text); }
        finally { CheckNowButton.IsEnabled = true; }
    }

    // 1.13.0: Options > General > When Windows starts NeoDM.
    private static readonly StartupWindowMode[] StartupModes = { StartupWindowMode.Tray, StartupWindowMode.Normal, StartupWindowMode.Maximized };
    private static string StartupModeLabel(StartupWindowMode m) => Loc.T(m switch { StartupWindowMode.Normal => "options.startup.normal", StartupWindowMode.Maximized => "options.startup.maximized", _ => "options.startup.tray" });
    public StartupWindowMode SelectedStartupWindow => StartupWindowBox.SelectedItem is ComboBoxItem { Tag: StartupWindowMode m } ? m : StartupWindowMode.Tray;
    private void FillStartupWindow(StartupWindowMode current)
    {
        StartupWindowBox.Items.Clear();
        foreach (var mode in StartupModes) StartupWindowBox.Items.Add(new ComboBoxItem { Content = StartupModeLabel(mode), Tag = mode });
        StartupWindowBox.SelectedIndex = Math.Max(0, Array.IndexOf(StartupModes, current));
        Loc.LanguageChanged += RelabelStartupWindow;
        Closed += (_, _) => Loc.LanguageChanged -= RelabelStartupWindow;
    }
    private void RelabelStartupWindow() { foreach (ComboBoxItem item in StartupWindowBox.Items) item.Content = StartupModeLabel((StartupWindowMode)item.Tag); }
}
