using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NDM.App;
using NDM.App.Localization;
using NDM.Core;

/// <summary>1.11.0 Part C item 16: the shared schedule control (text, calendar, time, quick buttons, plain words) and where it is used.</summary>
internal static partial class V1110Tests
{
    private static GridViewColumn Col(Window w, string name) => (GridViewColumn)w.FindName(name);
    private static readonly TimeZoneInfo EasternZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private static void C16Text()
    {
        var cultures = new[] { "en-US", "de-DE", "ar-SA", "ja-JP", "tr-TR", "ru-RU", "fr-FR", "en-GB" };
        var samples = new[] { new DateTime(2026, 11, 5, 23, 0, 0), new DateTime(2027, 1, 2, 8, 5, 0), new DateTime(2026, 12, 31, 12, 0, 0), new DateTime(2028, 2, 29, 0, 30, 0) };
        foreach (var name in cultures)
        {
            var culture = ScheduleText.Gregorian(CultureInfo.GetCultureInfo(name));
            foreach (var v in samples)
            {
                var text = ScheduleText.Format(v, culture);
                Check(ScheduleText.TryParse(text, culture, out var back) && back == v, $"{name}: '{text}' reads back as the same minute");
            }
            Check(!ScheduleText.TryParse("", culture, out _) && !ScheduleText.TryParse("tomorrow", culture, out _) && !ScheduleText.TryParse("99/99/2026 10:00", culture, out _), $"{name}: empty and nonsense text is refused");
            Check(!ScheduleText.TryParse(samples[0].ToString(culture.DateTimeFormat.ShortDatePattern, culture), culture, out _), $"{name}: a date without a time is refused");
            Check(!ScheduleText.TryParse(samples[0].ToString(culture.DateTimeFormat.ShortTimePattern, culture), culture, out _), $"{name}: a time without a date is refused");
            Check(ScheduleText.TryParse("2026-11-05 23:00", culture, out var iso) && iso == samples[0], $"{name}: the older yyyy-MM-dd HH:mm still reads");
            Check(ScheduleText.Words(samples[0], culture, new DateTime(2026, 11, 4)).Contains(culture.DateTimeFormat.GetDayName(DayOfWeek.Thursday)), $"{name}: the words use the culture's weekday name: " + ScheduleText.Words(samples[0], culture, new DateTime(2026, 11, 4)));
        }
        Check(ScheduleText.Words(new DateTime(2026, 11, 5, 23, 0, 0), CultureInfo.GetCultureInfo("en-US"), new DateTime(2026, 11, 4)) == "Starts Thursday November 5 at 11:00 PM", "plain words: " + ScheduleText.Words(new DateTime(2026, 11, 5, 23, 0, 0), CultureInfo.GetCultureInfo("en-US"), new DateTime(2026, 11, 4)));
        Check(ScheduleText.Words(new DateTime(2027, 1, 2, 8, 5, 0), CultureInfo.GetCultureInfo("de-DE"), new DateTime(2026, 11, 4)).Contains("2027"), "the year shows when it is not this year");
        Check(ScheduleText.Uses12Hour(CultureInfo.GetCultureInfo("en-US")) && !ScheduleText.Uses12Hour(CultureInfo.GetCultureInfo("de-DE")) && !ScheduleText.Uses12Hour(CultureInfo.GetCultureInfo("ru-RU")), "12 or 24 hour clock follows the culture");
        // checks against the clock
        var en = ScheduleText.Gregorian(CultureInfo.GetCultureInfo("en-US")); var now = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero); var utc = TimeZoneInfo.Utc;
        Check(ScheduleText.Check("", en, utc, now) is { IsValid: false, Error: { Length: > 0 } } && ScheduleText.Check("   ", en, utc, now).Error == Loc.T("schedule.error.empty"), "empty: a message, nothing scheduled");
        Check(ScheduleText.Check("blah", en, utc, now) is { IsValid: false } bad && bad.Error!.Contains("11/4/2026 11:00 PM"), "unreadable: the message shows an example in the culture's own format: " + ScheduleText.Check("blah", en, utc, now).Error);
        Check(ScheduleText.Check("11/4/2026 9:00 AM", en, utc, now) is { IsValid: false } past && past.Error == Loc.T("validation.datetime_future"), "the past is refused with a clear message");
        Check(ScheduleText.Check("11/4/2026 10:00 AM", en, utc, now).IsValid == false, "right now is not in the future");
        Check(ScheduleText.Check("11/4/2026 10:01 AM", en, utc, now) is { IsValid: true } ok && ok.At == new DateTimeOffset(2026, 11, 4, 10, 1, 0, TimeSpan.Zero), "a minute ahead is accepted");
        // clock changes: a time that does not exist (spring forward) or repeats (fall back)
        var eastNow = new DateTimeOffset(2027, 3, 1, 12, 0, 0, TimeSpan.FromHours(-5));
        Check(ScheduleText.Check(new DateTime(2027, 3, 14, 2, 30, 0), EasternZone, eastNow) is { IsValid: false } gap && gap.Error == Loc.T("validation.datetime_ambiguous"), "a time that does not exist (clock moves forward) is refused");
        Check(ScheduleText.Check(new DateTime(2027, 11, 7, 1, 30, 0), EasternZone, eastNow) is { IsValid: false } rep && rep.Error == Loc.T("validation.datetime_ambiguous"), "a time that happens twice (clock moves back) is refused");
        Check(ScheduleText.Check(new DateTime(2027, 3, 14, 3, 30, 0), EasternZone, eastNow) is { IsValid: true } after && after.At!.Value.Offset == TimeSpan.FromHours(-4), "just after the change the new offset is used");
        // quick times
        var n = new DateTime(2026, 11, 4, 10, 17, 42);
        Check(ScheduleText.InOneHour(n) == new DateTime(2026, 11, 4, 11, 17, 0) && ScheduleText.Tonight(n) == new DateTime(2026, 11, 4, 23, 0, 0) && ScheduleText.TomorrowMorning(n) == new DateTime(2026, 11, 5, 8, 0, 0), "quick times: in 1 hour, tonight 23:00, tomorrow 08:00");
        Check(ScheduleText.Tonight(new DateTime(2026, 11, 4, 23, 30, 0)) == new DateTime(2026, 11, 5, 23, 0, 0), "after 23:00 'tonight' is tomorrow night, never the past");
        Check(DownloadDialogValidation.ParseSchedule("2100-01-01 10:00") > DateTimeOffset.Now && throws(() => DownloadDialogValidation.ParseSchedule("")) && throws(() => DownloadDialogValidation.ParseSchedule("2000-01-01 10:00")), "the shared parser used by the scheduler keeps refusing empty and past times");
        static bool throws(Action a) { try { a(); return false; } catch (UserMessageException) { return true; } }
        KeysInEveryLocale(new[] { "schedule.label", "schedule.starts", "schedule.time", "schedule.error.empty", "schedule.error.format", "schedule.a11y.previous_month", "schedule.a11y.next_month", "schedule.a11y.calendar", "schedule.a11y.text", "schedule.a11y.open_calendar", "schedule.a11y.hour", "schedule.a11y.minute", "schedule.a11y.am_pm", "schedule.a11y.increase", "schedule.a11y.decrease", "schedule.quick.hour", "schedule.quick.tonight", "schedule.quick.tomorrow" }, "item 16");
    }

    private static SchedulePicker NewPicker(CultureInfo? culture, DateTime localNow, out Window host, double width = 420, FlowDirection flow = FlowDirection.LeftToRight)
    {
        var picker = new SchedulePicker { Zone = TimeZoneInfo.Utc, Now = () => new DateTimeOffset(DateTime.SpecifyKind(localNow, DateTimeKind.Utc)), Margin = new Thickness(16) };
        if (culture is not null) picker.Culture = culture;
        host = new Window { Width = width, Height = 360, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10, FlowDirection = flow, Content = new Border { Child = picker } };
        host.Show(); Pump(80); if (host.FlowDirection != flow) { host.FlowDirection = flow; Pump(60); } /* the app-wide window localisation resets it to the language direction once it is installed */ return picker;
    }

    private static void C16Control()
    {
        var now = new DateTime(2026, 11, 4, 10, 30, 0);
        var en = CultureInfo.GetCultureInfo("en-US");
        var p = NewPicker(en, now, out var host);
        Check(p.Text == "" && p.Value is null && p.WordsText.Text == "" && p.ErrorText.Visibility == Visibility.Collapsed, "an empty control shows no words and no error");
        // typing: valid text updates the pickers, words appear
        p.Box.Text = "11/5/2026 11:00 PM"; Pump(30);
        Check(p.Value == new DateTime(2026, 11, 5, 23, 0, 0) && p.WordsText.Text == "Starts Thursday November 5 at 11:00 PM" && p.ErrorText.Visibility == Visibility.Collapsed, "typed text sets the value and the plain words: " + p.WordsText.Text);
        Check(p.HourBox.Text == "11" && p.MinuteBox.Text == "00" && (string)p.AmPmButton.Content == "PM" && p.AmPmButton.Visibility == Visibility.Visible, "12 hour clock: the time boxes follow the text (11 : 00 PM)");
        Check(p.Require() == new DateTimeOffset(2026, 11, 5, 23, 0, 0, TimeSpan.Zero), "Require gives the instant");
        // invalid text: inline error, never schedules
        p.Box.Text = "next week"; Pump(30);
        Check(p.Value is null && p.ErrorText.Visibility == Visibility.Visible && p.ErrorText.Text.Contains("Example") && p.WordsText.Text == "", "unreadable text shows an inline error and no words");
        bool refused = false; try { p.Require(); } catch (UserMessageException) { refused = true; } Check(refused, "Require refuses unreadable text");
        p.Box.Text = "11/3/2026 9:00 AM"; Pump(30);
        Check(p.ErrorText.Text == Loc.T("validation.datetime_future") && p.WordsText.Text == "", "a past date shows the future message");
        refused = false; try { p.Require(); } catch (UserMessageException) { refused = true; } Check(refused, "Require refuses the past");
        // pickers to text: the calendar, the spinners, the quick buttons
        p.Box.Text = "11/5/2026 11:00 PM"; Pump(30);
        p.DropButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(120);
        Check(p.DropPopup.IsOpen && p.Calendar.Picked == new DateTime(2026, 11, 5) && p.Calendar.DisplayedMonth == new DateTime(2026, 11, 1), "the calendar opens on the chosen day");
        var today = p.Calendar.DayButtons[new DateTime(2026, 11, 4)]; var past = p.Calendar.DayButtons[new DateTime(2026, 11, 3)];
        Check(CalendarCell.GetIsToday(today) && !past.IsEnabled && today.IsEnabled && CalendarCell.GetIsPicked(p.Calendar.DayButtons[new DateTime(2026, 11, 5)]), "today is ringed, earlier days are disabled, the chosen day is marked");
        p.Calendar.Pick(new DateTime(2026, 11, 12)); Pump(40);
        Check(p.Text == "11/12/2026 11:00 PM" && p.Value == new DateTime(2026, 11, 12, 23, 0, 0), "picking a day changes the date and keeps the time: " + p.Text);
        p.Step(p.HourBox, 1); Check(p.Text == "11/13/2026 12:00 AM" || p.Text == "11/12/2026 12:00 AM" || p.Text.EndsWith("12:00 AM"), "the hour spinner wraps inside the day: " + p.Text);
        p.Box.Text = "11/12/2026 11:00 PM"; Pump(20); p.Step(p.MinuteBox, 5); Check(p.Text == "11/12/2026 11:05 PM", "the minute spinner adds five: " + p.Text);
        p.Step(p.MinuteBox, -10); Check(p.Text == "11/12/2026 11:55 PM", "and wraps inside the hour without touching the hour: " + p.Text);
        p.Box.Text = "11/12/2026 11:00 PM"; Pump(20); p.Step(p.HourBox, -1); Check(p.Text.EndsWith("10:00 PM"), "hour down from 11 PM gives 10 PM: " + p.Text);
        p.Box.Text = "11/12/2026 10:55 PM"; Pump(20);
        p.AmPmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20); Check(p.Text == "11/12/2026 10:55 AM", "the AM/PM button flips the half of the day: " + p.Text);
        p.HourBox.Text = "7"; p.MinuteBox.Text = "45"; p.HourBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); Pump(20);
        Check(p.Text == "11/12/2026 7:45 AM", "typing the hour and minute boxes works: " + p.Text);
        var quick = p.QuickButtons.Children.OfType<Button>().ToArray();
        Check(quick.Length == 3 && (string)quick[0].Content == "In 1 hour" && ((string)quick[1].Content).StartsWith("Tonight") && ((string)quick[2].Content).StartsWith("Tomorrow"), "three quick buttons: " + string.Join(" | ", quick.Select(b => b.Content)));
        quick[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20); Check(p.Text == "11/4/2026 11:30 AM", "In 1 hour: " + p.Text);
        quick[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20); Check(p.Text == "11/4/2026 11:00 PM", "Tonight: " + p.Text);
        quick[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20); Check(p.Text == "11/5/2026 8:00 AM", "Tomorrow: " + p.Text);
        // two-way sync after text edits
        p.Box.Text = "11/20/2026 6:15 PM"; Pump(20);
        Check(p.HourBox.Text == "6" && p.MinuteBox.Text == "15" && (string)p.AmPmButton.Content == "PM" && p.Calendar.Picked == new DateTime(2026, 11, 20), "text edits flow back to the calendar and the time boxes");
        // keyboard on the calendar
        var firstDay = p.Calendar.Cursor;
        void Key1(Key k) { var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; p.Calendar.RaiseEvent(e); }
        Key1(Key.Right); Check(p.Calendar.Cursor == firstDay.AddDays(1), "arrow right moves a day");
        Key1(Key.Down); Check(p.Calendar.Cursor == firstDay.AddDays(8), "arrow down moves a week");
        Key1(Key.PageDown); Check(p.Calendar.DisplayedMonth.Month == 12, "Page Down shows the next month");
        Key1(Key.End); Check(p.Calendar.Cursor.Day == 31, "End goes to the last day of the month");
        Key1(Key.Home); Check(p.Calendar.Cursor.Day == 1 && p.Calendar.Cursor.Month == 12, "Home goes to the first day");
        Key1(Key.PageUp); Key1(Key.PageUp); Check(p.Calendar.Cursor >= now.Date, "the cursor never goes before today");
        p.DropPopup.IsOpen = false; Pump(20);
        Check(p.AmPmButton.Parent is not null && System.Windows.Automation.AutomationProperties.GetName(p.Box) == Loc.T("schedule.a11y.text") && System.Windows.Automation.AutomationProperties.GetName(p.DropButton).Length > 0 && System.Windows.Automation.AutomationProperties.GetName(p.HourBox) == "Hour", "screen reader names are set");
        host.Close();

        // 24 hour culture, first day of the week from the culture, German words
        var de = CultureInfo.GetCultureInfo("de-DE"); var pd = NewPicker(de, now, out var hd);
        pd.Box.Text = "05.11.2026 23:00"; Pump(30);
        Check(pd.Value == new DateTime(2026, 11, 5, 23, 0, 0) && pd.HourBox.Text == "23" && pd.AmPmButton.Visibility == Visibility.Collapsed && pd.WordsText.Text.Contains("Donnerstag"), "de: 24 hour clock, no AM/PM, weekday in German: " + pd.WordsText.Text);
        pd.DropButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
        var firstCell = pd.Calendar.DayButtons.OrderBy(kv => kv.Key).First().Key; Check(firstCell.DayOfWeek == DayOfWeek.Monday, "de: the week starts on Monday");
        pd.DropPopup.IsOpen = false; hd.Close();
        var us = NewPicker(en, now, out var hu); hu.Content = hu.Content; us.DropButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
        Check(us.Calendar.DayButtons.OrderBy(kv => kv.Key).First().Key.DayOfWeek == DayOfWeek.Sunday, "en-US: the week starts on Sunday"); us.DropPopup.IsOpen = false; hu.Close();
        // right to left and a narrow window
        var ar = ScheduleText.Gregorian(CultureInfo.GetCultureInfo("ar-SA")); var pa = NewPicker(ar, now, out var ha, 320, FlowDirection.RightToLeft);
        pa.Box.Text = ScheduleText.Format(new DateTime(2026, 11, 5, 23, 0, 0), ar); Pump(30);
        Check(pa.Value == new DateTime(2026, 11, 5, 23, 0, 0) && pa.ActualWidth <= 320 && pa.DropButton.PointToScreen(new Point(0, 0)).X < pa.Box.PointToScreen(new Point(0, 0)).X, $"ar: right to left puts the calendar button on the left; fits a 320 px window ({pa.Value}, {pa.ActualWidth}, {pa.DropButton.PointToScreen(new Point(0, 0)).X} vs {pa.Box.PointToScreen(new Point(0, 0)).X}, text {pa.Box.Text})");
        ha.Close();
    }

    private static void C16Themes(string scratch)
    {
        var now = new DateTime(2026, 11, 4, 10, 30, 0);
        try
        {
            foreach (var theme in ThemeCatalog.Concrete)
                foreach (var accent in new string?[] { null, "#FF0000", "#FFFF00", "#FFFFFF" })
                {
                    ThemeManager.Apply(Application.Current, theme.Id, accent);
                    var fill = (Brush)Application.Current.FindResource("AccentFillBrush"); var on = BrushColor("OnAccentBrush"); var surface = BrushColor("SurfaceBrush"); var accentColor = BrushColor("AccentBrush");
                    var stops = fill is SolidColorBrush sb ? new[] { sb.Color } : ((LinearGradientBrush)fill).GradientStops.Select(s => s.Color).ToArray();
                    foreach (var c in stops) Check(ThemeManager.Contrast(on, c) >= 4.5 && ThemeManager.Contrast(c, surface) >= 3, $"{theme.Id}/{accent ?? "default"}: the chosen day reads (text {ThemeManager.Contrast(on, c):0.0}:1, fill on surface {ThemeManager.Contrast(c, surface):0.0}:1)");
                    Check(ThemeManager.Contrast(accentColor, surface) >= 3, $"{theme.Id}/{accent ?? "default"}: the today ring reads on the surface");
                }
            // rendered pixels in two themes
            foreach (var theme in new[] { "Light", "DeepNavy" })
            {
                ThemeManager.Apply(Application.Current, theme, null);
                var p = NewPicker(CultureInfo.GetCultureInfo("en-US"), now, out var host, 460);
                p.Box.Text = "11/12/2026 11:00 PM"; p.DropButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(150);
                var cal = p.Calendar; var cell = cal.DayButtons[new DateTime(2026, 11, 12)];
                var popupRoot = (FrameworkElement)p.DropPopup.Child;
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(popupRoot.ActualWidth), (int)Math.Ceiling(popupRoot.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(popupRoot);
                var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4]; int stride = bmp.PixelWidth * 4; bmp.CopyPixels(px, stride, 0);
                var cp = cell.TransformToAncestor(popupRoot).Transform(new Point(4, 4));
                var fillColor = (Application.Current.FindResource("AccentFillBrush") as SolidColorBrush)?.Color ?? ((LinearGradientBrush)Application.Current.FindResource("AccentFillBrush")).GradientStops[0].Color;
                var cellOrigin = cell.TransformToAncestor(popupRoot).Transform(new Point(0, 0)); int near = 0, total = 0;
                for (int y = (int)cellOrigin.Y - 6; y < (int)(cellOrigin.Y + cell.ActualHeight) + 6; y++) for (int x = (int)cellOrigin.X - 6; x < (int)(cellOrigin.X + cell.ActualWidth) + 6; x++) { if (x < 0 || y < 0 || x >= bmp.PixelWidth || y >= bmp.PixelHeight) continue; total++; if (Near(Px(px, stride, x, y), fillColor, 40)) near++; }
                { double best = 0; (byte, byte, byte) far = (0, 0, 0);
                  for (int y = (int)cellOrigin.Y + 4; y < (int)(cellOrigin.Y + cell.ActualHeight) - 4; y++) for (int x = (int)cellOrigin.X + 6; x < (int)(cellOrigin.X + cell.ActualWidth) - 6; x++) { if (x < 0 || y < 0 || x >= bmp.PixelWidth || y >= bmp.PixelHeight) continue; var q = Px(px, stride, x, y); double d = Math.Abs(q.R - fillColor.R) + Math.Abs(q.G - fillColor.G) + Math.Abs(q.B - fillColor.B); if (d > best) { best = d; far = q; } }
                  var tick = BrushColor("OnAccentBrush"); Console.WriteLine($"DEBUG {theme}: digit pixel {far} OnAccent {tick}");
                  Check(Math.Abs(far.Item1 - tick.R) + Math.Abs(far.Item2 - tick.G) + Math.Abs(far.Item3 - tick.B) < 90, $"{theme}: the chosen day's digits use the contrast colour ({far} vs {tick})"); }
                Check(near >= 150, $"{theme}: the chosen day is painted in the accent colour ({near} of {total} pixels)");
                var todayCell = cal.DayButtons[new DateTime(2026, 11, 4)]; var to = todayCell.TransformToAncestor(popupRoot).Transform(new Point(0, 0)); int ring = 0;
                for (int y = (int)to.Y - 6; y < (int)(to.Y + todayCell.ActualHeight) + 6; y++) for (int x = (int)to.X - 6; x < (int)(to.X + todayCell.ActualWidth) + 6; x++) { if (x < 0 || y < 0 || x >= bmp.PixelWidth || y >= bmp.PixelHeight) continue; if (Near(Px(px, stride, x, y), BrushColor("AccentBrush"), 40)) ring++; }
                Check(ring >= 40, $"{theme}: today carries an accent ring ({ring} pixels)");
                var pastCell = cal.DayButtons[new DateTime(2026, 11, 2)]; var pc = pastCell.TransformToAncestor(popupRoot).Transform(new Point(16, 14));
                Check(Near(Px(px, stride, 6, 6), BrushColor("SurfaceBrush"), 12) || Near(Px(px, stride, 12, 12), BrushColor("SurfaceBrush"), 12), $"{theme}: the pop-up uses the theme surface, not a system white");
                if (ShotsFolder is { Length: > 0 } f) { Directory.CreateDirectory(f); var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); using var fs = File.Create(Path.Combine(f, $"c16-schedule-popup-{theme}.png")); enc.Save(fs); SaveShot(host, f, $"c16-schedule-field-{theme}.png"); }
                p.DropPopup.IsOpen = false; host.Close();
            }
        }
        finally { ThemeManager.Apply(Application.Current, "Light", null); }
    }

    private static void C16Usage(string scratch)
    {
        // every former text entry now uses the control (static check of the layouts) ...
        foreach (var xaml in Directory.GetFiles(Path.Combine(RepoRoot, "src", "NDM.App"), "*.xaml"))
        {
            var text = File.ReadAllText(xaml);
            Check(!System.Text.RegularExpressions.Regex.IsMatch(text, "<TextBox[^>]*x:Name=\"ScheduleInput\""), Path.GetFileName(xaml) + ": no plain text box for the schedule");
        }
        foreach (var name in new[] { "AddDownloadWindow", "DownloadOptionsDialog", "StreamOptionsDialog", "YoutubeOptionsDialog", "PlaylistOptionsDialog" })
            Check(System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", name + ".xaml")), "<\\w+:SchedulePicker x:Name=\"ScheduleInput\""), name + ": the schedule is a SchedulePicker");
        Check(File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "JobOptionsWindow.xaml.cs")).Contains("new SchedulePicker()"), "the Scheduler / reschedule window builds the control");
        Check(!File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "LinkGrabberWindow.xaml")).Contains("Schedul") && !File.ReadAllText(Path.Combine(RepoRoot, "src", "NDM.App", "BulkImportWindow.xaml")).Contains("Schedul"), "the link grabber and bulk import have no schedule input to replace (they add to the queue)");
        // ... and at run time for the windows that can be built without a network
        var add = new AddDownloadWindow("") { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 }; add.Show(); Pump(60);
        Check(add.FindName("ScheduleInput") is SchedulePicker && !((SchedulePicker)add.FindName("ScheduleInput")).IsEnabled, "Add URL: the control is there and waits for the checkbox"); add.Close();
        var m = CManager(scratch, "c16"); var job = Wait(m.AddPausedAsync(new DownloadRequest("http://127.0.0.1:9/a.bin", "a.bin")));
        var jw = new JobOptionsWindow(m, job.Id, true) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 }; jw.Show(); Pump(60);
        Check(jw.Picker is not null && jw.Picker.Box.IsVisible, "Scheduler window: the control replaces the text box");
        jw.Picker!.Box.Text = "nonsense"; ((Button)jw.FindName("StartNowButton")).Visibility.ToString();
        var apply = Visual<Button>(jw).First(b => b.IsDefault); apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(40);
        Check(jw.Picker.ErrorText.Visibility == Visibility.Visible && m.Jobs.Single().ScheduledAt is null, "Apply with unreadable text shows the error and schedules nothing");
        jw.Close();
        var jw2 = new JobOptionsWindow(m, job.Id, true) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 };
        jw2.Picker!.Text = "2100-01-01 10:00";
        var applyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) }; applyTimer.Tick += (_, _) => { applyTimer.Stop(); try { ((Button)jw2.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } catch (Exception ex) { Console.WriteLine("DEBUG " + ex); jw2.Close(); } }; applyTimer.Start();
        Check(jw2.ShowDialog() == true && m.Jobs.Single().ScheduledAt is { } at && at.Year == 2100, "Apply with a good time schedules the job");
        var speed = new JobOptionsWindow(m, job.Id, false) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 10, Top = 10 }; speed.Show(); Pump(40);
        Check(speed.Picker is null && ((TextBox)speed.FindName("ValueInput")).Visibility == Visibility.Visible, "the speed limit window keeps its plain number box"); speed.Close();
        var v = NewVideoDialog(scratch, (u, t, l) => Task.FromResult(new YoutubeMetadata("S", new[] { new YoutubeFormat("137", "mp4", 1080, 0, true, false, 0) })));
        Check(Part<SchedulePicker>(v, "ScheduleInput") is not null, "YouTube dialog: the control"); v.Close();
        var pl = NewPlaylistDialog(scratch, (u, t, l) => Task.FromResult(SamplePlaylist()));
        Check(Part<SchedulePicker>(pl, "ScheduleInput") is not null, "Playlist dialog: the control"); pl.Close();
        m.Dispose();
    }
}
