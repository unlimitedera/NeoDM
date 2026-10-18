using System.Windows;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>One entry in the remove confirmation: what it is and what exists on disk for it.</summary>
public sealed record RemoveItem(string Name, string Detail, int DiskFiles, bool IsTorrent);

/// <summary>Themed confirmation for removing downloads. The delete-from-disk option is always opt-in per dialog.</summary>
public partial class RemoveConfirmWindow : Window
{
    private readonly IReadOnlyList<RemoveItem> _items;
    public bool DeleteFiles => DeleteFilesBox.IsChecked == true;
    public RemoveConfirmWindow(IReadOnlyList<RemoveItem> items, bool deleteFiles = false)
    {
        _items = items;
        InitializeComponent();
        int n = items.Count;
        Heading.Text = Loc.Plural("remove.heading", n);
        bool torrents = items.Any(i => i.IsTorrent);
        Explain.Text = Loc.T("remove.explain") + (torrents ? " " + Loc.T("remove.explain_torrents") : "");
        ItemList.ItemsSource = items;
        DeleteFilesBox.IsChecked = deleteFiles;
        DeleteFiles_Changed(this, new RoutedEventArgs());
    }
    private void DeleteFiles_Changed(object sender, RoutedEventArgs e)
    {
        if (DeleteWarning is null || RemoveButton is null) return;
        int files = _items.Sum(i => Math.Max(0, i.DiskFiles));
        bool on = DeleteFiles;
        DeleteWarning.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DeleteWarning.Text = files == 0 && !_items.Any(i => i.IsTorrent) ? Loc.T("remove.warning.none")
            : _items.Any(i => i.IsTorrent) ? Loc.T("remove.warning.torrent")
            : Loc.Plural("remove.warning.files", files);
        RemoveButton.Content = Loc.T(on ? "remove.btn.remove_delete" : "remove.btn.remove");
        // With the destructive option on, Enter must not confirm by accident.
        RemoveButton.IsDefault = !on; CancelButton.IsDefault = on;
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } }
    private void Cancel_Click(object sender, RoutedEventArgs e) { try { DialogResult = false; } catch (InvalidOperationException) { Close(); } }
}
