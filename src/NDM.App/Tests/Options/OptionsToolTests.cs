using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NDM.App;
using NDM.Core;

/// <summary>1.6.0: Options > Downloads > Video tool (ffmpeg) and the stream connection count: defaults, schema migration, validation, save and cancel, install / remove flows with a fake tool (the real ffmpeg is never downloaded).</summary>
internal static partial class OptionsTests
{
    private static void PumpFor(int ms)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void PumpUntil(Func<bool> done, string what) { var end = DateTime.UtcNow.AddSeconds(15); while (!done()) { if (DateTime.UtcNow > end) throw new TimeoutException(what); PumpFor(40); } }

    private static void VideoToolOptions(string root)
    {
        // ---- defaults, schema bump and migration, clamping, round trip
        string dir = Path.Combine(root, "tool-settings"); Directory.CreateDirectory(dir);
        Check(AppSettings.CurrentSchema >= 8, "Settings schema was bumped for the stream options");
        var defaults = AppSettings.Load(Path.Combine(root, "tool-missing"));
        Check(defaults.StreamConnections == 8 && !defaults.UseFfmpegFromPath && OptionsHelper.CreateDownloadOptions(defaults).StreamConnections == 8, "Stream connections default to 8 and 'use ffmpeg from PATH' defaults to off");
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":7,\"Theme\":\"Dark\",\"StreamConnections\":3,\"UseFfmpegFromPath\":true}");
        var migrated = AppSettings.Load(dir);
        Check(migrated.StreamConnections == 8 && !migrated.UseFfmpegFromPath && migrated.Theme == "Dark" && migrated.SettingsSchema == AppSettings.CurrentSchema, "A file from before the stream options (schema 7) migrates to the defaults and keeps other choices");
        foreach (var bad in new[] { 0, 17, -4, 1000 })
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":" + AppSettings.CurrentSchema + ",\"StreamConnections\":" + bad + "}");
            Check(AppSettings.Load(dir).StreamConnections == 8, "Out-of-range stream connections restore the default: " + bad);
        }
        File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"SettingsSchema\":" + AppSettings.CurrentSchema + ",\"StreamConnections\":16,\"UseFfmpegFromPath\":true}");
        var kept = AppSettings.Load(dir);
        Check(kept.StreamConnections == 16 && kept.UseFfmpegFromPath, "Valid current-schema stream options are kept");
        var copy = kept.Copy(); copy.StreamConnections = 5; kept.CopyFrom(copy);
        Check(kept.StreamConnections == 5 && kept.UseFfmpegFromPath && OptionsHelper.CreateDownloadOptions(kept).StreamConnections == 5, "Applying an edited copy updates the stream connection count in place and the engine snapshot follows");

        // ---- the group in the window
        string state = Path.Combine(root, "tool-window-state"); Directory.CreateDirectory(state);
        var settings = AppSettings.Load(state); settings.DownloadDirectory = Path.Combine(root, "tool-files");
        var tool = new FakeVideoTool { Gate = new TaskCompletionSource() };
        var w = new SettingsWindow(settings, state, tool) { ShowInTaskbar = false };
        w.Show(); PumpFor(150);
        var tab = Field<TabControl>(w, "OptionsTabs").Items.OfType<TabItem>().First(i => (string)i.Header == "_Downloads");
        Field<TabControl>(w, "OptionsTabs").SelectedItem = tab; PumpFor(100);
        var group = Field<GroupBox>(w, "ToolGroup");
        Check((string)group.Header == "Video tool (ffmpeg)" && LogicalUnder(group, (DependencyObject)tab.Content), "Options > Downloads has the 'Video tool (ffmpeg)' group");
        Check(w.ToolStatusText == "Not installed" && Field<Button>(w, "ToolInstallButton").IsEnabled && !Field<Button>(w, "ToolRemoveButton").IsEnabled && Field<TextBox>(w, "ToolStatus").IsReadOnly, "Video tool: status 'Not installed' (selectable), Install enabled, Remove disabled");
        Check(!Field<CheckBox>(w, "UseFfmpegPath").IsChecked.GetValueOrDefault() && Field<TextBox>(w, "StreamConnections").Text == "8", "Video tool: 'Use ffmpeg found in PATH' starts off; stream connections start at 8");
        var opened = new List<string>(); var oldOpen = UrlOpener.Open; UrlOpener.Open = opened.Add;
        try { typeof(SettingsWindow).GetMethod("ToolLicence_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(w, new object?[] { null, new RoutedEventArgs() }); } finally { UrlOpener.Open = oldOpen; }
        Check(opened.SequenceEqual(new[] { "https://ffmpeg.org/legal.html" }), "Video tool: the licence link opens the official page through the https-only opener");

        Click(w, "ToolInstallButton"); PumpFor(100);
        Check(w.ToolRunning && Field<ProgressBar>(w, "ToolBar").IsVisible && Field<Button>(w, "ToolCancelButton").IsVisible && !Field<Button>(w, "ToolInstallButton").IsVisible && !Field<Button>(w, "ToolRemoveButton").IsVisible && Math.Abs(Field<ProgressBar>(w, "ToolBar").Value - 10) < 0.001, "Video tool: installing shows the progress bar and Cancel " + $"{w.ToolRunning} {Field<ProgressBar>(w, "ToolBar").Value} {Field<ProgressBar>(w, "ToolBar").IsVisible} {Field<Button>(w, "ToolCancelButton").IsVisible}");
        Click(w, "ToolCancelButton"); PumpUntil(() => w.ToolTask!.IsCompleted, "install cancel"); PumpFor(60);
        Check(!w.ToolRunning && !tool.Installed && Field<TextBox>(w, "ToolMessage").Text == "The installation was cancelled." && Field<Button>(w, "ToolInstallButton").IsVisible && Field<Button>(w, "ToolInstallButton").IsEnabled, "Video tool: Cancel stops the install and says so");
        tool.Gate = null; tool.Failure = new ToolException(ToolException.Space, "x");
        Click(w, "ToolInstallButton"); PumpUntil(() => w.ToolTask!.IsCompleted, "install failure"); PumpFor(60);
        Check(!tool.Installed && Field<TextBox>(w, "ToolMessage").Text.Contains("not enough free disk space") && Field<Button>(w, "ToolCopyButton").IsVisible && Field<TextBox>(w, "ToolMessage").IsReadOnly, "Video tool: a failed install shows a plain-words reason, selectable, with Copy");
        tool.Failure = null; tool.Bytes = 120_000_000;
        Click(w, "ToolInstallButton"); PumpUntil(() => w.ToolTask!.IsCompleted, "install"); PumpFor(60);
        Check(tool.Installed && w.ToolStatusText.StartsWith("Installed, version 9.0.2, ") && w.ToolStatusText.Contains("MB") && Field<TextBox>(w, "ToolMessage").Text == "ffmpeg is installed." && !Field<Button>(w, "ToolInstallButton").IsEnabled && Field<Button>(w, "ToolRemoveButton").IsEnabled && !Field<Button>(w, "ToolCopyButton").IsVisible, "Video tool: after the install the status shows version and size; Install is disabled and Remove enabled: " + w.ToolStatusText);
        Click(w, "ToolRemoveButton"); PumpFor(80);
        Check(!tool.Installed && tool.UninstallCalls == 1 && w.ToolStatusText == "Not installed" && Field<TextBox>(w, "ToolMessage").Text == "ffmpeg was removed." && Field<Button>(w, "ToolInstallButton").IsEnabled, "Video tool: Remove uninstalls and says so");
        w.Close();

        // ---- save: stream connections validation and 'use from PATH'; cancel changes nothing
        var s2 = AppSettings.Load(state);
        var t2 = new FakeVideoTool();
        var cancel = new SettingsWindow(s2, state, t2);
        cancel.Loaded += (_, _) => { Field<TextBox>(cancel, "StreamConnections").Text = "12"; Field<CheckBox>(cancel, "UseFfmpegPath").IsChecked = true; Click(cancel, "CancelButton"); };
        Check(cancel.ShowDialog() != true && s2.StreamConnections == 8 && !s2.UseFfmpegFromPath && !t2.FromPath, "Video tool: Cancel leaves the stream options and the tool setting unchanged");
        var save = new SettingsWindow(s2, state, t2);
        save.Loaded += (_, _) =>
        {
            foreach (var bad in new[] { "0", "17", "-1", "abc", "" })
            {
                Field<TextBox>(save, "StreamConnections").Text = bad; Click(save, "SaveButton");
                Check(save.IsVisible && s2.StreamConnections == 8 && !string.IsNullOrWhiteSpace(Field<TextBlock>(save, "ErrorText").Text), "Stream connections reject '" + bad + "' (1 to 16 only)");
            }
            Field<TextBox>(save, "StreamConnections").Text = "12"; Field<CheckBox>(save, "UseFfmpegPath").IsChecked = true;
            Check(Field<TextBox>(save, "StreamConnections").FlowDirection == FlowDirection.LeftToRight, "The numeric field is always left to right");
            Click(save, "SaveButton");
        };
        Check(save.ShowDialog() == true && s2.StreamConnections == 12 && s2.UseFfmpegFromPath && t2.FromPath, "Video tool: Save applies the stream connections and the PATH choice to the settings and to the running tool");
        var back = AppSettings.Load(state);
        Check(back.StreamConnections == 12 && back.UseFfmpegFromPath && OptionsHelper.CreateDownloadOptions(back).StreamConnections == 12, "Video tool: the choices are persisted and reach the engine options");
        var reopened = new SettingsWindow(back, state, new FakeVideoTool { FromPath = true }) { ShowInTaskbar = false }; reopened.Show(); PumpFor(100);
        Check(Field<CheckBox>(reopened, "UseFfmpegPath").IsChecked == true && Field<TextBox>(reopened, "StreamConnections").Text == "12", "Video tool: the saved values come back when Options is opened again");
        reopened.Close();

        // ---- Arabic mirrors the group; small and large sizes
        NDM.App.Localization.Loc.SetLanguage("ar");
        try
        {
            var ar = new SettingsWindow(AppSettings.Load(state), state, new FakeVideoTool()) { ShowInTaskbar = false }; ar.Show(); PumpFor(150);
            Check(ar.FlowDirection == FlowDirection.RightToLeft && (string)Field<GroupBox>(ar, "ToolGroup").Header != "Video tool (ffmpeg)", "Options (Arabic): the video tool group is translated and mirrored");
            ar.Close();
        }
        finally { NDM.App.Localization.Loc.SetLanguage("en"); }
        var sized = new SettingsWindow(AppSettings.Load(state), state, new FakeVideoTool()) { ShowInTaskbar = false }; sized.Show(); PumpFor(150);
        Field<TabControl>(sized, "OptionsTabs").SelectedItem = Field<TabControl>(sized, "OptionsTabs").Items.OfType<TabItem>().First(i => (string)i.Header == "_Downloads");
        sized.Width = sized.MinWidth; sized.Height = sized.MinHeight; PumpFor(150); sized.UpdateLayout();
        Check(Field<Button>(sized, "SaveButton").IsVisible && Field<GroupBox>(sized, "ToolGroup").ActualWidth > 0, "Options: the video tool group lives in the scrolling Downloads tab at the smallest size");
        sized.Width = 1300; sized.Height = 950; PumpFor(150); sized.UpdateLayout();
        Check(Field<GroupBox>(sized, "ToolGroup").ActualWidth > 600, "Options: the video tool group grows with a large window");
        sized.Close();
    }
}
