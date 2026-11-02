namespace NDM.App;

/// <summary>Display-only interpolation. Retargeting starts at the current displayed value, never queues.</summary>
public sealed class NumericTransition
{
    private double start, target, started;
    public const double Duration = 0.45;
    public bool Active { get; private set; }
    public double Value(double now)
    {
        if (!Active) return target;
        var t = Math.Clamp((now - started) / Duration, 0, 1);
        if (t >= 1) { Active = false; return target; }
        return start + (target - start) * t;
    }
    public void Set(double value, double now, bool animate, bool monotonic = false)
    {
        var current = Value(now);
        if (!double.IsFinite(value)) value = 0;
        if (!animate || monotonic && value < current) { start = target = value; Active = false; return; }
        start = current; target = value; started = now; Active = Math.Abs(target - start) > 0.001;
    }
}
