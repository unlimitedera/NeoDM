using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>Help > Browser extension install guide: a browser selector, the extensions page address, numbered steps with four screenshots and the real extension folder.</summary>
public partial class ExtensionGuideWindow : Window
{
    private static ExtensionGuideWindow? _open;
    /// <summary>Test seam; the default writes to the Windows clipboard (which another program can hold locked for a moment).</summary>
    public static Func<string, bool> SetClipboard { get; set; } = text =>
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(40); }
        }
        return false;
    };

    /// <summary>Test seam: where the browser programs are. Null = App Paths registry key and default folders.</summary>
    public Func<GuideBrowser, string?>? BrowserFinder { get; set; }

    private readonly string _appVersion;
    private readonly ExtensionFolder _folder;
    private readonly Dictionary<string, RadioButton> _browserButtons = new();
    private readonly List<Image> _images = new();
    private TextBlock? _developerModeLine;

    public GuideBrowser Browser { get; private set; } = ExtensionGuide.Chrome;
    public ExtensionFolder Folder => _folder;
    public IReadOnlyDictionary<string, RadioButton> BrowserButtons => _browserButtons;
    public IReadOnlyList<Image> GuideImages => _images;
    public TextBox PathBox { get; private set; } = null!;
    public Button? CopyPathButton { get; private set; }
    public Button? OpenFolderButton { get; private set; }
    public string PendingNotice => PendingText.Text;
    public string StatusMessage => StatusText.Text;
    public string DeveloperModeText => _developerModeLine?.Text ?? "";

    /// <summary>Shows the guide once: a second request raises the open window instead of opening another.</summary>
    public static ExtensionGuideWindow ShowOrActivate()
    {
        if (_open is { IsLoaded: true } open) { WindowActivation.BringToFront(open); return open; }
        var window = new ExtensionGuideWindow();
        _open = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        window.Show();
        WindowActivation.BringToFront(window);
        return window;
    }

    public ExtensionGuideWindow(string? appVersion = null, ExtensionFolder? folder = null)
    {
        _appVersion = appVersion ?? AboutInfo.Version;
        _folder = folder ?? ExtensionGuide.ResolveFolder();
        InitializeComponent(); WindowPlacements.Track(this, "Guide");
        PendingText.Text = ExtensionGuide.PendingApproval;
        IntroText.Text = ExtensionGuide.SameForAllBrowsers + " " + Loc.T("guide.intro_tail");
        BuildBrowserBar();
        BuildSteps();
        SelectBrowser(ExtensionGuide.Chrome);
        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>Language switch: the steps are built in code, so they are built again in the new language.</summary>
    private void OnLanguageChanged()
    {
        PendingText.Text = ExtensionGuide.PendingApproval;
        IntroText.Text = ExtensionGuide.SameForAllBrowsers + " " + Loc.T("guide.intro_tail");
        StepsPanel.Children.Clear(); _developerModeLine = null; _images.Clear();
        BuildSteps();
        SelectBrowser(Browser);
    }

    private void BuildBrowserBar()
    {
        foreach (var browser in ExtensionGuide.Browsers)
        {
            var button = new RadioButton { Content = browser.Name, GroupName = "GuideBrowser", Style = (Style)FindResource("Segment"), Tag = browser };
            AutomationProperties.SetName(button, browser.Name);
            button.Checked += (_, _) => { if (button.Tag is GuideBrowser picked && picked != Browser) SelectBrowser(picked); };
            _browserButtons[browser.Id] = button;
            BrowserBar.Children.Add(button);
        }
    }

    /// <summary>Switches the address and the browser-specific wording.</summary>
    public void SelectBrowser(GuideBrowser browser)
    {
        Browser = browser;
        AddressBox.Text = browser.Url;
        if (_browserButtons.TryGetValue(browser.Id, out var button) && button.IsChecked != true) button.IsChecked = true;
        if (_developerModeLine is not null) _developerModeLine.Text = ExtensionGuide.DeveloperModeLine(browser);
        StatusText.Text = "";
    }

    private void BuildSteps()
    {
        foreach (var step in ExtensionGuide.Steps(_appVersion, ExtensionGuide.Chrome))
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = step.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            foreach (var line in step.Lines)
            {
                var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                if (step.Number == 1) _developerModeLine = text;
                if (line == ExtensionGuide.RemoveWhy) text.Style = (Style)FindResource("Muted");
                body.Children.Add(text);
            }
            if (step.Number == 3) body.Children.Add(BuildFolderRow());
            if (step.Image is not null) body.Children.Add(BuildImage(step.Image, step.Title));
            var row = new Grid { Margin = new Thickness(0, 0, 0, 22) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var number = new TextBlock { Text = step.Number.ToString(), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            number.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
            var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Child = number };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentFillBrush");
            AutomationProperties.SetName(badge, Loc.T("guide.a11y.step", step.Number));
            Grid.SetColumn(body, 1);
            row.Children.Add(badge); row.Children.Add(body);
            StepsPanel.Children.Add(row);
        }
        var trouble = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        trouble.Children.Add(new TextBlock { Text = Loc.T("guide.trouble_heading"), FontSize = 15, FontWeight = FontWeights.SemiBold });
        foreach (var (problem, answer) in ExtensionGuide.Troubleshooting)
        {
            trouble.Children.Add(new TextBlock { Text = problem, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            trouble.Children.Add(new TextBlock { Text = answer, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        }
        StepsPanel.Children.Add(trouble);
    }

    private FrameworkElement BuildFolderRow()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        bool found = _folder.Path is not null;
        PathBox = new TextBox { IsReadOnly = true, Text = found ? _folder.Path! : ExtensionGuide.NotFoundMessage };
        AutomationProperties.SetName(PathBox, Loc.T("guide.a11y.folder_path"));
        if (!found) PathBox.SetResourceReference(Control.ForegroundProperty, "ErrorBrush");
        CopyPathButton = new Button { Content = Loc.T("guide.btn.copy_path"), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsEnabled = found };
        OpenFolderButton = new Button { Content = Loc.T("common.btn.open_folder"), MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), IsEnabled = found };
        CopyPathButton.Click += (_, _) => Say(SetClipboard(_folder.Path!) ? Loc.T("guide.status.path_copied") : Loc.T("guide.status.clipboard_path"));
        OpenFolderButton.Click += (_, _) => { var problem = ExtensionGuide.OpenFolder(_folder.Path); Say(problem.Length == 0 ? Loc.T("guide.status.folder_opened") : problem); };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(CopyPathButton, 1); Grid.SetColumn(OpenFolderButton, 2);
        grid.Children.Add(PathBox); grid.Children.Add(CopyPathButton); grid.Children.Add(OpenFolderButton);
        panel.Children.Add(grid);
        if (_folder.Kind == ExtensionFolderKind.Development)
            panel.Children.Add(new TextBlock { Text = Loc.T("guide.dev_copy"), Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private FrameworkElement BuildImage(string name, string description)
    {
        var image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        try { image.Source = new BitmapImage(ExtensionGuide.ImageUri(name)); } catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException) { }
        AutomationProperties.SetName(image, Loc.T("guide.a11y.screenshot", description));
        _images.Add(image);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Child = image };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return frame;
    }

    private void Say(string text) => StatusText.Text = text;

    private void CopyAddress_Click(object sender, RoutedEventArgs e) => Say(SetClipboard(AddressBox.Text) ? Loc.T("guide.status.address_copied", Browser.Name) : Loc.T("guide.status.clipboard_address"));
    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        var problem = ExtensionGuide.OpenExtensionsPage(Browser, BrowserFinder);
        Say(problem.Length == 0 ? Loc.T("guide.status.opening", Browser.Name) : problem);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
