using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>
/// Read-only text that can be selected and copied. The look is the look of a TextBlock (see the SelectableText, SelectableLine and SelectableMuted styles in Themes/Controls.xaml);
/// this class adds the behaviour: a context menu with Copy, Copy all and Select all in the user's language, Ctrl+C that copies everything when nothing is selected, and selecting
/// everything when the control gets the keyboard focus (a visible focus mark that also makes the next Ctrl+C copy the whole value).
/// </summary>
public static class SelectableText
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SelectableText), new PropertyMetadata(false, OnEnabledChanged));
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.ContextMenuOpening -= Opening; box.PreviewKeyDown -= KeyDown; box.GotKeyboardFocus -= Focused;
        if (e.NewValue is not true) return;
        box.ContextMenuOpening += Opening; box.PreviewKeyDown += KeyDown; box.GotKeyboardFocus += Focused;
    }

    /// <summary>A new read-only text box in the SelectableText look, for text that code builds (rows of a list).</summary>
    public static TextBox Create(string text, bool muted = false, bool wrap = true)
    {
        var box = new TextBox { Text = text };
        box.SetResourceReference(FrameworkElement.StyleProperty, muted ? "SelectableMuted" : wrap ? "SelectableText" : "SelectableLine");
        return box;
    }

    /// <summary>Copies the whole text (not only the selection). False when there is nothing or the clipboard stayed busy.</summary>
    public static bool CopyAll(TextBox box) => SafeClipboard.TrySetText(box.Text);

    /// <summary>Copies the selection, or everything when nothing is selected.</summary>
    public static bool Copy(TextBox box) => SafeClipboard.TrySetText(box.SelectionLength > 0 ? box.SelectedText : box.Text);

    /// <summary>The entries of the menu for this box: Copy (only with a selection), Copy all and Select all (only with text). Used by the real menu and by the tests.</summary>
    public static (string Header, bool Enabled, Action Run)[] Entries(TextBox box) => new (string, bool, Action)[]
    {
        (Loc.T("common.menu.copy"), box.SelectionLength > 0, () => SafeClipboard.TrySetText(box.SelectedText)),
        (Loc.T("common.menu.copy_all"), box.Text.Length > 0, () => CopyAll(box)),
        (Loc.T("common.menu.select_all"), box.Text.Length > 0, () => { box.Focus(); box.SelectAll(); }),
    };

    public static ContextMenu BuildMenu(TextBox box)
    {
        var menu = new ContextMenu { PlacementTarget = box, FlowDirection = Loc.FlowDirection };
        foreach (var (header, enabled, run) in Entries(box))
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            var action = run; item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        return menu;
    }

    private static void Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (!box.IsKeyboardFocusWithin) box.Focus();
        box.ContextMenu = BuildMenu(box);
    }

    private static void KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && (e.Key == Key.C || e.Key == Key.Insert) && box.IsReadOnly) { Copy(box); e.Handled = true; }
    }

    private static void Focused(object sender, KeyboardFocusChangedEventArgs e)
    {
        // keyboard focus (Tab) selects everything so the focus is visible and Ctrl+C takes it all; a mouse click keeps the user's own selection
        if (sender is TextBox box && !Mouse.LeftButton.HasFlag(MouseButtonState.Pressed)) SelectOnKeyboardFocus(box);
    }

    /// <summary>What keyboard focus does to a value: everything is selected (the focus is visible and Ctrl+C takes it all).</summary>
    public static void SelectOnKeyboardFocus(TextBox box) { if (box.IsReadOnly) box.SelectAll(); }
}
