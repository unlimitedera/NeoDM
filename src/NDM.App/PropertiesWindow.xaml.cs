using System.Windows;

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
        string size = row.TotalBytes > 0 ? $"{DownloadRow.FormatBytes(row.TotalBytes)} ({row.TotalBytes:N0} bytes)" : row.IsComplete ? DownloadRow.FormatBytes(row.DownloadedBytes) : "Unknown";
        var list = new List<PropertyField>
        {
            new("Address (URL)", row.Url),
            new("Referer", string.IsNullOrWhiteSpace(row.Referer) ? "None" : row.Referer!),
            new("Size", size),
            new("Downloaded", $"{DownloadRow.FormatBytes(row.DownloadedBytes)} ({row.Percent})"),
            new("Save path", row.FilePath),
            new("Status", row.Status + (string.IsNullOrWhiteSpace(row.Error) ? "" : ": " + row.Error)),
            new("Added", row.AddedAt is { } added ? added.LocalDateTime.ToString("f") : "Unknown (added before dates were recorded)")
        };
        if (row.ScheduledAt is { } at) list.Add(new("Scheduled", at.LocalDateTime.ToString("f")));
        return list;
    }
    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_url); } catch (System.Runtime.InteropServices.ExternalException) { }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
