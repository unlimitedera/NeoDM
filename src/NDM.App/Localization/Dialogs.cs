using System.Windows;

namespace NDM.App.Localization;

/// <summary>Message boxes with the theme, the button captions and the reading direction of the active language (1.11.0: a themed window replaces the system box, which ignored the theme).</summary>
public static class Dialogs
{
    /// <summary>Tests answer without showing a window: (owner, text, title, buttons, image, default) returns the result.</summary>
    public static Func<Window?, string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult>? Override { get; set; }

    public static MessageBoxResult Show(string text, string title) => Show(null, text, title, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage image) => Show(null, text, title, buttons, image);
    public static MessageBoxResult Show(Window? owner, string text, string title) => Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        if (Override is { } answer) return answer(owner, text, title, buttons, image, defaultResult);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) return dispatcher.Invoke(() => MessageWindow.Ask(owner, text, title, buttons, image, defaultResult));
        return MessageWindow.Ask(owner, text, title, buttons, image, defaultResult);
    }
}
