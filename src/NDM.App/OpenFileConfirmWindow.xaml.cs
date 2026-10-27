using System.Windows;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>What the user decided in the open confirmation. Remember is true only when the box was shown and ticked and the answer was Yes.</summary>
public readonly record struct OpenConfirmResult(bool Open, bool Remember);

/// <summary>
/// Themed replacement of the Windows message box that asked "Open downloaded file" (1.5.9). Yes and No; No is the default (Enter and Esc both answer No).
/// For a safe kind (see <see cref="OpenFilePolicy"/>) it shows "Don't ask again for ... files"; for any other file it has no such box.
/// </summary>
public partial class OpenFileConfirmWindow : Window
{
    public OpenKind? RememberKind { get; }
    /// <summary>True after Yes.</summary>
    public bool Accepted { get; private set; }
    public bool DontAskAgain => DontAskBox.Visibility == Visibility.Visible && DontAskBox.IsChecked == true;
    public OpenConfirmResult Result => new(Accepted, Accepted && DontAskAgain);

    /// <param name="rememberKind">The kind to offer the box for, or null for no box (risky or unknown files, or nowhere to save the choice).</param>
    public OpenFileConfirmWindow(string fileName, OpenKind? rememberKind)
    {
        RememberKind = rememberKind;
        InitializeComponent();
        MessageText.Text = Loc.T("complete.confirm_open", fileName);
        if (rememberKind is { } kind)
        {
            DontAskBox.Content = Loc.T(CheckKey(kind));
            DontAskBox.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => { WindowActivation.BringToFront(this); NoButton.Focus(); };
    }

    /// <summary>Full keys, so the localization checks can see every text this window uses.</summary>
    internal static string CheckKey(OpenKind kind) => kind switch
    {
        OpenKind.Video => "openfile.chk.dont_ask_video",
        OpenKind.Music => "openfile.chk.dont_ask_music",
        OpenKind.Pictures => "openfile.chk.dont_ask_pictures",
        OpenKind.Documents => "openfile.chk.dont_ask_documents",
        _ => "openfile.chk.dont_ask_archives",
    };

    private void Yes_Click(object sender, RoutedEventArgs e) { Accepted = true; Finish(true); }
    private void No_Click(object sender, RoutedEventArgs e) { Accepted = false; Finish(false); }
    private void Finish(bool value) { try { DialogResult = value; } catch (InvalidOperationException) { Close(); } }

    /// <summary>Shows the window modally (centred on the owner when there is one) and returns the answer.</summary>
    public static OpenConfirmResult Ask(Window? owner, string fileName, OpenKind? rememberKind)
    {
        var window = new OpenFileConfirmWindow(fileName, rememberKind);
        if (owner is { IsLoaded: true, IsVisible: true }) window.Owner = owner;
        else { window.WindowStartupLocation = WindowStartupLocation.CenterScreen; window.ShowInTaskbar = true; }
        window.ShowDialog();
        return window.Result;
    }
}
