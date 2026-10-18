using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>Modal, always-on-top warning before a session-ending action. Built in code; no XAML needed.</summary>
public sealed class CountdownWindow : Window
{
    private readonly CompletionActionKind kind;
    private readonly TextBlock message = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer timer;
    public int Remaining { get; private set; }
    public CountdownOutcome Outcome { get; private set; } = CountdownOutcome.Cancelled;
    public string MessageText => message.Text;
    public Button CancelButton { get; } = new() { Content = Loc.T("countdown.btn.cancel"), IsCancel = true, IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
    public Button NowButton { get; } = new() { Content = Loc.T("countdown.btn.now"), MinWidth = 100 };
    public CountdownWindow(CompletionActionKind kind, int seconds, int tickMilliseconds = 1000)
    {
        this.kind = kind; Remaining = Math.Max(1, seconds);
        Title = "NeoDM"; Topmost = true; ShowInTaskbar = true; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight; MinWidth = 380; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(CancelButton); buttons.Children.Add(NowButton);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(message);
        panel.Children.Add(new TextBlock { Text = Loc.T("countdown.text.finished"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = WrapMode, MaxWidth = 420 });
        panel.Children.Add(buttons);
        Content = panel;
        System.Windows.Automation.AutomationProperties.SetName(message, Loc.T("countdown.a11y.countdown"));
        System.Windows.Automation.AutomationProperties.SetLiveSetting(message, System.Windows.Automation.AutomationLiveSetting.Assertive);
        CancelButton.Click += (_, _) => Finish(CountdownOutcome.Cancelled);
        NowButton.Click += (_, _) => Finish(CountdownOutcome.DoItNow);
        // Closing the window by any other route (Alt+F4, taskbar) is a cancel, never a silent proceed.
        Closed += (_, _) => timer!.Stop();
        timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(Math.Max(1, tickMilliseconds)) };
        timer.Tick += (_, _) => Tick();
        UpdateText();
        Loaded += (_, _) => { timer.Start(); Activate(); };
    }
    private const TextWrapping WrapMode = TextWrapping.Wrap;
    private void UpdateText() => message.Text = CompletionActionInfo.CountdownMessage(kind, Remaining);
    /// <summary>One countdown step; public so tests can drive it without waiting.</summary>
    public void Tick()
    {
        if (Remaining <= 1) { Remaining = 0; Finish(CountdownOutcome.Elapsed); return; }
        Remaining--; UpdateText();
    }
    private void Finish(CountdownOutcome outcome)
    {
        Outcome = outcome; timer.Stop();
        try { DialogResult = outcome != CountdownOutcome.Cancelled; } catch (InvalidOperationException) { Close(); }
    }
}

public sealed class CountdownPrompt : ICountdownPrompt
{
    public CountdownOutcome Show(CompletionActionKind kind, int seconds)
    {
        var window = new CountdownWindow(kind, seconds);
        if (Application.Current?.MainWindow is { IsVisible: true } owner) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        window.ShowDialog();
        return window.Outcome;
    }
}
