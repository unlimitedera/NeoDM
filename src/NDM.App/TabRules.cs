// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.Core;

namespace NDM.App;

/// <summary>2.0.0: the strip filter of the tabbed downloads window.</summary>
public enum TabFilter { All, Active, Failed }

/// <summary>2.0.0: pure rules of the tab strip: the names that fit a tab, groups, the filter, the overflow list and the automatic close of finished tabs.</summary>
public static class TabRules
{
    /// <summary>From this many tabs on, the strip offers a list of all tabs.</summary>
    public const int OverflowAt = 20;
    /// <summary>Seconds a finished tab stays open when the automatic close is on (allowed range).</summary>
    public const int MinCloseSeconds = AppSettings.AutoCloseSecondsMin, MaxCloseSeconds = AppSettings.AutoCloseSecondsMax, DefaultCloseSeconds = AppSettings.AutoCloseSecondsDefault;
    /// <summary>The prompt 'Show downloads as tabs?' comes after this many downloads, and once more after this many more when the answer was 'not now'.</summary>
    public const int OfferAfter = AppSettings.TabsOfferAfter, OfferAgainAfter = 10;

    public static int ClampCloseSeconds(int seconds) => Math.Clamp(seconds, MinCloseSeconds, MaxCloseSeconds);

    /// <summary>How many characters of a name a tab of this width can show next to its icon, the close button and the paddings.</summary>
    public static int CharBudget(double tabWidth) => Math.Max(8, (int)Math.Floor((tabWidth - 76) / 6.8));

    /// <summary>The text shown on each tab for the given names. A name that fits is shown whole. A longer one keeps its start and its END (so the extension and the part number stay
    /// visible: a middle ellipsis); when other names share its beginning, the kept end reaches back to the place where the names start to differ, so "-00002-of-00005" and
    /// "-00003-of-00005" never look the same.</summary>
    public static IReadOnlyList<string> Labels(IReadOnlyList<string> names, int budget)
    {
        budget = Math.Max(8, budget);
        var result = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i] ?? "";
            if (name.Length <= budget) { result[i] = name; continue; }
            int differ = 0; // the first position where this name differs from the most similar other long name
            for (int j = 0; j < names.Count; j++)
            {
                if (j == i || names[j] is not { Length: > 0 } other) continue;
                int k = 0; int max = Math.Min(name.Length, other.Length);
                while (k < max && name[k] == other[k]) k++;
                if (k < name.Length || k < other.Length) differ = Math.Max(differ, k); // identical names have nothing to show
            }
            int headMin = Math.Min(4, budget / 4); // the start of the name always keeps a few characters
            int maxTail = budget - 1 - headMin;
            if (differ == 0)
            {
                // nothing to tell apart: keep the start and the end (extension and number)
                int tail = Math.Clamp(budget * 4 / 10, 1, maxTail);
                result[i] = name[..(budget - 1 - tail)].TrimEnd() + "…" + name[^tail..];
                continue;
            }
            int tailStart = Math.Max(0, differ - 1), needed = name.Length - tailStart;
            if (needed <= maxTail)
            {
                // the whole end from the first difference fits: use all the spare room for more of the end
                int tail = Math.Min(name.Length, maxTail);
                result[i] = name[..(budget - 1 - tail)].TrimEnd() + "…" + name[^tail..];
            }
            else
            {
                // the end is too long to show with the difference: start at the difference and mark the cut
                result[i] = name[..headMin].TrimEnd() + "…" + name.Substring(tailStart, maxTail - 1) + "…";
            }
        }
        return result;
    }

    /// <summary>The group a download's tab belongs to: its package, else its queue when it is not the Main queue; null for a plain download.</summary>
    public static string? GroupOf(string? package, string? queueId, string? queueName) =>
        !string.IsNullOrWhiteSpace(package) ? "p:" + package : queueId is { Length: > 0 } && queueId != QueueIds.Main ? "q:" + (queueName ?? queueId) : null;
    /// <summary>The name to show for a group key.</summary>
    public static string GroupName(string key) => key.Length > 2 ? key[2..] : key;
    public const int GroupColors = 6;
    /// <summary>A stable colour slot (0..5) for a group key.</summary>
    public static int GroupColor(string key)
    {
        unchecked { int h = 17; foreach (var c in key) h = h * 31 + c; return (h & 0x7fffffff) % GroupColors; }
    }

    public static bool Matches(TabFilter filter, string status) => filter switch
    {
        TabFilter.Failed => status == "Failed",
        TabFilter.Active => status is not ("Failed" or "Completed" or "Seeding" or "Canceled"),
        _ => true,
    };
    /// <summary>A tab whose download is finished (completed, or seeding): the ones 'Close completed tabs' and the automatic close take. A failed tab is never one of them.</summary>
    public static bool IsFinished(string status) => status is "Completed" or "Seeding";

    /// <summary>The line under the percentage: the speed and the time left while transferring, whichever is known.</summary>
    public static string Detail(string status, string speed, string remaining, Func<string, string, string> both, Func<string, string> leftOnly)
    {
        if (status is not ("Downloading" or "Processing")) return "";
        bool hasSpeed = speed.Length > 0 && speed != "—", hasLeft = remaining.Length > 0 && remaining != "—";
        return hasSpeed && hasLeft ? both(speed, remaining) : hasSpeed ? speed : hasLeft ? leftOnly(remaining) : "";
    }

    /// <summary>The next count at which the 'Show downloads as tabs?' question comes; -1 = never again.</summary>
    public static int NextOffer(int started, bool yes, bool dontAsk) => yes || dontAsk ? -1 : started + OfferAgainAfter;
    public static bool OfferDue(bool tabsOn, int startedCount, int offerAt) => !tabsOn && offerAt >= 0 && startedCount >= offerAt;
}
