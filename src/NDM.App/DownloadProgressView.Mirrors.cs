// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NDM.App.Localization;
using NDM.Core;

namespace NDM.App;

/// <summary>2.0.0: decides when the chooser's connect times are measured again: every 5 seconds, only while the window is open and the section is expanded. The clock is injectable.</summary>
public sealed class MirrorRefreshGate
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private readonly Func<DateTimeOffset> now; private DateTimeOffset? last;
    public MirrorRefreshGate(Func<DateTimeOffset>? clock = null) { now = clock ?? (() => DateTimeOffset.UtcNow); }
    /// <summary>True when a measurement is due. An inactive section (closed window, hidden tab, collapsed) never asks and starts fresh when it becomes active again.</summary>
    public bool Due(bool active)
    {
        if (!active) { last = null; return false; }
        var t = now();
        if (last is null || t - last.Value >= Interval) { last = t; return true; }
        return false;
    }
}

public partial class DownloadProgressView
{
    private string mirrorSignature = "";
    private bool mirrorRefreshing;
    private readonly string mirrorGroup = "mirror-" + Guid.NewGuid().ToString("N");
    /// <summary>The clock of the 5-second refresh (tests replace it).</summary>
    public MirrorRefreshGate MirrorGate { get; set; } = new();
    /// <summary>Asks for the address of a mirror; null = cancelled. Replaceable so tests never open a real window.</summary>
    public Func<string?>? AskMirrorAddress { get; set; }
    public bool MirrorsExpanded => MirrorBody.Visibility == Visibility.Visible;

    private void MirrorToggle_Click(object sender, RoutedEventArgs e) => SetMirrorsExpanded(MirrorToggle.IsChecked == true);
    private void MirrorHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SetMirrorsExpanded(!MirrorsExpanded);
    public void SetMirrorsExpanded(bool open)
    {
        MirrorToggle.IsChecked = open; MirrorBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MirrorGate.Due(false); RefreshMirrors(true);
    }

    private static bool OffersMirrors(DownloadRow row) => !row.IsTorrent && !row.IsStream && !row.IsYoutube && (row.Status != "Completed" || row.Sources.Count > 0);

    /// <summary>Shows or hides the section, rebuilds the list when something changed, and asks for fresh connect times when they are due.</summary>
    private void RefreshMirrors(bool force = false)
    {
        var show = OffersMirrors(Row);
        MirrorSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MirrorToggle.LayoutTransform = Loc.IsRtl ? new ScaleTransform(-1, 1) : Transform.Identity;
        if (!show) return;
        var sources = Row.Sources;
        var signature = string.Join("|", sources.Select(s => $"{s.Url}~{s.State}~{s.Reason}~{s.LatencyMs}~{s.IsActive}~{s.Address}")) + $"|{Row.MirrorChoice}|{Row.ChecksumKnown}|{Row.ChecksumVerified}|{Row.MirrorUnverified}|{Loc.Language}|{Row.ServerViaProxy}|{MirrorsExpanded}";
        if (force || signature != mirrorSignature)
        {
            mirrorSignature = signature;
            if (MirrorsExpanded) BuildMirrorRows(sources);
            var anyChecked = sources.Any(s => !s.IsPrimary && s.State == MirrorState.Verified);
            MirrorNote.Foreground = (Brush)FindResource(Row.MirrorUnverified || (anyChecked && !Row.ChecksumKnown) ? "WarningBrush" : "MutedBrush");
            MirrorNote.Text = Row.ChecksumVerified ? Loc.T("mirror.checked") : Row.MirrorUnverified ? Loc.T("mirror.warn.used_unverified") : anyChecked && !Row.ChecksumKnown ? Loc.T("mirror.warn.unverified") : mirrorNotice ?? "";
            MirrorNote.Visibility = MirrorNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var active = IsVisible && IsLoaded && MirrorsExpanded && sources.Count > 1 && !Row.ServerViaProxy && Row.Status is "Downloading" or "Connecting" or "Paused" or "Queued";
        if (MirrorGate.Due(active) && !mirrorRefreshing)
        {
            mirrorRefreshing = true;
            _ = RefreshLatenciesAsync();
        }
    }

    private string? mirrorNotice;

    private async Task RefreshLatenciesAsync()
    {
        try { await manager.RefreshMirrorLatenciesAsync(id).ConfigureAwait(true); }
        catch (Exception ex) when (ex is KeyNotFoundException or ObjectDisposedException or OperationCanceledException) { }
        finally { mirrorRefreshing = false; }
    }

    private void BuildMirrorRows(IReadOnlyList<MirrorInfo> sources)
    {
        MirrorList.Children.Clear();
        var automatic = Row.MirrorChoice is null;
        var now = sources.FirstOrDefault(s => s.IsActive);
        MirrorList.Children.Add(MirrorRowFor(null, Loc.T("mirror.auto"), null, automatic, true, now is null ? null : Loc.T("mirror.now", Loc.Ltr(now.Host)), null, null, null, false, null));
        foreach (var s in sources)
        {
            var code = (s.Address is null ? null : ServerInfo.CodeOf(s.Address)) ?? s.Geo?.ToUpperInvariant();
            var country = code is null ? null : ServerInfo.CountryName(code);
            var selected = s.IsPrimary ? Row.MirrorChoice == s.Url : Row.MirrorChoice == s.Url;
            var usable = s.State == MirrorState.Verified;
            string? note = s.IsPrimary ? Loc.T("mirror.original")
                : s.State == MirrorState.Rejected ? Loc.T("mirror.reason." + (s.Reason ?? "unreachable"))
                : s.State == MirrorState.Candidate ? Loc.T("mirror.candidate")
                : !Row.ChecksumKnown ? Loc.T("mirror.unverified") : null;
            var latency = s.State == MirrorState.Rejected ? null : LatencyText(s.LatencyMs);
            MirrorList.Children.Add(MirrorRowFor(s.Url, country, code, selected, usable, note, s.Host, latency, s.LatencyMs, !s.IsPrimary, OriginTip(s)));
        }
    }

    private string? LatencyText(int? ms) => Row.ServerViaProxy ? Loc.T("mirror.proxy") : ms is { } m ? Loc.Ltr(Loc.T("mirror.latency", Loc.Number(m))) : "\u2014";
    private static string OriginTip(MirrorInfo s) => s.IsPrimary ? s.Url : s.Url + "\n" + Loc.T(s.Origin switch { MirrorOrigin.Header => "mirror.origin.header", MirrorOrigin.Metalink => "mirror.origin.metalink", _ => "mirror.origin.user" });

    private UIElement MirrorRowFor(string? url, string? title, string? code, bool selected, bool enabled, string? note, string? host, string? latency, int? latencyMs, bool removable, string? tip)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var radio = new RadioButton { GroupName = mirrorGroup, IsChecked = selected, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center, Tag = url, Margin = new Thickness(0, 0, 6, 0), MinHeight = 20 };
        System.Windows.Automation.AutomationProperties.SetName(radio, url is null ? title : (host ?? ""));
        radio.Click += (_, _) => { try { manager.SelectMirror(id, url); } catch (KeyNotFoundException) { } };
        Grid.SetColumn(radio, 0); grid.Children.Add(radio);
        var flag = new Image { Width = 20, Height = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Source = FlagImages.Get(code), Visibility = code is null || FlagImages.Get(code) is null ? Visibility.Hidden : Visibility.Visible };
        RenderOptions.SetBitmapScalingMode(flag, BitmapScalingMode.HighQuality);
        Grid.SetColumn(flag, 1); grid.Children.Add(flag);
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        text.Inlines.Add(new Run(title ?? host ?? "") { FontWeight = url is null ? FontWeights.SemiBold : FontWeights.Normal });
        if (host is not null && title is not null) { var h = new Run("  " + Loc.Ltr(host)); h.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush"); text.Inlines.Add(h); }
        if (note is not null) { var n = new Run("  " + note); n.SetResourceReference(TextElement.ForegroundProperty, enabled || url is null ? "MutedBrush" : "ErrorBrush"); text.Inlines.Add(n); }
        Grid.SetColumn(text, 2); grid.Children.Add(text);
        if (latency is not null)
        {
            var l = new TextBlock { Text = latency, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
            l.SetResourceReference(TextBlock.ForegroundProperty, latencyMs is { } ms && !Row.ServerViaProxy ? ServerInfo.BrushKeyFor(ms) : "MutedBrush");
            Grid.SetColumn(l, 3); grid.Children.Add(l);
        }
        if (removable)
        {
            var remove = new Button { Content = "\u2715", MinWidth = 24, MinHeight = 22, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = Loc.T("mirror.remove"), Tag = url };
            System.Windows.Automation.AutomationProperties.SetName(remove, Loc.T("mirror.remove"));
            remove.Click += (_, _) => { try { manager.RemoveMirror(id, url!); } catch (KeyNotFoundException) { } mirrorSignature = ""; RefreshMirrors(true); };
            Grid.SetColumn(remove, 4); grid.Children.Add(remove);
        }
        return grid;
    }

    private async void AddMirror_Click(object sender, RoutedEventArgs e)
    {
        var address = (AskMirrorAddress ?? (() => PromptForMirror(Window.GetWindow(this))))();
        if (string.IsNullOrWhiteSpace(address)) return;
        MirrorAddResult result;
        try { result = await manager.AddMirrorAsync(id, address).ConfigureAwait(true); }
        catch (KeyNotFoundException) { return; }
        await Dispatcher.InvokeAsync(() =>
        {
            mirrorNotice = result.Added ? Loc.T(result.State == MirrorState.Candidate ? "mirror.add.pending" : "mirror.add.ok")
                : result.Reason switch
                {
                    "address" => Loc.T("mirror.add.bad_address"), "duplicate" => Loc.T("mirror.add.duplicate"), "unsupported" => Loc.T("mirror.add.unsupported"), MirrorReasons.Content => Loc.T("mirror.add.metalink"),
                    var r => Loc.T("mirror.add.rejected", Loc.T("mirror.reason." + (r ?? "unreachable")))
                };
            mirrorSignature = ""; RefreshMirrors(true);
            MirrorNote.Text = mirrorNotice; MirrorNote.Visibility = Visibility.Visible;

        });
    }

    /// <summary>The prompt that asks for a mirror address (used by the button; the window layer of the app shows it).</summary>
    public static string? PromptForMirror(Window? owner)
    {
        if (Dialogs.TestMode) return null;
        var window = new TextPromptWindow(Loc.T("mirror.prompt.title"), Loc.T("mirror.prompt.text"), "",
            text => Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? null : Loc.T("mirror.add.bad_address"));
        PromptWindows.Prepare(window, owner);
        return window.ShowDialog() == true ? window.Value : null;
    }
}
