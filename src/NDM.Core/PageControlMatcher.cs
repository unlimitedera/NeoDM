using System.Text.RegularExpressions;

namespace NDM.Core;

// 1.16.1: moved from the app so the form logic in this library can score button labels too.

/// <summary>One control on the page that might start the download, as the page scan reports it.</summary>
public sealed record PageControl(string Id, string Text, string Tag, bool Enabled, bool Visible, bool FormPost, bool HasDownloadAttribute);

/// <summary>Chooses the control that starts a download: Download, Free download, Slow download, Create download link, Continue and the like; never Premium, Buy, Upgrade, sign-up or install buttons.</summary>
public static class PageControlMatcher
{
    private static readonly Regex Deny = new(@"\b(premium|buy|purchase|upgrade|subscribe|subscription|vip|pro|plus|fast|faster|turbo|high[ -]?speed|unlimited|sign[ -]?up|register|log[ -]?in|sign[ -]?in|install|extension|app|watch|play|ads?|advert\w*|sponsored|torrent|magnet|open in|report|share|donate|buy now)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly (Regex Pattern, int Score)[] Allow =
    {
        (new(@"^(?:free |slow |regular |normal |standard )?(?:file )?download(?: file)?(?: now)?(?: for free)?(?: \(?free\)?)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), 100),
        (new(@"^(?:create|generate|get|start|request|prepare) (?:the |your |a |my )?(?:free |slow )?(?:download )?link$", RegexOptions.IgnoreCase | RegexOptions.Compiled), 92),
        (new(@"^(?:start|begin|proceed to|continue to|click here to|click to|go to) (?:the |your )?download(?: now)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), 90),
        (new(@"^(?:free |slow )download.{0,24}$", RegexOptions.IgnoreCase | RegexOptions.Compiled), 85),
        (new(@"\bdownload\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), 70),
        (new(@"^(?:continue|proceed|next)(?: to (?:the )?(?:file|link|page|site))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled), 55),
    };
    public const int MaxText = 60;

    /// <summary>The score of one control text (0 = not a candidate).</summary>
    public static int Score(string? text)
    {
        var t = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        if (t.Length == 0 || t.Length > MaxText) return 0;
        if (Deny.IsMatch(t)) return 0;
        foreach (var (pattern, score) in Allow) if (pattern.IsMatch(t)) return score;
        return 0;
    }

    /// <summary>The best visible control (enabled or not: a disabled one is the timer still running). Ties go to a form post, then a control with the download attribute, then the first in page order.</summary>
    public static PageControl? Best(IReadOnlyList<PageControl> controls)
    {
        PageControl? best = null; int bestScore = 0;
        foreach (var c in controls)
        {
            if (!c.Visible) continue;
            int score = Score(c.Text); if (score == 0) continue;
            score = score * 10 + (c.FormPost ? 2 : 0) + (c.HasDownloadAttribute ? 1 : 0) + (c.Enabled ? 3 : 0);
            if (score > bestScore) { best = c; bestScore = score; }
        }
        return best;
    }
}

