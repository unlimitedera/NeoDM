using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using NDM.App.Localization;
using NDM.Core;
namespace NDM.App;

public static class DownloadDialogValidation
{
    public static DateTimeOffset ParseSchedule(string text)
    {
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new UserMessageException(Loc.T("validation.datetime_format"));
        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.Local;
        if (zone.IsInvalidTime(date) || zone.IsAmbiguousTime(date))
            throw new UserMessageException(Loc.T("validation.datetime_ambiguous"));
        var result = new DateTimeOffset(date, zone.GetUtcOffset(date));
        if (result <= DateTimeOffset.Now) throw new UserMessageException(Loc.T("validation.datetime_future"));
        return result;
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
    public JobOptionsWindow(DownloadManager manager, string id, bool schedule)
    {
        _manager = manager; _id = id; _schedule = schedule;
        InitializeComponent();
        Title = Heading.Text = Loc.T(schedule ? "joboptions.title.schedule" : "joboptions.title.limit");
        InputLabel.Content = Loc.T(schedule ? "common.label.local_date_and_time_yyyy_mm" : "joboptions.label.new_limit");
        Help.Text = Loc.T(schedule ? "joboptions.help.schedule" : "joboptions.help.limit");
        AutomationProperties.SetName(ValueInput, Loc.T(schedule ? "common.a11y.scheduled_local_date_and_time" : "joboptions.a11y.new_limit"));
        StartNowButton.Visibility = schedule ? Visibility.Visible : Visibility.Collapsed;
        var job = manager.Jobs.FirstOrDefault(j => j.Id == id);
        if (schedule && job?.ScheduledAt > DateTimeOffset.Now) ValueInput.Text = job.ScheduledAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Loaded += (_, _) => { ValueInput.Focus(); ValueInput.SelectAll(); };
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_manager.Jobs.Any(j => j.Id == _id && j.Status != "Completed")) throw new InvalidOperationException(Loc.T("joboptions.error.not_editable"));
            if (_schedule) _manager.Schedule(_id, DownloadDialogValidation.ParseSchedule(ValueInput.Text));
            else _manager.SetSpeedLimit(_id, DownloadDialogValidation.ParseSpeed(ValueInput.Text));
            DialogResult = true;
        }
        catch (FormatException ex) { ValidationError.Text = ex.Message; ValueInput.Focus(); }
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
