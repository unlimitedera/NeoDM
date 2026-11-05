using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>State flags of one day button of the calendar; the theme styles (CalendarDay in Controls.xaml) react to them.</summary>
public static class CalendarCell
{
    public static readonly DependencyProperty IsPickedProperty = DependencyProperty.RegisterAttached("IsPicked", typeof(bool), typeof(CalendarCell), new PropertyMetadata(false));
    public static readonly DependencyProperty IsTodayProperty = DependencyProperty.RegisterAttached("IsToday", typeof(bool), typeof(CalendarCell), new PropertyMetadata(false));
    public static readonly DependencyProperty IsOutsideProperty = DependencyProperty.RegisterAttached("IsOutside", typeof(bool), typeof(CalendarCell), new PropertyMetadata(false));
    public static bool GetIsPicked(DependencyObject d) => (bool)d.GetValue(IsPickedProperty);
    public static void SetIsPicked(DependencyObject d, bool v) => d.SetValue(IsPickedProperty, v);
    public static bool GetIsToday(DependencyObject d) => (bool)d.GetValue(IsTodayProperty);
    public static void SetIsToday(DependencyObject d, bool v) => d.SetValue(IsTodayProperty, v);
    public static bool GetIsOutside(DependencyObject d) => (bool)d.GetValue(IsOutsideProperty);
    public static void SetIsOutside(DependencyObject d, bool v) => d.SetValue(IsOutsideProperty, v);
}

/// <summary>A themed month view: previous and next month, weekday names and first day of the week from the culture, today ringed, the picked day in the accent colour, days before today disabled.
/// Keyboard: arrows move by day or week, Page Up/Down by month, Home/End to the first and last day of the month, Enter or Space picks.</summary>
public sealed class MonthCalendar : Border
{
    private readonly Grid grid = new();
    private readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button previous = new() { Content = "‹", Width = 34, Height = 30, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, FontSize = 18 }, next = new() { Content = "›", Width = 34, Height = 30, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, FontSize = 18 };
    private readonly Dictionary<DateTime, Button> days = new();
    private CultureInfo culture = CultureInfo.CurrentCulture;
    private DateTime month = DateTime.Today, cursor = DateTime.Today;
    public DateTime? Picked { get; private set; }
    public DateTime Minimum { get; set; } = DateTime.Today;
    public Func<DateTime> Today { get; set; } = () => DateTime.Today;
    public event Action<DateTime>? DayPicked;
    public DateTime Cursor => cursor;
    public DateTime DisplayedMonth => month;
    public IReadOnlyDictionary<DateTime, Button> DayButtons => days;

    public MonthCalendar()
    {
        Padding = new Thickness(8);
        var root = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1); Grid.SetColumn(next, 2);
        header.Children.Add(previous); header.Children.Add(title); header.Children.Add(next);
        previous.Click += (_, _) => Move(month.AddMonths(-1), keepDay: true);
        next.Click += (_, _) => Move(month.AddMonths(1), keepDay: true);
        AutomationProperties.SetName(previous, Loc.T("schedule.a11y.previous_month")); AutomationProperties.SetName(next, Loc.T("schedule.a11y.next_month"));
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        root.Children.Add(header);
        grid.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(grid);
        Child = root;
        Focusable = false;
        PreviewKeyDown += OnKey;
        AutomationProperties.SetName(this, Loc.T("schedule.a11y.calendar"));
    }

    public void Show(CultureInfo cultureInfo, DateTime? picked, DateTime minimum)
    {
        culture = cultureInfo; Minimum = minimum.Date; Picked = picked?.Date;
        cursor = (picked ?? Today()).Date; if (cursor < Minimum) cursor = Minimum;
        month = new DateTime(cursor.Year, cursor.Month, 1);
        Rebuild();
    }

    private void Move(DateTime newMonthOrDay, bool keepDay)
    {
        if (keepDay) { int d = Math.Min(cursor.Day, DateTime.DaysInMonth(newMonthOrDay.Year, newMonthOrDay.Month)); cursor = new DateTime(newMonthOrDay.Year, newMonthOrDay.Month, d); }
        else cursor = newMonthOrDay.Date;
        if (cursor < Minimum) cursor = Minimum;
        month = new DateTime(cursor.Year, cursor.Month, 1);
        Rebuild();
        if (days.TryGetValue(cursor, out var button)) button.Focus();
    }

    private void Rebuild()
    {
        var f = culture.DateTimeFormat;
        title.Text = month.ToString("MMMM yyyy", culture);
        previous.IsEnabled = month > new DateTime(Minimum.Year, Minimum.Month, 1);
        grid.Children.Clear(); grid.RowDefinitions.Clear(); grid.ColumnDefinitions.Clear(); days.Clear();
        for (int c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 32 });
        for (int r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var first = f.FirstDayOfWeek;
        for (int c = 0; c < 7; c++)
        {
            var name = f.GetAbbreviatedDayName((DayOfWeek)(((int)first + c) % 7));
            var label = new TextBlock { Text = name.Length > 3 ? name[..Math.Min(3, name.Length)] : name, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Grid.SetColumn(label, c); grid.Children.Add(label);
        }
        int offset = ((int)month.DayOfWeek - (int)first + 7) % 7;
        var start = month.AddDays(-offset);
        var today = Today().Date;
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var button = new Button { Content = date.Day.ToString(culture), Tag = date, IsEnabled = date >= Minimum, Focusable = true, IsTabStop = date == cursor };
            button.SetResourceReference(FrameworkElement.StyleProperty, "CalendarDay");
            CalendarCell.SetIsOutside(button, date.Month != month.Month);
            CalendarCell.SetIsToday(button, date == today);
            CalendarCell.SetIsPicked(button, Picked == date);
            AutomationProperties.SetName(button, date.ToString("D", culture));
            var captured = date;
            button.Click += (_, _) => Pick(captured);
            Grid.SetRow(button, 1 + i / 7); Grid.SetColumn(button, i % 7);
            grid.Children.Add(button); days[date] = button;
        }
    }

    public void Pick(DateTime date)
    {
        if (date.Date < Minimum) return;
        Picked = date.Date; cursor = date.Date;
        if (date.Month != month.Month || date.Year != month.Year) { month = new DateTime(date.Year, date.Month, 1); }
        Rebuild();
        if (days.TryGetValue(cursor, out var b)) b.Focus();
        DayPicked?.Invoke(date.Date);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        DateTime? target = e.Key switch
        {
            Key.Left => cursor.AddDays(rtl ? 1 : -1), Key.Right => cursor.AddDays(rtl ? -1 : 1),
            Key.Up => cursor.AddDays(-7), Key.Down => cursor.AddDays(7),
            Key.PageUp => cursor.AddMonths(-1), Key.PageDown => cursor.AddMonths(1),
            Key.Home => new DateTime(cursor.Year, cursor.Month, 1), Key.End => new DateTime(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month)),
            _ => null
        };
        if (target is { } t) { Move(t, keepDay: false); e.Handled = true; return; }
        if (e.Key is Key.Enter or Key.Space && Keyboard.FocusedElement is Button { Tag: DateTime date }) { Pick(date); e.Handled = true; }
    }
}

/// <summary>1.11.0: the one schedule control used by every dialog that asks for a start time. An editable text field in the culture's date and time format, a calendar drop-down, hour and minute spinners (12 or 24 hour clock from the culture),
/// quick buttons and a plain-words line. The text and the pickers stay in sync both ways; unreadable, past and non-existent times show an inline message and never schedule.</summary>
public sealed class SchedulePicker : UserControl
{
    private readonly TextBox box = new() { MinWidth = 170 };
    private readonly Button drop = new() { Padding = new Thickness(8, 0, 8, 0), MinWidth = 34 };
    private DateTime closedAt = DateTime.MinValue;
    private readonly TextBlock words = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly MonthCalendar calendar = new();
    private readonly TextBox hourBox = new() { Width = 48, Padding = new Thickness(0, 4, 0, 4), MinHeight = 0, TextAlignment = TextAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, MaxLength = 2 }, minuteBox = new() { Width = 48, Padding = new Thickness(0, 4, 0, 4), MinHeight = 0, TextAlignment = TextAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, MaxLength = 2 };
    private readonly Button ampm = new() { MinWidth = 52, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
    private readonly StackPanel quick = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private CultureInfo culture = ScheduleText.Gregorian(CultureInfo.CurrentCulture);
    private bool syncing;
    private DateTime? value;
    /// <summary>The clock and the time zone; tests replace them (daylight saving cases).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;
    public event Action? ValueChanged;
    public TextBox Box => box;
    public MonthCalendar Calendar => calendar;
    public Button DropButton => drop;
    public Popup DropPopup => popup;
    public TextBox HourBox => hourBox;
    public TextBox MinuteBox => minuteBox;
    public Button AmPmButton => ampm;
    public Panel QuickButtons => quick;
    public TextBlock WordsText => words;
    public TextBlock ErrorText => error;

    public CultureInfo Culture
    {
        get => culture;
        set { culture = ScheduleText.Gregorian(value); RebuildQuick(); Refresh(rewriteText: box.Text.Length > 0 && this.value is not null); }
    }

    /// <summary>The text field. Reading it gives what is shown; setting it (in the culture format, or yyyy-MM-dd HH:mm) updates the pickers.</summary>
    public string Text
    {
        get => box.Text;
        set { box.Text = value ?? ""; Reparse(); }
    }

    /// <summary>The chosen local date and time, or null while the text is empty or unreadable.</summary>
    public DateTime? Value
    {
        get => value;
        set { this.value = value is { } v ? new DateTime(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, DateTimeKind.Unspecified) : null; Refresh(rewriteText: true); }
    }

    /// <summary>The check of the current text against the clock.</summary>
    public ScheduleCheck Check => ScheduleText.Check(box.Text, culture, Zone, Now());

    /// <summary>The instant to schedule. Throws a user message (shown inline too) when the text is empty, unreadable, in the past or does not exist.</summary>
    public DateTimeOffset Require()
    {
        var check = Check;
        ShowError(check.Error);
        if (check.At is not { } at) throw new UserMessageException(check.Error ?? Loc.T("schedule.error.empty"));
        return at;
    }

    public SchedulePicker()
    {
        Focusable = false;
        var root = new StackPanel();
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(drop, 1); drop.Margin = new Thickness(6, 0, 0, 0);
        box.FlowDirection = FlowDirection.LeftToRight;
        drop.Content = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource("IconScheduled"), Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
        ((System.Windows.Shapes.Path)drop.Content).SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "InkBrush");
        row.Children.Add(box); row.Children.Add(drop);
        words.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        root.Children.Add(row); root.Children.Add(words); root.Children.Add(error);
        Content = root;
        AutomationProperties.SetName(box, Loc.T("schedule.a11y.text"));
        AutomationProperties.SetName(drop, Loc.T("schedule.a11y.open_calendar"));
        drop.ToolTip = Loc.T("schedule.a11y.open_calendar");
        AutomationProperties.SetName(hourBox, Loc.T("schedule.a11y.hour")); AutomationProperties.SetName(minuteBox, Loc.T("schedule.a11y.minute"));
        AutomationProperties.SetName(ampm, Loc.T("schedule.a11y.am_pm"));

        // popup: calendar, time, quick buttons
        var pop = new StackPanel { Margin = new Thickness(0) };
        var time = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var timeLabel = new TextBlock { Text = Loc.T("schedule.time"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        timeLabel.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var colon = new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        colon.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        time.Children.Add(timeLabel); time.Children.Add(Spinner(hourBox, 1)); time.Children.Add(colon); time.Children.Add(Spinner(minuteBox, 5)); time.Children.Add(ampm);
        quick.Margin = new Thickness(8, 8, 8, 8);
        pop.Children.Add(calendar); pop.Children.Add(time); pop.Children.Add(quick);
        var frame = new Border { Child = pop, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 4, 8, 8), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.35 } };
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        popup.Child = frame; popup.PlacementTarget = drop;
        drop.Click += (_, _) => { if (!popup.IsOpen && (DateTime.UtcNow - closedAt).TotalMilliseconds > 250) popup.IsOpen = true; else popup.IsOpen = false; };
        popup.Closed += (_, _) => closedAt = DateTime.UtcNow;
        popup.Opened += (_, _) => { calendar.Today = () => NowLocal.Date; calendar.Show(culture, value, NowLocal.Date); LoadTime(); };
        popup.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; drop.Focus(); e.Handled = true; } };
        calendar.DayPicked += date => { var t = value?.TimeOfDay ?? DefaultTime(); SetFromPicker(date + t); };
        hourBox.LostFocus += (_, _) => TimeEdited(); minuteBox.LostFocus += (_, _) => TimeEdited();
        hourBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TimeEdited(); }; minuteBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TimeEdited(); };
        ampm.Click += (_, _) => { var v = value ?? NowLocal.Date + DefaultTime(); SetFromPicker(v.AddHours(v.Hour >= 12 ? -12 : 12)); };
        box.TextChanged += (_, _) => { if (!syncing) Reparse(); };
        box.LostFocus += (_, _) => { if (value is not null) { syncing = true; box.Text = ScheduleText.Format(value.Value, culture); syncing = false; } };
        RebuildQuick();
        Refresh(rewriteText: false);
    }

    /// <summary>The current wall-clock time in <see cref="Zone"/>.</summary>
    private DateTime NowLocal => TimeZoneInfo.ConvertTime(Now(), Zone).DateTime;

    private TimeSpan DefaultTime() { var n = NowLocal; return TimeSpan.FromHours(n.Hour + 1 > 23 ? 23 : n.Hour + 1); }

    private FrameworkElement Spinner(TextBox target, int step)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };
        var up = new Button { Content = "▲", FontSize = 8, Width = 22, Height = 14, Padding = new Thickness(0), MinHeight = 0 };
        var down = new Button { Content = "▼", FontSize = 8, Width = 22, Height = 14, Padding = new Thickness(0), MinHeight = 0 };
        AutomationProperties.SetName(up, Loc.T("schedule.a11y.increase")); AutomationProperties.SetName(down, Loc.T("schedule.a11y.decrease"));
        up.Click += (_, _) => Step(target, step); down.Click += (_, _) => Step(target, -step);
        target.PreviewKeyDown += (_, e) => { if (e.Key == Key.Up) { Step(target, step); e.Handled = true; } else if (e.Key == Key.Down) { Step(target, -step); e.Handled = true; } };
        target.PreviewMouseWheel += (_, e) => { if (target.IsKeyboardFocusWithin) { Step(target, e.Delta > 0 ? step : -step); e.Handled = true; } };
        buttons.Children.Add(up); buttons.Children.Add(down);
        panel.Children.Add(target); panel.Children.Add(buttons);
        return panel;
    }

    /// <summary>Adds <paramref name="delta"/> hours or minutes to the chosen time (wrapping inside the day) and keeps the date.</summary>
    public void Step(TextBox target, int delta)
    {
        var v = value ?? NowLocal.Date + DefaultTime();
        var next = ReferenceEquals(target, hourBox) ? v.Date + TimeSpan.FromHours(((v.Hour + delta) % 24 + 24) % 24) + TimeSpan.FromMinutes(v.Minute)
                                                    : v.Date + TimeSpan.FromHours(v.Hour) + TimeSpan.FromMinutes(((v.Minute + delta) % 60 + 60) % 60);
        SetFromPicker(next);
    }

    private void TimeEdited()
    {
        if (syncing) return;
        var v = value ?? NowLocal.Date + DefaultTime();
        if (!int.TryParse(hourBox.Text.Trim(), NumberStyles.None, culture, out var h) || !int.TryParse(minuteBox.Text.Trim(), NumberStyles.None, culture, out var m) || m is < 0 or > 59) { LoadTime(); return; }
        if (ScheduleText.Uses12Hour(culture)) { if (h is < 1 or > 12) { LoadTime(); return; } h = h % 12 + (v.Hour >= 12 ? 12 : 0); }
        else if (h is < 0 or > 23) { LoadTime(); return; }
        SetFromPicker(v.Date + new TimeSpan(h, m, 0));
    }

    private void SetFromPicker(DateTime v)
    {
        value = new DateTime(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, DateTimeKind.Unspecified);
        Refresh(rewriteText: true);
    }

    private void Reparse()
    {
        if (ScheduleText.TryParse(box.Text, culture, out var parsed)) value = parsed; else value = null;
        Refresh(rewriteText: false);
    }

    private void Refresh(bool rewriteText)
    {
        syncing = true;
        try
        {
            if (rewriteText) box.Text = value is { } v ? ScheduleText.Format(v, culture) : box.Text;
            LoadTime();
            if (popup.IsOpen && value is { } shown) { calendar.Today = () => NowLocal.Date; calendar.Show(culture, shown, NowLocal.Date); }
            var text = box.Text;
            if (string.IsNullOrWhiteSpace(text)) { words.Text = ""; ShowError(null); }
            else
            {
                var check = ScheduleText.Check(text, culture, Zone, Now());
                words.Text = value is { } ok && check.Error is null ? ScheduleText.Words(ok, culture, NowLocal.Date) : "";
                ShowError(check.Error);
            }
        }
        finally { syncing = false; }
        ValueChanged?.Invoke();
    }

    private void LoadTime()
    {
        bool wasSyncing = syncing; syncing = true;
        var v = value ?? NowLocal.Date + DefaultTime();
        bool twelve = ScheduleText.Uses12Hour(culture);
        hourBox.Text = twelve ? (v.Hour % 12 == 0 ? 12 : v.Hour % 12).ToString(culture) : v.Hour.ToString("00", culture);
        minuteBox.Text = v.Minute.ToString("00", culture);
        ampm.Visibility = twelve ? Visibility.Visible : Visibility.Collapsed;
        ampm.Content = v.Hour >= 12 ? culture.DateTimeFormat.PMDesignator : culture.DateTimeFormat.AMDesignator;
        if (ampm.Content is string s && s.Length == 0) ampm.Content = v.Hour >= 12 ? "PM" : "AM";
        syncing = wasSyncing;
    }

    private void ShowError(string? message)
    {
        error.Text = message ?? ""; error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
    }

    private void RebuildQuick()
    {
        quick.Children.Clear();
        void Add(string label, Func<DateTime, DateTime> make)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 3, 8, 3) };
            b.Click += (_, _) => SetFromPicker(make(NowLocal));
            quick.Children.Add(b);
        }
        Add(Loc.T("schedule.quick.hour"), ScheduleText.InOneHour);
        Add(Loc.T("schedule.quick.tonight", new DateTime(2000, 1, 1, 23, 0, 0).ToString(culture.DateTimeFormat.ShortTimePattern, culture)), ScheduleText.Tonight);
        Add(Loc.T("schedule.quick.tomorrow", new DateTime(2000, 1, 1, 8, 0, 0).ToString(culture.DateTimeFormat.ShortTimePattern, culture)), ScheduleText.TomorrowMorning);
    }

    /// <summary>Puts the keyboard focus in the text field (used when a dialog reports a schedule problem).</summary>
    public new bool Focus() => box.Focus();
}
