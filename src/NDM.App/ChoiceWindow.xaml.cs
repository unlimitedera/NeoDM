using System.Windows;
using System.Windows.Controls;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>A small themed window with a message and a few choice buttons (the last one is the cancel choice). Used for "this video only / the whole playlist".</summary>
public partial class ChoiceWindow : Window
{
    /// <summary>Tests replace this to answer without showing a window: (owner, title, message, labels) returns the index or -1.</summary>
    public static Func<Window?, string, string, string[], int>? Override { get; set; }
    public int Result { get; private set; } = -1;
    public ChoiceWindow(string title, string message, string[] labels)
    {
        InitializeComponent(); Title = title; Message.Text = message;
        for (int i = 0; i < labels.Length; i++)
        {
            var index = i; var last = i == labels.Length - 1 && labels.Length > 1;
            var button = new Button { Content = labels[i], MinWidth = 96, Margin = new Thickness(0, 0, 8, 6), IsDefault = i == 0, IsCancel = last };
            if (i == 0) button.Style = (Style)FindResource("PrimaryButton");
            button.Click += (_, _) => { Result = last ? -1 : index; try { DialogResult = !last; } catch (InvalidOperationException) { Close(); } };
            Buttons.Children.Add(button);
        }
        Loaded += (_, _) => { if (Buttons.Children.Count > 0) Buttons.Children[0].Focus(); };
    }
    public static int Ask(Window? owner, string title, string message, params string[] labels)
    {
        if (Override is { } answer) return answer(owner, title, message, labels);
        var window = new ChoiceWindow(title, message, labels) { Owner = owner };
        window.ShowDialog();
        return window.Result;
    }
}
