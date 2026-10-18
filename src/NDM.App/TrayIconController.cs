using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace NDM.App;

/// <summary>Where the controller puts the icon and the tooltip. The real one wraps the notification icon; tests use a fake.</summary>
public interface ITrayTarget
{
    void SetIcon(System.Drawing.Icon icon);
    void SetTooltip(string text);
}

/// <summary>
/// Keeps the tray icon and tooltip in step with the download activity. The icon is redrawn only when its look changes (count bucket, icon size,
/// accent colours, setting), the tooltip at most once per second. The previous icon handle is destroyed as soon as the new one is set.
/// Call every method on the UI thread.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly IReadOnlyList<BitmapSource> _frames;
    private readonly ITrayTarget _target;
    private readonly Func<bool> _showCount;
    private readonly Func<TrayBadgeColors> _colors;
    private readonly Func<int> _iconSize;
    private readonly Func<DateTime> _now;
    private TrayIconHandle? _current;
    private (int Bucket, int Size, TrayBadgeColors Colors, bool Check)? _drawn;
    private string? _tooltip;
    private DateTime _tooltipAt = DateTime.MinValue;
    private (int Downloading, int Seeding, double Speed, int Unseen) _last;
    private bool _disposed;

    public int IconRenders { get; private set; }
    public int TooltipSets { get; private set; }
    public string? Tooltip => _tooltip;

    public TrayIconController(IReadOnlyList<BitmapSource> frames, ITrayTarget target, Func<bool> showCount, Func<TrayBadgeColors> colors, Func<int> iconSize, Func<DateTime>? now = null)
    {
        _frames = frames; _target = target; _showCount = showCount; _colors = colors; _iconSize = iconSize; _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Applies the current activity. <paramref name="downloading"/> counts Downloading, Connecting and Checking items; seeding-only items are not counted in it.</summary>
    public void Update(int downloading, int seeding, double bytesPerSecond, int unseenFinished = 0)
    {
        if (_disposed) return;
        _last = (downloading, seeding, bytesPerSecond, unseenFinished);
        int count = _showCount() ? downloading : 0;
        bool check = downloading <= 0 && unseenFinished > 0; // the count keeps its meaning; the check only shows while nothing is active
        var look = (TrayIconRenderer.Bucket(count), _iconSize(), count > 0 ? _colors() : default, check);
        if (_drawn != look)
        {
            var image = check ? TrayIconRenderer.RenderCheck(_frames, look.Item2) : TrayIconRenderer.Render(_frames, count, count > 0 ? look.Item3 : default, look.Item2);
            var handle = TrayIconRenderer.CreateIcon(image);
            _target.SetIcon(handle.Icon);
            _current?.Dispose(); _current = handle; _drawn = look; IconRenders++;
        }
        string text = TrayIconRenderer.Tooltip(downloading, seeding, bytesPerSecond, null, unseenFinished);
        var now = _now();
        if (text != _tooltip && (_tooltip is null || now - _tooltipAt >= TimeSpan.FromSeconds(1)))
        {
            _target.SetTooltip(text); _tooltip = text; _tooltipAt = now; TooltipSets++;
        }
    }

    /// <summary>Redraws with the last known activity (theme or accent changed).</summary>
    public void Refresh() { _drawn = null; Update(_last.Downloading, _last.Seeding, _last.Speed, _last.Unseen); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _current?.Dispose(); _current = null;
    }
}
