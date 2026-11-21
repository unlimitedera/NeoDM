// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>1.16.0: right-click a queue > Edit queue. Name, downloads at once, schedule (weekdays or one date, start, optional stop), run together, power action when done.
/// This replaces the old per-download Scheduler window.</summary>
public partial class QueueEditorWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly string _id;
    private readonly int _globalLimit;
    public SchedulePicker StartPicker { get; } = new();
    public SchedulePicker StopPicker { get; } = new();
    public IReadOnlyList<ToggleButton> DayButtons { get; }
    public TextBox NameField => NameBox;
    public TextBox LimitField => LimitBox;
    public CheckBox ScheduleCheck => ScheduleBox;
    public CheckBox OneOffCheck => OneOffBox;
    public CheckBox StopCheck => StopBox;
    public CheckBox ParallelCheck => ParallelBox;
    public ComboBox WhenDoneCombo => WhenDoneBox;
    public Button Save => SaveButton;
    public bool SchedulePanelEnabled => SchedulePanel.IsEnabled;
    public TextBlock Error => ErrorText;
    public TextBlock SummaryText => Summary;
    /// <summary>The clock of the pickers' one-off date check (tests set it).</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;
    public static readonly CompletionActionKind[] PowerChoices = { CompletionActionKind.None, CompletionActionKind.Sleep, CompletionActionKind.Hibernate, CompletionActionKind.Shutdown, CompletionActionKind.Restart };

    public QueueEditorWindow(DownloadManager manager, string queueId, int globalLimit)
    {
        _manager = manager; _id = queueId; _globalLimit = globalLimit;
        InitializeComponent();
        var q = manager.Queues.First(x => x.Id == queueId);
        NameBox.Text = DisplayName(q);
        NameBox.IsReadOnly = false;
        LimitBox.Text = (q.Limit ?? globalLimit).ToString(CultureInfo.InvariantCulture);
        LimitHint.Text = Loc.T("queue.editor.limit_hint", globalLimit);
        var days = new List<ToggleButton>();
        var culture = CultureInfo.CurrentCulture;
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)((i + 1) % 7); // Monday first
            var button = new ToggleButton { Style = (Style)FindResource("DayPill"), Content = culture.DateTimeFormat.GetAbbreviatedDayName(day), Tag = day, IsChecked = q.Schedule is { } s && s.OneOffDate is null && (s.DaysMask & QueueSchedule.Bit(day)) != 0 };
            System.Windows.Automation.AutomationProperties.SetName(button, culture.DateTimeFormat.GetDayName(day));
            button.Click += (_, _) => UpdateSummary();
            Days.Children.Add(button); days.Add(button);
        }
        DayButtons = days;
        StartHost.Content = StartPicker; StopHost.Content = StopPicker;
        StartPicker.WordsText.Visibility = StopPicker.WordsText.Visibility = Visibility.Collapsed; // the summary line under the pickers says it once
        var sched = q.Schedule;
        // The pickers ask for a date and a time; for weekdays only the time counts, so their "must be in the future" rule is moved far back in time.
        var far = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        StartPicker.Now = StopPicker.Now = () => OneOffBox.IsChecked == true ? DateTimeOffset.Now : far;
        var baseDay = sched?.OneOffDate ?? DateTime.Today.AddDays(1);
        StartPicker.Value = baseDay.Date + (sched?.StartTime ?? new TimeSpan(23, 0, 0));
        StopPicker.Value = baseDay.Date + (sched?.StopTime ?? new TimeSpan(6, 0, 0));
        StartPicker.ValueChanged += UpdateSummary; StopPicker.ValueChanged += UpdateSummary;
        ScheduleBox.IsChecked = sched?.Enabled == true; OneOffBox.IsChecked = sched?.OneOffDate is not null; StopBox.IsChecked = sched?.StopTime is not null;
        ParallelBox.IsChecked = q.AllowParallel; ResumeOnScheduleBox.IsChecked = q.ResumeOnSchedule;
        foreach (var kind in PowerChoices) WhenDoneBox.Items.Add(new ComboBoxItem { Content = CompletionActionInfo.Label(kind), Tag = kind });
        var chosen = Enum.TryParse<CompletionActionKind>(q.WhenDone, out var k) && PowerChoices.Contains(k) ? k : CompletionActionKind.None;
        WhenDoneBox.SelectedIndex = Array.IndexOf(PowerChoices, chosen);
        SyncEnabled();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>The Main queue shows a localized name unless the user renamed it.</summary>
    public static string DisplayName(DownloadQueue q) => q.Id == QueueIds.Main && q.Name == DownloadManager.MainQueueName ? Loc.T("queue.main") : q.Name;

    private void Schedule_Toggled(object sender, RoutedEventArgs e) { if (IsLoaded || SchedulePanel is not null) SyncEnabled(); }
    private void OneOff_Toggled(object sender, RoutedEventArgs e) { if (SchedulePanel is not null) SyncEnabled(); }
    private void Stop_Toggled(object sender, RoutedEventArgs e) { if (SchedulePanel is not null) SyncEnabled(); }
    private void SyncEnabled()
    {
        bool on = ScheduleBox.IsChecked == true, one = OneOffBox.IsChecked == true;
        SchedulePanel.IsEnabled = on;
        foreach (var d in DayButtons) d.IsEnabled = !one;
        StopPicker.IsEnabled = StopBox.IsChecked == true;
        UpdateSummary();
    }

    /// <summary>The schedule as typed, or null (with <see cref="Error"/> set) when it is not valid.</summary>
    public QueueSchedule? ReadSchedule(out string? problem)
    {
        problem = null;
        if (ScheduleBox.IsChecked != true) return new QueueSchedule { Enabled = false, DaysMask = MaskOfDays(), OneOffDate = null, StartTime = StartPicker.Value?.TimeOfDay ?? new TimeSpan(23, 0, 0) };
        bool one = OneOffBox.IsChecked == true;
        var schedule = new QueueSchedule { Enabled = true, DaysMask = one ? 0 : MaskOfDays() };
        if (StartPicker.Value is not { } start) { problem = Loc.T("queue.editor.err_time"); return null; }
        schedule.StartTime = start.TimeOfDay;
        if (one) { schedule.OneOffDate = start.Date; if (start <= Now()) { problem = Loc.T("validation.datetime_future"); return null; } }
        if (StopBox.IsChecked == true)
        {
            if (StopPicker.Value is not { } stop || stop.TimeOfDay == start.TimeOfDay) { problem = Loc.T("queue.editor.err_stop"); return null; }
            schedule.StopTime = stop.TimeOfDay;
        }
        if (schedule.Problem() == "days") { problem = Loc.T("queue.editor.err_days"); return null; }
        return schedule;
    }
    private int MaskOfDays() => DayButtons.Where(b => b.IsChecked == true).Aggregate(0, (m, b) => m | QueueSchedule.Bit((DayOfWeek)b.Tag));

    private void UpdateSummary()
    {
        if (Summary is null) return;
        var schedule = ReadSchedule(out var problem);
        Summary.Text = problem is not null || schedule is not { Enabled: true } ? (problem ?? "") : QueueText.StartsText(schedule, Now(), CultureInfo.CurrentCulture) + (schedule.StopTime is { } stop ? " " + Loc.T("queue.editor.and_stops", QueueText.Time(stop, CultureInfo.CurrentCulture)) : "");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var name = NameBox.Text.Trim();
        if (!QueueIds.ValidName(name)) { Fail(Loc.T("queue.editor.err_name"), NameBox); return; }
        int? limit = null;
        if (!int.TryParse(LimitBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 16) { Fail(Loc.T("queue.editor.err_limit"), LimitBox); return; }
        if (n != _globalLimit || _manager.Queues.First(x => x.Id == _id).Limit is not null) limit = n;
        var schedule = ReadSchedule(out var problem);
        if (schedule is null) { Fail(problem ?? Loc.T("queue.editor.err_time"), ScheduleBox); return; }
        var kind = WhenDoneBox.SelectedItem is ComboBoxItem { Tag: CompletionActionKind k } ? k : CompletionActionKind.None;
        try
        {
            var main = _id == QueueIds.Main && name == Loc.T("queue.main");
            _manager.UpdateQueue(_id, q =>
            {
                q.Name = main ? DownloadManager.MainQueueName : name; q.Limit = limit; q.AllowParallel = ParallelBox.IsChecked == true; q.ResumeOnSchedule = ResumeOnScheduleBox.IsChecked == true;
                q.Schedule = schedule.Enabled ? schedule : null; q.WhenDone = kind == CompletionActionKind.None ? null : kind.ToString();
                if (schedule.Enabled) q.LastStarted = null;
            });
        }
        catch (ArgumentException ex) when (ex.Message.Contains("already exists")) { Fail(Loc.T("queue.editor.err_dup"), NameBox); return; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or IOException) { Fail(Loc.T("queue.editor.err_save"), NameBox); return; }
        try { DialogResult = true; } catch (InvalidOperationException) { Close(); }
    }
    private void Fail(string message, Control focus) { ErrorText.Text = message; focus.Focus(); }
}

/// <summary>Plain-words schedule text for the sidebar and the editor.</summary>
public static class QueueText
{
    public static string Time(TimeSpan t, CultureInfo culture) => (DateTime.Today + t).ToString(culture.DateTimeFormat.ShortTimePattern, culture);
    /// <summary>"Starts Tuesday at 23:00" (a one-off date shows the date; today shows "today").</summary>
    public static string StartsText(QueueSchedule s, DateTime now, CultureInfo culture)
    {
        var next = s.NextStart(now) ?? s.CurrentStart(now);
        if (next is null && s.OneOffDate is { } one) next = one.Date + s.StartTime;
        if (next is null) return "";
        var day = next.Value.Date == now.Date ? Loc.T("queue.today") : s.OneOffDate is not null ? next.Value.ToString(culture.DateTimeFormat.ShortDatePattern, culture) : culture.DateTimeFormat.GetDayName(next.Value.DayOfWeek);
        return Loc.T("schedule.starts", day, Time(s.StartTime, culture));
    }
}
