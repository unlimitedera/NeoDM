using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using NDM.App.Localization;
namespace NDM.App;

/// <summary>What NeoDM does when ONE download completes ("Options on completion" tab).</summary>
public sealed class JobCompletionOptions
{
    public bool ShowDialog { get; set; } = true;
    public bool ExitApp { get; set; }
    public bool TurnOff { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public CompletionActionKind PowerAction { get; set; } = CompletionActionKind.Shutdown;
    public bool Force { get; set; }
    /// <summary>Per-download 'Play sound when done'. null = follow the global setting; a value is this download's own choice.</summary>
    public bool? PlaySound { get; set; }
    /// <summary>Per-download 'Show this window when the download finishes'. null = follow the global setting; a value is this download's own choice and wins.</summary>
    public bool? ShowWindow { get; set; }
    /// <summary>'Remember this choice for future sessions': only remembered choices are written to disk; the others last until NeoDM exits.</summary>
    public bool Remember { get; set; }

    public static readonly IReadOnlyList<CompletionActionKind> PowerChoices = new[] { CompletionActionKind.Shutdown, CompletionActionKind.Restart, CompletionActionKind.Sleep, CompletionActionKind.Hibernate, CompletionActionKind.Lock };
    public static string PowerLabel(CompletionActionKind kind) => kind switch
    {
        CompletionActionKind.Shutdown => Loc.T("completion.power.shutdown"), CompletionActionKind.Restart => Loc.T("completion.label.restart"), CompletionActionKind.Sleep => Loc.T("completion.label.sleep"),
        CompletionActionKind.Hibernate => Loc.T("completion.label.hibernate"), CompletionActionKind.Lock => Loc.T("completion.power.lock"), _ => kind.ToString()
    };
    // Enable rules: the action boxes only apply when the complete dialog is off.
    public bool ActionsEnabled => !ShowDialog;
    public bool PowerKindEnabled => ActionsEnabled && TurnOff;
    public bool ForceEnabled => PowerKindEnabled && CompletionActionInfo.UsesForce(PowerAction);
    public bool HasAction => ActionsEnabled && (ExitApp || TurnOff);
    public JobCompletionOptions Clone() => (JobCompletionOptions)MemberwiseClone();
}

/// <summary>Decides whether the completion sound plays for a finished job: the job's own choice wins, otherwise the global setting.</summary>
public static class CompletionSound
{
    public static bool ShouldShowWindow(AppSettings settings, JobCompletionOptions? job) => job?.ShowWindow ?? settings.ShowProgressOnCompletion;
    public static bool ShouldPlay(AppSettings settings, JobCompletionOptions? job) => job?.PlaySound ?? settings.PlayCompletionSound;
}

/// <summary>Per-job completion choices keyed by job id, persisted in app state so they survive a restart. Path null = memory only.</summary>
public sealed class JobCompletionStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, JobCompletionOptions> map = new();
    private readonly string? path;
    public string? LastError { get; private set; }
    public JobCompletionStore(string? path = null)
    {
        this.path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, JobCompletionOptions>>(File.ReadAllText(path));
            if (loaded is not null) foreach (var (id, o) in loaded) if (o is not null && Enum.IsDefined(o.PowerAction)) { o.Remember = true; map[id] = o; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
    public JobCompletionOptions? Get(string id) { lock (gate) return map.TryGetValue(id, out var o) ? o.Clone() : null; }
    public bool AnyShowDialog { get { lock (gate) return map.Values.Any(o => o.ShowDialog); } }
    public void Set(string id, JobCompletionOptions options) { lock (gate) { map[id] = options.Clone(); Save(); } }
    public void Remove(string id) { lock (gate) { if (map.Remove(id)) Save(); } }
    /// <summary>Drops entries for jobs that no longer exist.</summary>
    public void Prune(IEnumerable<string> liveIds) { var live = liveIds.ToHashSet(); lock (gate) { if (map.Keys.Where(k => !live.Contains(k)).ToArray() is { Length: > 0 } stale) { foreach (var k in stale) map.Remove(k); Save(); } } }
    private void Save()
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map.Where(p => p.Value.Remember).ToDictionary(p => p.Key, p => p.Value), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true); LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = Loc.T("completion.error.options_not_saved"); }
    }
}
