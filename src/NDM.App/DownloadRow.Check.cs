using NDM.App.Localization;

namespace NDM.App;

/// <summary>1.11.0: the selection checkbox of a row. Kept on the row object, which the list updates in place, so a refresh never loses it.</summary>
public sealed partial class DownloadRow
{
    private bool isChecked;
    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (isChecked == value) return;
            isChecked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
            CheckedChanged?.Invoke(this);
        }
    }
    public event Action<DownloadRow>? CheckedChanged;
    /// <summary>Screen reader name of the row's checkbox.</summary>
    public string SelectName => Loc.T("main.a11y.select_row", FileName);
}
