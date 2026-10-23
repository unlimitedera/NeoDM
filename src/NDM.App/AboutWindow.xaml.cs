using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>One released version from CHANGELOG.md: heading, date and its sections with bullet lines.</summary>
public sealed record ChangelogRelease(string Version, string Date, IReadOnlyList<(string Section, IReadOnlyList<string> Items)> Sections);

/// <summary>Reads the embedded CHANGELOG.md (Keep a Changelog layout) and the assembly version for the About window.</summary>
public static class AboutInfo
{
    public static string CreatorText => Loc.T("about.text.neodm_is_an_open_source_project");
    public static string LicenseLine => Loc.T("about.text.released_under_the_mit_license");

    public static string Version
    {
        get
        {
            var asm = typeof(AboutInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    public static string ReadChangelogText()
    {
        using var stream = typeof(AboutInfo).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Releases, latest version first. Unparseable headings are ignored.</summary>
    public static IReadOnlyList<ChangelogRelease> ParseChangelog(string text)
    {
        var releases = new List<(Version Key, ChangelogRelease Release)>();
        string? version = null, date = null, section = "";
        var sections = new List<(string, IReadOnlyList<string>)>(); var items = new List<string>();
        void FlushSection() { if (version is not null && items.Count > 0) sections.Add((section ?? "", items.ToArray())); items = new List<string>(); }
        void FlushRelease()
        {
            FlushSection();
            if (version is not null && System.Version.TryParse(version, out var key)) releases.Add((key, new ChangelogRelease(version, date ?? "", sections.ToArray())));
            sections = new List<(string, IReadOnlyList<string>)>(); version = null; date = null; section = "";
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            var heading = Regex.Match(line, @"^##\s+\[(?<v>\d+\.\d+\.\d+)\]\s*(?:-\s*(?<d>.+))?$");
            if (heading.Success) { FlushRelease(); version = heading.Groups["v"].Value; date = heading.Groups["d"].Success ? heading.Groups["d"].Value.Trim() : ""; continue; }
            if (version is null) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal)) { FlushSection(); section = line[4..].Trim(); continue; }
            if (line.StartsWith("- ", StringComparison.Ordinal)) items.Add(line[2..].Trim());
            else if (line.StartsWith("  ", StringComparison.Ordinal) && items.Count > 0 && line.Trim().Length > 0) items[^1] += " " + line.Trim();
        }
        FlushRelease();
        return releases.OrderByDescending(r => r.Key).Select(r => r.Release).ToArray();
    }
}

public partial class AboutWindow : Window
{
    /// <summary>The update controller used by the button; null in tests that do not set one up.</summary>
    public UpdateController? Updates { get; set; } = (Application.Current as App)?.Updates;
    /// <summary>About is modal, which would disable the modeless guide: close About first, then open the guide.</summary>
    private void Store_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(ExtensionGuide.StoreUrl);
    private void InstallGuide_Click(object sender, RoutedEventArgs e) { Close(); ExtensionGuideWindow.ShowOrActivate(); }
    private void Repo_Click(object sender, RoutedEventArgs e) => UrlOpener.Open(UpdateChecker.RepositoryUrl);
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (Updates is null) { UpdateStatus.Text = Loc.T("about.status.unavailable"); return; }
        CheckUpdatesButton.IsEnabled = false; UpdateStatus.Text = Loc.T("about.status.checking");
        try
        {
            var result = await Updates.CheckNowAsync(text => UpdateStatus.Text = text);
            if (result?.Outcome == UpdateOutcome.Available) UpdateStatus.Text = Loc.T("about.status.available");
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = Loc.T("about.version", AboutInfo.Version);
        var releases = AboutInfo.ParseChangelog(AboutInfo.ReadChangelogText());
        if (releases.Count == 0) ChangelogPanel.Children.Add(new TextBlock { Text = Loc.T("about.no_notes"), FlowDirection = Loc.FlowDirection, Style = (Style)FindResource("Muted") });
        foreach (var release in releases)
        {
            var title = new TextBlock { Text = release.Version + (release.Date.Length > 0 ? "  \u2022  " + release.Date : ""), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 4) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            ChangelogPanel.Children.Add(title);
            foreach (var (name, entries) in release.Sections)
            {
                if (name.Length > 0) ChangelogPanel.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("Muted"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
                foreach (var entry in entries)
                {
                    var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                    var bullet = new TextBlock { Text = "\u2022" }; bullet.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
                    var text = new TextBlock { Text = entry.Replace("`", ""), TextWrapping = TextWrapping.Wrap }; text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
                    Grid.SetColumn(text, 1); row.Children.Add(bullet); row.Children.Add(text);
                    ChangelogPanel.Children.Add(row);
                }
            }
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
