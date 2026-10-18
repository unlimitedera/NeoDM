using System.Windows;

using NDM.App.Localization;
namespace NDM.App;

public sealed record PropertyField(string Label, string Value);

/// <summary>Read-only details of one HTTP download: address, referer, size, save path, status and added date.</summary>
public partial class PropertiesWindow : Window
{
    private readonly string _url;
    public PropertiesWindow(DownloadRow row)
    {
        _url = row.Url;
        InitializeComponent(); WindowPlacements.Track(this, "Properties");
        Title2.Text = row.FileName;
        Fields.ItemsSource = Build(row);
    }
    public static IReadOnlyList<PropertyField> Build(DownloadRow row)
    {
        string size = row.TotalBytes > 0 ? Loc.T("complete.size_exact", DownloadRow.FormatBytes(row.TotalBytes), row.TotalBytes) : row.IsComplete ? DownloadRow.FormatBytes(row.DownloadedBytes) : Loc.T("row.unknown");
        var list = new List<PropertyField>
        {
            new(Loc.T("props.field.address"), Loc.Iso(row.Url)),
            new(Loc.T("props.field.referer"), string.IsNullOrWhiteSpace(row.Referer) ? Loc.T("props.none") : Loc.Iso(row.Referer!)),
            new(Loc.T("common.btn.size"), size),
            new(Loc.T("progress.text.downloaded"), Loc.T("props.downloaded_value", DownloadRow.FormatBytes(row.DownloadedBytes), row.Percent)),
            new(Loc.T("props.field.save_path"), Loc.Iso(row.FilePath)),
            new(Loc.T("common.text.status"), row.StatusText + (string.IsNullOrWhiteSpace(row.Error) ? "" : ": " + ErrorMessages.Describe(row.Error))),
            new(Loc.T("props.field.added"), row.AddedAt is { } added ? Loc.DateTime(added, "f") : Loc.T("props.added_unknown"))
        };
        if (row.ScheduledAt is { } at) list.Add(new(Loc.T("status.scheduled"), Loc.DateTime(at, "f")));
        return list;
    }
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_url); } catch (System.Runtime.InteropServices.ExternalException) { }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
