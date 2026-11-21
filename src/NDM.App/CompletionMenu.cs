// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows.Controls;

namespace NDM.App;

/// <summary>Builds the 'When downloads finish' radio submenu used by both the menu bar and the tray.</summary>
public static class CompletionMenu
{
    public static void Fill(MenuItem menu, Func<CompletionActionKind> current, Action<CompletionActionKind> choose)
    {
        menu.Items.Clear();
        foreach (var kind in CompletionActionInfo.All)
        {
            var item = new MenuItem { Header = CompletionActionInfo.Label(kind), Tag = kind, IsCheckable = true, StaysOpenOnClick = false, ToolTip = CompletionActionInfo.Description(kind) };
            item.Click += (_, _) => { choose(kind); Sync(menu, current()); };
            menu.Items.Add(item);
        }
        menu.SubmenuOpened += (_, _) => Sync(menu, current());
        Sync(menu, current());
    }
    /// <summary>Radio semantics over checkable items: exactly the current kind is checked.</summary>
    public static void Sync(MenuItem menu, CompletionActionKind current)
    {
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsChecked = item.Tag is CompletionActionKind kind && kind == current;
    }
}
