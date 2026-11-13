using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NDM.App;

/// <summary>One tab of the tabbed downloads window: file-type icon, name, state and percent, a thin status-coloured bar under it and a close button
/// (shown on hover and on the selected tab). The tab is a toggle button, so it is reachable and operable from the keyboard.</summary>
public sealed class DownloadTabButton : ToggleButton
{
    public static readonly DependencyProperty ExtensionProperty = DependencyProperty.Register(nameof(Extension), typeof(string), typeof(DownloadTabButton), new PropertyMetadata(""));
    public static readonly DependencyProperty FileTextProperty = DependencyProperty.Register(nameof(FileText), typeof(string), typeof(DownloadTabButton), new PropertyMetadata(""));
    public static readonly DependencyProperty StateTextProperty = DependencyProperty.Register(nameof(StateText), typeof(string), typeof(DownloadTabButton), new PropertyMetadata(""));
    public static readonly DependencyProperty CloseTipProperty = DependencyProperty.Register(nameof(CloseTip), typeof(string), typeof(DownloadTabButton), new PropertyMetadata(""));
    public string Extension { get => (string)GetValue(ExtensionProperty); set => SetValue(ExtensionProperty, value); }
    public string FileText { get => (string)GetValue(FileTextProperty); set => SetValue(FileTextProperty, value); }
    public string StateText { get => (string)GetValue(StateTextProperty); set => SetValue(StateTextProperty, value); }
    public string CloseTip { get => (string)GetValue(CloseTipProperty); set => SetValue(CloseTipProperty, value); }
    public string JobId { get; }
    /// <summary>Theme token of the bar under the tab (the same Status*Brush tokens as the progress bars).</summary>
    public string? StatusToken { get; private set; }
    public event Action<DownloadTabButton>? CloseRequested;
    private Border? bar;

    public DownloadTabButton(string jobId)
    {
        JobId = jobId; Focusable = true; IsTabStop = true;
        SetResourceReference(StyleProperty, "DownloadTabStyle");
        AutomationProperties.SetItemStatus(this, "");
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        bar = GetTemplateChild("PART_Bar") as Border;
        if (GetTemplateChild("PART_Close") is Button close) close.Click += (_, e) => { e.Handled = true; CloseRequested?.Invoke(this); };
        ApplyToken();
    }
    public void SetStatusToken(string token) { StatusToken = token; ApplyToken(); }
    private void ApplyToken() { if (bar is not null && StatusToken is not null) bar.SetResourceReference(Border.BackgroundProperty, StatusToken); }
    /// <summary>Middle click closes the tab.</summary>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CloseRequested?.Invoke(this); return; }
        base.OnMouseUp(e);
    }
    protected override void OnMouseDown(MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; return; } base.OnMouseDown(e); }
    /// <summary>A selected tab stays selected when it is clicked again.</summary>
    protected override void OnToggle() { if (IsChecked != true) base.OnToggle(); }
}
