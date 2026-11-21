// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>What the chooser returned: an existing queue, or a new one by name, and the busy choice when that queue is full.</summary>
public sealed record QueueChoice(string? QueueId, string? NewName, BusyChoice Busy);

/// <summary>1.16.0: "Add to queue" when more than one queue exists: which queue (default preselected, "+ New queue..."), and, when that queue is full, wait or download now. One small window replaces two questions.</summary>
public partial class QueueChooserWindow : Window
{
    private const string NewTag = "\u0001new";
    private readonly IReadOnlyList<DownloadQueue> _queues;
    private readonly Func<string, (int Running, int Limit)> _load;
    private readonly bool _askBusy; private readonly int _adding; private readonly BusyChoice _saved;
    public QueueChoice? Result { get; private set; }
    public IReadOnlyList<RadioButton> Choices { get; }
    public TextBox NewNameBox => NewName;
    public RadioButton WaitChoice => WaitRadio;
    public RadioButton NowChoice => NowRadio;
    public bool BusyVisible => BusyPanel.Visibility == Visibility.Visible;
    public Button Ok => OkButton;

    public QueueChooserWindow(IReadOnlyList<DownloadQueue> queues, string defaultId, Func<string, (int Running, int Limit)> load, bool askBusy, BusyChoice saved, int adding)
    {
        _queues = queues; _load = load; _askBusy = askBusy; _saved = saved; _adding = adding;
        InitializeComponent();
        var list = new List<RadioButton>();
        foreach (var q in queues) list.Add(Add(QueueEditorWindow.DisplayName(q), q.Id, q.Id == defaultId));
        list.Add(Add(Loc.T("queue.chooser.new"), NewTag, false));
        if (!list.Any(r => r.IsChecked == true)) list[0].IsChecked = true;
        Choices = list;
        Sync();
        Loaded += (_, _) => (list.FirstOrDefault(r => r.IsChecked == true) ?? list[0]).Focus();
    }
    private RadioButton Add(string text, string tag, bool check)
    {
        var r = new RadioButton { Content = text, Tag = tag, GroupName = "queue", IsChecked = check, Margin = new Thickness(0, 0, 0, 6) };
        r.Checked += (_, _) => Sync(); QueueList.Children.Add(r); return r;
    }
    private string? Selected => Choices?.FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
    private void Sync()
    {
        if (Choices is null) return;
        bool isNew = Selected == NewTag;
        NewName.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
        if (isNew) NewName.Focus();
        bool busy = false;
        if (!isNew && Selected is { } id)
        {
            var (running, limit) = _load(id);
            busy = BusyQueue.ShouldAsk(_askBusy, running, limit, _adding);
            if (busy) BusyMessage.Text = Loc.T("queue.chooser.busy", QueueEditorWindow.DisplayName(_queues.First(q => q.Id == id)), running, limit);
        }
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == NewTag)
        {
            var name = NewName.Text.Trim();
            if (!QueueIds.ValidName(name)) { ErrorText.Text = Loc.T("queue.editor.err_name"); ErrorText.Visibility = Visibility.Visible; NewName.Focus(); return; }
            if (_queues.Any(q => string.Equals(QueueEditorWindow.DisplayName(q), name, StringComparison.OrdinalIgnoreCase))) { ErrorText.Text = Loc.T("queue.editor.err_dup"); ErrorText.Visibility = Visibility.Visible; NewName.Focus(); return; }
            Finish(new QueueChoice(null, name, BusyChoice.Wait));
        }
        else Finish(new QueueChoice(Selected, null, BusyVisible ? (NowRadio.IsChecked == true ? BusyChoice.DownloadNow : BusyChoice.Wait) : _saved));
    }
    public void Finish(QueueChoice choice) { Result = choice; try { DialogResult = true; } catch (InvalidOperationException) { Close(); } }
    public static QueueChoice? Show(Window? owner, IReadOnlyList<DownloadQueue> queues, string defaultId, Func<string, (int Running, int Limit)> load, bool askBusy, BusyChoice saved, int adding)
    {
        var window = new QueueChooserWindow(queues, defaultId, load, askBusy, saved, adding);
        PromptWindows.Prepare(window, owner);
        window.ShowDialog();
        return window.Result;
    }
}
