using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using NDM.App.Localization;
using NDM.Core;
namespace NDM.App;

public static class DownloadDialogValidation
{
    /// <summary>Reads a start time typed or pasted as text: the culture's short date and time, or yyyy-MM-dd HH:mm. Refuses empty, unreadable, past and non-existent or repeated (clock change) times with a plain message.</summary>
    public static DateTimeOffset ParseSchedule(string text)
    {
        var check = ScheduleText.Check(text, ScheduleText.Gregorian(CultureInfo.CurrentCulture), TimeZoneInfo.Local, DateTimeOffset.Now);
        return check.At ?? throw new UserMessageException(check.Error ?? Loc.T("schedule.error.empty"));
    }
    public static long? ParseSpeed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > long.MaxValue / 1024)
            throw new UserMessageException(Loc.T("validation.speed"));
        return checked(value * 1024);
    }
}

public partial class JobOptionsWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly string _id;
    private readonly bool _schedule;
    /// <summary>The schedule control of the scheduler window; null in the speed limit window.</summary>
    public SchedulePicker? Picker { get; }
    public JobOptionsWindow(DownloadManager manager, string id, bool schedule)
    {
        _manager = manager; _id = id; _schedule = schedule;
        InitializeComponent();
        Title = Heading.Text = Loc.T(schedule ? "joboptions.title.schedule" : "joboptions.title.limit");
        if (schedule) { Height = 480; MinHeight = 440; } // room for the plain-words line and an inline error under the date field without a scroll bar
        InputLabel.Content = Loc.T(schedule ? "schedule.label" : "joboptions.label.new_limit");
        Help.Text = Loc.T(schedule ? "joboptions.help.schedule" : "joboptions.help.limit");
        AutomationProperties.SetName(ValueInput, Loc.T(schedule ? "common.a11y.scheduled_local_date_and_time" : "joboptions.a11y.new_limit"));
        StartNowButton.Visibility = schedule ? Visibility.Visible : Visibility.Collapsed;
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
if (schedule)
        {
            // The scheduler window uses the shared schedule control (calendar, time, quick buttons, typed text) instead of a plain text box.
            Picker = new SchedulePicker();
            var panel = (System.Windows.Controls.Panel)ValueInput.Parent;
            panel.Children.Insert(panel.Children.IndexOf(ValueInput) + 1, Picker);
            ValueInput.Visibility = Visibility.Collapsed;
            InputLabel.Target = Picker.Box;
            if (job?.ScheduledAt > DateTimeOffset.Now) Picker.Text = job.ScheduledAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Loaded += (_, _) => Picker.Focus();
        }
        else Loaded += (_, _) => { ValueInput.Focus(); ValueInput.SelectAll(); };
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_manager.Jobs.Any(j => j.Id == _id && j.Status != "Completed")) throw new InvalidOperationException(Loc.T("joboptions.error.not_editable"));
            if (_schedule) _manager.Schedule(_id, Picker!.Require());
            else _manager.SetSpeedLimit(_id, DownloadDialogValidation.ParseSpeed(ValueInput.Text));
            DialogResult = true;
        }
        catch (FormatException ex) { ValidationError.Text = _schedule ? "" : ex.Message; if (_schedule) Picker!.Focus(); else ValueInput.Focus(); }
        catch (Exception) { ValidationError.Text = Loc.T("joboptions.error.save_failed"); }
    }
    private void StartNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_manager.Jobs.Any(j => j.Id == _id && j.Status != "Completed")) throw new InvalidOperationException();
            _manager.Schedule(_id, DateTimeOffset.UtcNow); DialogResult = true;
        }
        catch (Exception) { ValidationError.Text = Loc.T("joboptions.error.start_failed"); }
    }
}
