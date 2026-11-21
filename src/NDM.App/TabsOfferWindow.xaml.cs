// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;

namespace NDM.App;

/// <summary>What the person answered to 'Show downloads as tabs?': yes, or no, with 'Don't ask again' ticked or not.</summary>
public sealed record TabsOfferAnswer(bool Yes, bool DontAskAgain);

/// <summary>2.0.0: asked once after the third download (a window of its own that does not block the app). Yes turns the tabbed window on; No keeps separate windows; 'Don't ask again' stops the question for good (without it the
/// question comes back once, ten downloads later).</summary>
public partial class TabsOfferWindow : Window
{
    /// <summary>Tests answer without showing a window.</summary>
    public static Func<Window?, TabsOfferAnswer?>? Override { get; set; }
    public TabsOfferAnswer? Answer { get; private set; }
    public TabsOfferWindow() { InitializeComponent(); Loaded += (_, _) => YesButton.Focus(); }
    private void Finish(bool yes) { Answer = new(yes, NeverBox.IsChecked == true); try { DialogResult = yes; } catch (InvalidOperationException) { Close(); } }
    private void Yes_Click(object sender, RoutedEventArgs e) => Finish(true);
    private void No_Click(object sender, RoutedEventArgs e) => Finish(false);
    /// <summary>Asks without blocking the app (the window is not modal, so the person can finish what they were doing first). Null when the window was closed without an answer (nothing is remembered then).</summary>
    public static Task<TabsOfferAnswer?> AskAsync(Window? owner)
    {
        if (Override is { } answer) return Task.FromResult(answer(owner));
        if (Localization.Dialogs.TestMode) return Task.FromResult<TabsOfferAnswer?>(null);
        var done = new TaskCompletionSource<TabsOfferAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new TabsOfferWindow { Owner = owner is { IsVisible: true, WindowState: not WindowState.Minimized } ? owner : null };
        window.Closed += (_, _) => done.TrySetResult(window.Answer);
        window.Show();
        return done.Task;
    }
}
