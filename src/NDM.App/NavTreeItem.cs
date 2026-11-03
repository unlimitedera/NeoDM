using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NDM.App;

/// <summary>1.10.0: the All downloads entry of the sidebar. It carries an expand chevron; <see cref="IsExpanded"/> shows or hides the category entries below it.</summary>
public sealed class NavTreeItem : ListBoxItem
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(NavTreeItem),
        new PropertyMetadata(true, (d, e) => { var item = (NavTreeItem)d; NavIcon.SetExpanded(item, (bool)e.NewValue); item.Expanded?.Invoke(item, (bool)e.NewValue); item.RaiseState((bool)e.OldValue, (bool)e.NewValue); }));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    /// <summary>Raised when the user or code expands or collapses the entry.</summary>
    public event Action<NavTreeItem, bool>? Expanded;
    public NavTreeItem() { NavIcon.SetChevron(this, true); NavIcon.SetExpanded(this, true); }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("Chevron") is UIElement chevron)
        {
            chevron.MouseLeftButtonDown -= ChevronDown; chevron.MouseLeftButtonDown += ChevronDown;
        }
    }
    private void ChevronDown(object sender, MouseButtonEventArgs e) { IsExpanded = !IsExpanded; e.Handled = true; }
    private void RaiseState(bool oldValue, bool newValue)
    {
        if (ItemsControl.ItemsControlFromItemContainer(this) is ListBox list && UIElementAutomationPeer.FromElement(list) is ListBoxAutomationPeer listPeer && listPeer.GetChildren() is { } kids)
            kids.OfType<NavTreeItemPeer>().FirstOrDefault(p => ReferenceEquals(p.Item, this))?.RaiseExpandState(oldValue, newValue);
    }
}

/// <summary>Sidebar list whose All entry reports tree semantics (expand and collapse pattern, level).</summary>
public sealed class NavListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new NavListBoxPeer(this);
    private sealed class NavListBoxPeer(NavListBox owner) : ListBoxAutomationPeer(owner)
    {
        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) => item is NavTreeItem ? new NavTreeItemPeer(item, this) : base.CreateItemAutomationPeer(item);
    }
}

internal sealed class NavTreeItemPeer(object owner, SelectorAutomationPeer parent) : ListBoxItemAutomationPeer(owner, parent), IExpandCollapseProvider
{
    public NavTreeItem Item => (NavTreeItem)((ItemAutomationPeer)this).Item;
    public override object GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);
    public ExpandCollapseState ExpandCollapseState => Item.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
    public void Expand() => Item.IsExpanded = true;
    public void Collapse() => Item.IsExpanded = false;
    internal void RaiseExpandState(bool oldValue, bool newValue) =>
        RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, oldValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed, newValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
}
