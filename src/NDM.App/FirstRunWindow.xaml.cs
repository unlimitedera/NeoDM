// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NDM.App.Localization;

namespace NDM.App;

/// <summary>2.0.0: the welcome shown once on a new install (and from Help > Welcome and setup): the save folder, whether the browser extension is connected, and a theme. Skippable; it never comes back by itself.</summary>
public partial class FirstRunWindow : Window
{
    private readonly AppSettings _settings; private readonly string _state; private readonly string _savedTheme; private readonly string? _savedAccent;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _filling;
    /// <summary>True when Finish saved the choices (false for Skip and for closing the window).</summary>
    public bool Finished { get; private set; }
    /// <summary>Opens the extension install guide (tests replace it).</summary>
    public static Action OpenGuide { get; set; } = () => ExtensionGuideWindow.ShowOrActivate();
    /// <summary>Tests choose the folder without a dialog.</summary>
    /// <summary>How many times the window was asked for (tests count it; in test mode no window is shown).</summary>
    public static int Requested { get; private set; }
    public static Func<string?>? FolderPicker { get; set; }

    public FirstRunWindow(AppSettings settings, string stateDirectory)
    {
        _settings = settings; _state = stateDirectory; _savedTheme = ThemeCatalog.Normalize(settings.Theme); _savedAccent = settings.AccentOverride;
        InitializeComponent();
        FolderBox.Text = settings.DownloadDirectory;
        _filling = true;
        foreach (var t in ThemeCatalog.All) ThemeBox.Items.Add(new ComboBoxItem { Content = Loc.T("theme." + t.Id.ToLowerInvariant() + ".name"), Tag = t.Id });
        ThemeBox.SelectedIndex = Math.Max(0, ThemeCatalog.All.Select(t => t.Id).ToList().IndexOf(_savedTheme));
        _filling = false;
        RefreshExtension();
        _timer.Tick += (_, _) => RefreshExtension(); _timer.Start();
        Closed += (_, _) => { _timer.Stop(); if (!Finished) RevertTheme(); };
    }

    /// <summary>At the first start: marks the setup as seen (so it never repeats, even when the app is closed from here) and shows the window.</summary>
    public static void RunAtStart(Window? owner, AppSettings settings, string state)
    {
        if (settings.SetupDone) return;
        settings.SetupDone = true;
        try { settings.Save(state); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        ShowFor(owner, settings, state);
    }

    public static FirstRunWindow? ShowFor(Window? owner, AppSettings settings, string state)
    {
        Requested++;
        if (Dialogs.TestMode) return null;
        var window = new FirstRunWindow(settings, state);
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.ShowDialog();
        return window;
    }

    private void RefreshExtension()
    {
        var seen = _settings.BrowserExtensions;
        ExtensionState.Text = seen.Count > 0 ? Loc.T("firstrun.ext.connected", string.Join(", ", seen.Select(b => b.Browser + (string.IsNullOrEmpty(b.Version) ? "" : " " + b.Version)))) : Loc.T("firstrun.ext.waiting");
    }

    private string ChosenTheme => (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _savedTheme;
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Application.Current is not { } app) return;
        ThemeManager.Apply(app, ChosenTheme, _savedAccent);
    }
    private void RevertTheme() { if (Application.Current is { } app) ThemeManager.Apply(app, _savedTheme, _savedAccent); }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        string? picked;
        if (FolderPicker is { } pick) picked = pick();
        else
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("options.picker.destination"), Multiselect = false };
            if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
            picked = dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        if (picked is not null) FolderBox.Text = picked;
    }
    private void Guide_Click(object sender, RoutedEventArgs e) => OpenGuide();
    private void Skip_Click(object sender, RoutedEventArgs e) { RevertTheme(); Close(); }

    /// <summary>Saves the folder and the theme, then closes. A folder that cannot be used stays on the page with a message.</summary>
    public void Finish()
    {
        ErrorText.Text = "";
        string folder = FolderBox.Text.Trim();
        try
        {
            if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException();
            folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
            using (new FileStream(Path.Combine(folder, ".ndm-write-test-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var updated = _settings.Copy(); updated.DownloadDirectory = folder; updated.Theme = ChosenTheme; updated.SetupDone = true;
            updated.Save(_state); _settings.CopyFrom(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { ErrorText.Text = Loc.T("firstrun.folder.error"); return; }
        Finished = true; Close();
    }
    private void Finish_Click(object sender, RoutedEventArgs e) => Finish();
}
