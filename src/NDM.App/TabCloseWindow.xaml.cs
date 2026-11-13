using System.Windows;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>What the user answered when closing the tab (or the window) of running downloads. Null from the dialog = keep the tab open.</summary>
public sealed record TabCloseAnswer(TabCloseAction Action, bool Remember);

/// <summary>1.15.0: "Cancel the download" or "Hide (keep downloading)" for a running download's tab, with a Remember my choice tick. The same window asks once for all running tabs when the tabbed window is closed.</summary>
public partial class TabCloseWindow : Window
{
    /// <summary>Tests answer without showing a window: (owner, file name or null for several, running count) returns the answer.</summary>
    public static Func<Window?, string?, int, TabCloseAnswer?>? Override { get; set; }
    public TabCloseAnswer? Answer { get; private set; }
    public TabCloseWindow(string? fileName, int count)
    {
        InitializeComponent();
        Message.Text = fileName is not null ? Loc.T("tabs.close_message", fileName) : Loc.T("tabs.close_message_all", count);
        if (fileName is null) { HideButton.Content = Loc.T("tabs.close_hide_all"); CancelDownloadButton.Content = Loc.T("tabs.close_cancel_all"); }
        Loaded += (_, _) => HideButton.Focus();
    }
    private void Finish(TabCloseAnswer? answer) { Answer = answer; try { DialogResult = answer is not null; } catch (InvalidOperationException) { Close(); } }
    private void Hide_Click(object sender, RoutedEventArgs e) => Finish(new(TabCloseAction.Hide, RememberBox.IsChecked == true));
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => Finish(new(TabCloseAction.Cancel, RememberBox.IsChecked == true));
    private void Keep_Click(object sender, RoutedEventArgs e) => Finish(null);
    public static TabCloseAnswer? Ask(Window? owner, string? fileName, int count)
    {
        if (Override is { } answer) return answer(owner, fileName, count);
        var window = new TabCloseWindow(fileName, count) { Owner = owner };
        window.ShowDialog();
        return window.Answer;
    }
}
