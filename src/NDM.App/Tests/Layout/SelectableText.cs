using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.5.8: the values in the download windows (address, status, errors, request attempts, paths) are selectable and copyable; the layout still scales and mirrors.</summary>
internal static partial class LayoutTests
{
    private static (string Header, bool Enabled, Action Run)[] Menu(TextBox box) => SelectableText.Entries(box);

    private static void SelectableTextWindows(string root)
    {
        var captured = new List<string>(); SafeClipboard.Write = text => captured.Add(text);
        try
        {
            using var manager = NewManager(root, "selectable");
            var job = Sync(manager.AddScheduledAsync(new DownloadRequest("http://127.0.0.1:9/some/long/path/file-name.iso?token=" + new string('a', 120)), DateTimeOffset.UtcNow.AddDays(1)));
            foreach (var lang in new[] { "en", "ar" })
            {
                Loc.SetLanguage(lang); string tag = lang + ": ";
                var progress = new DownloadProgressWindow(manager, job.Id, new JobCompletionStore(), new AppSettings()) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
                progress.Show(); Pump(250);
                foreach (var name in new[] { "UrlText", "StatusText", "Notice", "LocationMessage", "LimitMessage", "AttemptsText" })
                {
                    var box = Named<TextBox>(progress, name);
                    Check(box.IsReadOnly && !box.IsReadOnlyCaretVisible && box.Focusable && SelectableText.GetEnabled(box) && box.BorderThickness == new Thickness(0) && box.Background is SolidColorBrush { Color.A: 0 }, tag + $"progress window {name}: read-only, focusable, caret hidden, no border, transparent (looks like text)");
                }
                var url = Named<TextBox>(progress, "UrlText"); url.Text = "https://example.org/a/b?c=d&e=f";
                Check(url.FlowDirection == FlowDirection.LeftToRight && url.SelectionBrush is SolidColorBrush && url.Foreground is SolidColorBrush, tag + "the address stays left-to-right even in a mirrored window; selection colours come from the theme");
                url.SelectionStart = 8; url.SelectionLength = 11; var entries = Menu(url);
                Check(entries.Length == 3 && entries[0].Header == Loc.T("common.menu.copy") && entries[1].Header == Loc.T("common.menu.copy_all") && entries[2].Header == Loc.T("common.menu.select_all") && entries.All(e => e.Enabled), tag + "menu: Copy, Copy all, Select all (localized), all enabled with a selection");
                captured.Clear(); entries[0].Run(); Check(captured.SequenceEqual(new[] { "example.org" }), tag + "Copy copies only the selected part");
                captured.Clear(); entries[1].Run(); Check(captured.SequenceEqual(new[] { "https://example.org/a/b?c=d&e=f" }), tag + "Copy all copies the whole address");
                url.SelectionLength = 0; Check(!Menu(url)[0].Enabled && Menu(url)[1].Enabled, tag + "Copy is disabled without a selection, Copy all stays");
                Menu(url)[2].Run(); Check(url.SelectedText == url.Text, tag + "Select all selects everything");
                captured.Clear(); SelectableText.Copy(url); Check(captured.SequenceEqual(new[] { url.Text }), tag + "Ctrl+C copies the selection (all of it here)");
                url.SelectionLength = 0; captured.Clear(); SelectableText.Copy(url); Check(captured.SequenceEqual(new[] { url.Text }), tag + "Ctrl+C with no selection copies the whole value");
                var empty = Named<TextBox>(progress, "Notice"); empty.Text = ""; Check(Menu(empty).All(e => !e.Enabled), tag + "an empty value has nothing to copy");
                var attempts = Named<TextBox>(progress, "AttemptsText"); attempts.Text = "Request attempts: 1. Range -> 403; 2. no Range -> 200"; captured.Clear(); Menu(attempts)[1].Run();
                Check(captured.Single().Contains("403") && captured.Single().Contains("no Range"), tag + "the request attempts trail can be copied");
                var status = Named<TextBox>(progress, "StatusText"); status.Text = "The server refused the request (403 Forbidden)."; captured.Clear(); Menu(status)[1].Run();
                Check(captured.Single() == status.Text && status.ToolTip is not null, tag + "an error in the status line can be copied and its tooltip carries the full text");
                var save = Named<TextBox>(progress, "SaveToText"); save.Text = @"C:\Downloads\ünï\file.iso"; captured.Clear(); Menu(save)[1].Run(); Check(captured.Single() == save.Text && SelectableText.GetEnabled(save), tag + "the save path box has the same menu");
                var opened = SelectableText.BuildMenu(url); Check(opened.Items.Count == 3 && opened.FlowDirection == Loc.FlowDirection, tag + "the context menu has three entries and follows the window direction");
                var label = Visual<TextBlock>(progress).First(t => Grid.GetRow(t) == Grid.GetRow(status) && Grid.GetColumn(t) == 0 && t.Parent == status.Parent);
                double labelX = label.TransformToAncestor(progress).Transform(new Point(0, 0)).X, valueX = status.TransformToAncestor(progress).Transform(new Point(0, 0)).X;
                Check(lang == "en" ? valueX > labelX : valueX < labelX, tag + $"status label and value are laid out {(lang == "en" ? "left to right" : "mirrored")} ({labelX:0} / {valueX:0})");
                Check(status.FlowDirection == Loc.FlowDirection, tag + "the status text follows the window direction");
                status.SelectionLength = 0; status.Focus(); System.Windows.Input.Keyboard.Focus(status); Pump(80); bool real = status.SelectedText == status.Text; status.SelectionLength = 0; SelectableText.SelectOnKeyboardFocus(status); Console.WriteLine("info: real focus selected everything: " + real + " focused=" + status.IsKeyboardFocused); Check(status.SelectedText == status.Text, tag + "keyboard focus selects the whole value (visible focus, Ctrl+C takes it all)");
                progress.Close();

                var capture = new DownloadOptionsDialog("https://example.org/dir/file.zip", "file.zip", "", "", root) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
                capture.Show(); Pump(200);
                foreach (var name in new[] { "DetectedInfo", "PrivateNetworkNotice", "ValidationError", "UrlDisplay" })
                    Check(Named<TextBox>(capture, name) is { IsReadOnly: true } b && SelectableText.GetEnabled(b), tag + $"capture dialog {name} has the Copy menu");
                var err = Named<TextBox>(capture, "ValidationError"); err.Text = "Enter a valid address."; captured.Clear(); Menu(err)[1].Run(); Check(captured.Single() == "Enter a valid address.", tag + "capture dialog: the error message can be copied");
                capture.Close();

                var complete = new DownloadCompleteWindow(job, new AppSettings(), () => { }) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
                complete.Show(); Pump(150);
                Check(Named<TextBox>(complete, "FileNameLabel") is { IsReadOnly: true } fn && SelectableText.GetEnabled(fn) && Named<TextBox>(complete, "PathLabel") is { IsReadOnly: true } pl && SelectableText.GetEnabled(pl) && SelectableText.GetEnabled(Named<TextBox>(complete, "SizeLabel")), tag + "completion window: file name, size and path can be selected and copied");
                captured.Clear(); Menu(Named<TextBox>(complete, "PathLabel"))[1].Run(); Check(captured.Single() == Named<TextBox>(complete, "PathLabel").Text && captured.Single().Length > 0, tag + "completion window: Copy all on the path copies the saved path");
                complete.Close();

                var props = new PropertiesWindow(new DownloadRow(job)) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
                props.Show(); Pump(200);
                var values = Visual<TextBox>(props).Where(t => t.IsReadOnly).ToList();
                Check(values.Count >= 6 && values.All(SelectableText.GetEnabled), tag + $"properties window: all {values.Count} values can be selected and copied");
                captured.Clear(); Menu(values.First(v => v.Text.Contains("127.0.0.1")))[1].Run(); Check(captured.Single().StartsWith("http://127.0.0.1:9/") && captured.Single().Length > 120, tag + "properties window: the long address copies whole");
                props.Close();
            }
        }
        finally { SafeClipboard.Write = text => Clipboard.SetText(text); Loc.SetLanguage("en"); }
    }
}
