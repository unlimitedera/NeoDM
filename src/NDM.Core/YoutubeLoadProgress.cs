using System.Diagnostics;

namespace NDM.Core;

public enum YoutubeLoadStage { Preparing, Contacting, Solving, Reading, Building, Done }

/// <summary>Progress of "Load video info" (1.11.0). The helper cannot report a percentage, so the value is an ESTIMATE: stages with weights, a smoothed
/// time curve inside the current stage, capped at 95 percent until the real result arrives (then 100). Real stage changes come from the helper's own
/// progress lines (pinned yt-dlp 2026.10.26 with --quiet --verbose prints "[youtube] Extracting URL", "...: Downloading webpage", "...: Downloading ... player API JSON",
/// "...: Downloading player", "[youtube] [jsc:deno] Solving JS challenges", "...: Downloading m3u8 information", "[info] ...: Downloading N format(s)").
/// Without any such line (a different helper version) the stages advance by elapsed time alone. The value never goes down.</summary>
public sealed class YoutubeLoadProgress
{
    /// <summary>Share of the bar for each stage, in order: preparing tools, contacting YouTube, solving the player challenge, reading formats, building the list.</summary>
    public static readonly double[] Weights = { 5, 25, 40, 20, 10 };
    /// <summary>Typical time spent in each stage (seconds); the in-stage curve approaches the end of the stage's share with this time constant.</summary>
    public static readonly double[] Typical = { 1.0, 3.5, 6.0, 2.5, 0.8 };
    public const double Cap = 95;
    private const double TimedSlack = 1.6;

    private readonly Func<TimeSpan> clock;
    private TimeSpan started, stageStarted;
    private bool realLines, running, complete;
    private double high;
    public YoutubeLoadStage Stage { get; private set; } = YoutubeLoadStage.Preparing;
    public bool Running => running;
    /// <summary>True once the helper itself reported a stage; false means the stages are timed guesses.</summary>
    public bool UsesHelperLines => realLines;

    public YoutubeLoadProgress(Func<TimeSpan>? clock = null)
    {
        if (clock is null) { var watch = Stopwatch.StartNew(); clock = () => watch.Elapsed; }
        this.clock = clock;
    }

    public void Start() { started = stageStarted = clock(); Stage = YoutubeLoadStage.Preparing; realLines = false; running = true; complete = false; high = 0; }
    public TimeSpan Elapsed => running || complete ? clock() - started : TimeSpan.Zero;

    /// <summary>Maps one helper line to a stage; unknown lines change nothing.</summary>
    public static YoutubeLoadStage? StageOf(string line)
    {
        if (line.Length == 0 || line.StartsWith("[debug]", StringComparison.Ordinal) || char.IsWhiteSpace(line[0])) return null;
        if (line.Contains("Solving JS challenge", StringComparison.OrdinalIgnoreCase) || line.Contains(": Downloading player ", StringComparison.Ordinal)) return YoutubeLoadStage.Solving;
        if (line.Contains("Downloading m3u8", StringComparison.Ordinal) || line.Contains("Downloading MPD", StringComparison.Ordinal) || line.StartsWith("[info]", StringComparison.Ordinal) || line.Contains("Downloading item", StringComparison.Ordinal) || line.Contains(" items of ", StringComparison.Ordinal)) return YoutubeLoadStage.Reading;
        if (line.StartsWith("[youtube", StringComparison.Ordinal)) return YoutubeLoadStage.Contacting;
        return null;
    }

    /// <summary>Feeds one helper line (stderr). Returns true when the stage changed. Stages only move forward.</summary>
    public bool Line(string line)
    {
        if (!running || StageOf(line) is not { } next) return false;
        realLines = true;
        return Move(next);
    }

    public bool Move(YoutubeLoadStage next)
    {
        if (!running || next <= Stage || next > YoutubeLoadStage.Building) return false;
        Stage = next; stageStarted = clock(); return true;
    }

    /// <summary>The helper finished and the list is being built.</summary>
    public void Building() => Move(YoutubeLoadStage.Building);
    public void Finish() { running = false; complete = true; Stage = YoutubeLoadStage.Done; high = 100; }
    public void Stop() { running = false; }

    /// <summary>Estimated percentage 0 to 100 (100 only after <see cref="Finish"/>). Monotonic; call as often as needed.</summary>
    public double Percent()
    {
        if (complete) return 100;
        if (!running) return high;
        var now = clock();
        if (!realLines)
        {
            var t = (now - started).TotalSeconds; double sum = 0; var target = YoutubeLoadStage.Preparing; double targetStart = 0;
            for (int i = 0; i < Typical.Length - 1; i++) { sum += Typical[i] * TimedSlack; if (t >= sum) { target = (YoutubeLoadStage)(i + 1); targetStart = sum; } }
            if (target > Stage) { Stage = target; stageStarted = started + TimeSpan.FromSeconds(targetStart); }
        }
        int s = Math.Min((int)Stage, Weights.Length - 1);
        double floor = 0; for (int i = 0; i < s; i++) floor += Weights[i];
        var inStage = Math.Max(0, (now - stageStarted).TotalSeconds);
        double fraction = 1 - Math.Exp(-inStage / Typical[s]);
        var value = Math.Min(Cap, floor + Weights[s] * 0.92 * fraction);
        if (value > high) high = value;
        return high;
    }

    /// <summary>Resource key of the plain status line for the current stage.</summary>
    public string StatusKey => Stage switch
    {
        YoutubeLoadStage.Preparing => "youtube.stage.preparing",
        YoutubeLoadStage.Contacting => "youtube.stage.contacting",
        YoutubeLoadStage.Solving => "youtube.stage.solving",
        YoutubeLoadStage.Reading => "youtube.stage.reading",
        YoutubeLoadStage.Building => "youtube.stage.building",
        _ => "youtube.stage.done"
    };
}
