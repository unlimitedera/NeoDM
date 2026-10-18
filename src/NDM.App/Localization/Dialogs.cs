using System.Windows;

namespace NDM.App.Localization;

/// <summary>MessageBox with the reading direction of the active language. The button captions come from Windows and follow the Windows display language.</summary>
public static class Dialogs
{
    private static MessageBoxOptions Options => Loc.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;

    public static MessageBoxResult Show(string text, string title) => MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None, Options);
    public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage image) => MessageBox.Show(text, title, buttons, image, MessageBoxResult.None, Options);
    public static MessageBoxResult Show(Window? owner, string text, string title) => Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        owner is null ? MessageBox.Show(text, title, buttons, image, defaultResult, Options) : MessageBox.Show(owner, text, title, buttons, image, defaultResult, Options);
}
