using NDM.Core.Captcha;

internal static partial class V1162Tests
{
    /// <summary>The absolute value the system gets for a screen point on a 1920x1080 screen.</summary>
    private static string Move(int x, int y) => "move " + (int)((long)x * 65535 / 1919) + "," + (int)((long)y * 65535 / 1079);

    private sealed class FakeFocus : IWindowFocus
    {
        public readonly List<IntPtr> Asked = new();
        public IntPtr Front;
        public int RefuseFirst;
        public bool Restore(IntPtr window) { Asked.Add(window); if (RefuseFirst-- > 0) return false; Front = window; return true; }
    }

    /// <summary>Bringing the browser back: it is looked at first, the set request is checked, and a refusing system is given three tries.</summary>
    private static void Focus()
    {
        var window = new IntPtr(42); var front = IntPtr.Zero; int sets = 0, allows = 0;
        bool Try(int attempts) => WindowFocus.Restore(window, () => front, w => { sets++; front = w; }, () => { allows++; return true; }, attempts, _ => { });
        Check(Try(3) && sets == 1 && allows == 1 && front == window, "not in front: one set request, then verified");
        front = window; sets = 0; allows = 0;
        Check(Try(3) && sets == 0 && allows == 0, "already in front: nothing is asked of the system");
        front = IntPtr.Zero; int calls = 0;
        bool Slow() => WindowFocus.Restore(window, () => front, w => { if (++calls >= 3) front = w; }, () => true, 3, _ => { });
        Check(Slow() && calls == 3, "a system that refuses the first two times still ends in front on the third try");
        front = IntPtr.Zero; calls = 0;
        bool Never() => WindowFocus.Restore(window, () => front, _ => calls++, () => true, 3, _ => { });
        Check(!Never() && calls == 3, "a system that always refuses: three attempts, then false");
        calls = 0;
        Check(!WindowFocus.Restore(IntPtr.Zero, () => front, _ => calls++, () => true, 3, _ => { }) && calls == 0, "no window handle: no attempt");
        Check(WindowFocus.Restore(window, () => front, _ => { }, () => throw new InvalidOperationException(), 1, _ => { }) || true, "a throwing permission call does not escape");
        var fake = new FakeFocus { RefuseFirst = 2 };
        var screen = new Clicker(screen: new PixelRect(0, 0, 1920, 1080), focus: fake);
        screen.Real.Click(new ClickTarget(new PixelRect(100, 60, 10, 10), 1, new IntPtr(9)));
        Check(fake.Asked.Count == 1 && fake.Asked[0] == new IntPtr(9), "the press asks for the named window before sending anything");
    }

    /// <summary>A recorder around the real clicker: it keeps the order of the system calls and the settle times.</summary>
    private sealed class Clicker
    {
        public readonly List<string> Log = new();
        public readonly ScreenClicker Real;
        public Clicker(PixelRect screen, IWindowFocus? focus = null, bool acceptMove = true, bool acceptDown = true)
        {
            Real = new ScreenClicker { Screen = () => screen, Focus = focus ?? new FakeFocus() };
            Real.Wait = ms => Log.Add("wait " + ms);
            Real.Send = (x, y, flags) =>
            {
                var name = flags == Native.MoveFlags ? "move" : flags == Native.DownFlags ? "down" : flags == Native.UpFlags ? "up" : "other";
                Log.Add(name + " " + x + "," + y);
                return name switch { "move" => acceptMove, "down" => acceptDown, _ => true };
            };
        }
        public string[] Sequence => Log.Where(l => !l.StartsWith("wait")).ToArray();
    }

    /// <summary>The press is move, settle, down, short rest, up, and the settle stays between 60 and 140 ms.</summary>
    private static void Sequence()
    {
        var screen = new PixelRect(0, 0, 1920, 1080);
        var c = new Clicker(screen);
        c.Real.Click(new ClickTarget(new PixelRect(100, 60, 10, 10), 1, IntPtr.Zero));
        Check(c.Sequence.SequenceEqual(new[] { Move(105, 65), "down 0,0", "up 0,0" }), "order is move, press, release (and nothing else): " + string.Join("|", c.Log));
        Check(c.Log.Count == 5 && c.Log[1].StartsWith("wait ") && c.Log[3] == "wait " + ScreenClicker.TapMs, "a settle sits between the move and the press");
        int settle = int.Parse(c.Log[1]["wait ".Length..]);
        Check(settle is >= ScreenClicker.MinSettleMs and <= ScreenClicker.MaxSettleMs, "the settle is inside 60 to 140 ms");
        Check(c.Real.LastAccepted, "an accepted press says so");
        int same = ScreenClicker.SettleFor(100, 60);
        Check(Enumerable.Range(0, 300).Select(i => ScreenClicker.SettleFor(i, i * 3)).All(v => v is >= 60 and <= 140), "every settle stays inside the limits");
        Check(Enumerable.Range(0, 300).Select(i => ScreenClicker.SettleFor(i, i * 3)).Distinct().Count() > 20, "the settle is not one fixed number");

        var off = new Clicker(screen);
        off.Real.Click(new ClickTarget(new PixelRect(5000, 5000, 10, 10), 1, IntPtr.Zero));
        Check(off.Sequence[0] == "move 65535,65535", "a point off the screen is clamped before it is sent");
        var refused = new Clicker(screen, acceptMove: false);
        refused.Real.Click(new ClickTarget(new PixelRect(10, 10, 4, 4), 1, IntPtr.Zero));
        Check(refused.Sequence.SequenceEqual(new[] { Move(12, 12) }) && !refused.Real.LastAccepted, "a refused move stops before the press, so nothing is clicked elsewhere");
        var noUp = new Clicker(screen, acceptDown: false);
        noUp.Real.Click(new ClickTarget(new PixelRect(10, 10, 4, 4), 1, IntPtr.Zero));
        Check(noUp.Sequence.SequenceEqual(new[] { Move(12, 12), "down 0,0" }) && !noUp.Real.LastAccepted, "a refused press is reported and no release is sent");
        Check(same == ScreenClicker.SettleFor(100, 60), "the settle is stable for one point");
    }
}
