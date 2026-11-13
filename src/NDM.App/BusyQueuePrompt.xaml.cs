using System.Globalization;
using System.Windows;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>1.15.0: "Other downloads are running." Wait in the queue (default), download now beside the running ones, or change how many download at once.</summary>
public partial class BusyQueuePrompt : Window
{
    public BusyPromptResult? Result { get; private set; }
    private int? newLimit;
    public BusyQueuePrompt(int running, int adding, int limit)
    {
        InitializeComponent();
        Message.Text = Loc.T(adding > 1 ? "busy.message_batch" : "busy.message", running, adding);
        CountBox.Text = limit.ToString(CultureInfo.InvariantCulture);
        Loaded += (_, _) => WaitRadio.Focus();
    }
    private int? ReadCount()
    {
        if (int.TryParse(CountBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 16) { CountError.Visibility = Visibility.Collapsed; return n; }
        CountError.Text = Loc.T("busy.count_invalid"); CountError.Visibility = Visibility.Visible; return null;
    }
    /// <summary>Apply changes the saved count and ends the question: with a free slot the new download simply starts.</summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ReadCount() is not { } n) return;
        newLimit = n; Finish(BusyChoice.Wait);
    }
    private void Ok_Click(object sender, RoutedEventArgs e) => Finish(NowRadio.IsChecked == true ? BusyChoice.DownloadNow : BusyChoice.Wait);
    public void Finish(BusyChoice choice)
    {
        Result = new BusyPromptResult(choice, newLimit, RememberBox.IsChecked == true);
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
    public static BusyPromptResult? Show(Window? owner, int running, int adding, int limit)
    {
        var window = new BusyQueuePrompt(running, adding, limit) { Owner = owner != null && owner.IsVisible ? owner : null };
        if (window.Owner is null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window.Result;
    }
}
