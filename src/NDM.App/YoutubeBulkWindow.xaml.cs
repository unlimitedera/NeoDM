using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>Several YouTube video links were given at once: open them one by one, or queue them all with one shared quality.</summary>
public partial class YoutubeBulkWindow : Window
{
    public bool QueueAllChosen { get; private set; }
    public YoutubePreset Preset { get; private set; } = new();
    public bool Mp3 { get; private set; }
    public static Func<Window?, int, (bool Queue, YoutubePreset Preset, bool Mp3)?>? Override { get; set; }
    public YoutubeBulkWindow(int count)
    {
        InitializeComponent(); Intro.Text = Loc.T("youtube.bulk_intro", count);
        QualityBox.ItemsSource = new[] { new QualityChoice("best", Loc.T("playlist.quality_best")) }.Concat(new[] { 2160, 1440, 1080, 720, 480, 360 }.Select(h => new QualityChoice(h.ToString(CultureInfo.InvariantCulture), h + "p"))).ToArray();
        QualityBox.DisplayMemberPath = "Label"; QualityBox.SelectedValuePath = "Value"; QualityBox.SelectedIndex = 0;
    }
    private void Mode_Changed(object sender, RoutedEventArgs e) { if (PresetPanel is not null) PresetPanel.IsEnabled = QueueAll.IsChecked == true; }
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        QueueAllChosen = QueueAll.IsChecked == true;
        Preset = new YoutubePreset(AudioOnlyBox.IsChecked == true ? "audio" : QualityBox.SelectedValue as string ?? "best");
        Mp3 = AudioOnlyBox.IsChecked == true && Mp3Box.IsChecked == true;
        DialogResult = true;
    }
    /// <summary>Null = cancelled.</summary>
    public static (bool Queue, YoutubePreset Preset, bool Mp3)? Ask(Window? owner, int count)
    {
        if (Override is { } answer) return answer(owner, count);
        var w = new YoutubeBulkWindow(count) { Owner = owner };
        return w.ShowDialog() == true ? (w.QueueAllChosen, w.Preset, w.Mp3) : null;
    }
}
