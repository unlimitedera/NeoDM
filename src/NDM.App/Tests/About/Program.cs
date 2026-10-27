using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NDM.App;

internal sealed class TestApp : NDM.App.App
{
    public TestApp()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Light.xaml") });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Controls.xaml") });
    }
    protected override void OnStartup(StartupEventArgs e) { }
}

/// <summary>Help > About window: version from the assembly, creator text, MIT line and the embedded changelog (latest first).</summary>
internal static class AboutTests
{
    private static int passed;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new TestApp();
            var assemblyVersion = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var root = AppContext.BaseDirectory;
            while (root is not null && !File.Exists(Path.Combine(root, "Directory.Build.props"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
            var props = File.ReadAllText(Path.Combine(root!, "Directory.Build.props"));
            var propsVersion = Regex.Match(props, @"<Version>(\d+\.\d+\.\d+)</Version>").Groups[1].Value;
            Check(assemblyVersion == propsVersion, "Assembly informational version equals Directory.Build.props version: " + assemblyVersion);
            Check(typeof(MainWindow).Assembly.GetName().Version!.ToString(3) == propsVersion, "Assembly version matches too");

            var window = new AboutWindow { ShowInTaskbar = false };
            window.Show(); window.UpdateLayout();
            string Text(string name) => window.FindName(name) is TextBox tb ? tb.Text : ((TextBlock)window.FindName(name)).Text;
            Check(Text("AppName") == "NeoDM", "Shows the app name");
            Check(Text("VersionText") == "Version " + assemblyVersion, "Shows 'Version <assembly version>': " + Text("VersionText"));
            Check(Text("CreatorText") == "NeoDM is an open source project created by unlimitedera.", "Shows the exact creator text");
            Check(Text("LicenseText").Contains("MIT"), "Shows the MIT license line");
            Check(window.Icon is not null && Descendants<Image>(window).Any(i => i.Source is not null), "Has an icon in the window and the header");
            Check(window.FindName("ChangelogScroll") is ScrollViewer { VerticalScrollBarVisibility: ScrollBarVisibility.Auto }, "What's new is scrollable");

            var all = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
            Check(all.Contains("What's new"), "Has a What's new section");
            int i110 = all.FindIndex(t => t.StartsWith("1.1.0")), i100 = all.FindIndex(t => t.StartsWith("1.0.0"));
            Check(i110 >= 0 && i100 > i110, "Changelog lists 1.1.0 before 1.0.0 (latest first)");
            Check(all[i110].Contains("2026-10-15"), "Release date is shown");
            Check(all.Any(t => t.Contains("Torrents are part of the normal product")) && all.Any(t => t.Contains("Segmented download engine")), "Changelog entries of both releases are rendered");
            Check(!all.Any(t => t.Contains('`')), "Markdown code ticks are not shown");

            var parsed = AboutInfo.ParseChangelog("## [1.0.0] - 2026-01-01\n### Added\n- a\n## [1.2.0] - 2026-02-02\n### Fixed\n- b\n  continued\n");
            Check(parsed.Count == 2 && parsed[0].Version == "1.2.0" && parsed[0].Sections[0].Items[0] == "b continued", "Parser sorts latest first and joins wrapped bullets");

            var light = ((SolidColorBrush)window.Background).Color;
            app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("pack://application:,,,/NeoDM;component/Themes/Dark.xaml") };
            window.UpdateLayout();
            Check(((SolidColorBrush)window.Background).Color != light, "Window follows the theme (dark differs from light)");
            window.Close();
            Console.WriteLine($"About tests passed: {passed}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
