// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Text.RegularExpressions;

namespace NDM.App;

/// <summary>1.16.0: links that clearly belong to one set (Part_1..Part_N, name.part1.rar, name.z01, name.001, "name (1 of 4)", name_01 ...) from the same host.</summary>
/// <param name="Stem">The shared name without the part number and the extension; empty when the names are only "Part_N" (the window then suggests a name from the host).</param>
/// <param name="Members">Indexes into the list given to <see cref="LinkGrouping.Group"/>, in part order.</param>
public sealed record LinkGroup(string Host, string Stem, IReadOnlyList<int> Members);

/// <summary>Finds sets of numbered parts. Only a group of two or more links with different part numbers counts, and every member needs the same host, the same stem and the same kind of numbering.
/// A lone numbered file is never grouped. Pure function: no network, no window.</summary>
public static class LinkGrouping
{
    public const int MinMembers = 2;
    private static readonly Regex[] Patterns =
    {
        // name.part03.rar, name.part3.zip
        new(@"^(?<stem>.+?)[ ._\-]part[ ._\-]?0*(?<n>\d{1,4})(?<ext>\.[A-Za-z0-9]{1,5})$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // name (1 of 4).zip   name [2 of 4].zip   name 2-of-4.zip
        new(@"^(?<stem>.+?)[ ._\-]*[\(\[]\s*0*(?<n>\d{1,4})\s*(?:of|von|de|di|sur|/)\s*\d{1,4}\s*[\)\]](?<ext>\.[A-Za-z0-9]{1,5})?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // name.z01, name.z02 (the first part is name.zip)
        new(@"^(?<stem>.+?)\.z0*(?<n>\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // name.7z.001, name.001, name.rar.002
        new(@"^(?<stem>.+?)(?:\.[A-Za-z0-9]{2,4})?\.0*(?<n>\d{1,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Part_1.zip, Part 2.zip, name_part2.zip, name-CD1.iso, name disc 2.iso, name vol3.pdf
        new(@"^(?<stem>.*?)[ ._\-]*(?:part|pt|cd|disc|disk|vol|volume|teil)[ ._\-]*0*(?<n>\d{1,4})(?<ext>\.[A-Za-z0-9]{1,5})?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // name_01.zip, name-2.zip, name 03.rar (needs a separator before the number so "mp3" or "x264" is not a part)
        new(@"^(?<stem>.+?)[ ._\-]+0*(?<n>\d{1,3})(?<ext>\.[A-Za-z0-9]{1,5})$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private sealed record Parsed(int Pattern, string Stem, int Number, string Ext);

    /// <summary>The part number and stem a file name carries, or null when it does not look numbered.</summary>
    private static Parsed? Parse(string name)
    {
        name = name.Trim();
        for (int i = 0; i < Patterns.Length; i++)
        {
            var m = Patterns[i].Match(name);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups["n"].Value, out var n)) continue;
            var stem = m.Groups["stem"].Value.Trim(' ', '.', '_', '-');
            var ext = m.Groups["ext"].Success ? m.Groups["ext"].Value.ToLowerInvariant() : "";
            // a purely numeric "stem" of the generic pattern is a plain number file (2024_05.zip), not a part of a set
            if (i == 5 && stem.Length > 0 && stem.All(char.IsDigit)) continue;
            return new Parsed(i == 0 || i == 4 ? 0 : i, stem, n, i is 2 or 3 ? "" : ext);
        }
        return null;
    }

    /// <summary>Groups the entries. <paramref name="items"/> carries the file name and host of each link, in table order.</summary>
    public static IReadOnlyList<LinkGroup> Group(IReadOnlyList<(string Name, string Host)> items)
    {
        // a ".zip" next to ".z01" ".z02" is the last part of the set: give it the number 0 of that stem
        var parsed = items.Select(i => Parse(i.Name)).ToArray();
        var zipStems = parsed.Select((p, idx) => (p, idx)).Where(x => x.p is { Pattern: 2 }).Select(x => (items[x.idx].Host.ToLowerInvariant(), x.p!.Stem.ToLowerInvariant())).ToHashSet();
        for (int i = 0; i < items.Count; i++)
        {
            if (parsed[i] is not null) continue;
            var ext = System.IO.Path.GetExtension(items[i].Name);
            if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = System.IO.Path.GetFileNameWithoutExtension(items[i].Name).Trim(' ', '.', '_', '-');
            if (zipStems.Contains((items[i].Host.ToLowerInvariant(), stem.ToLowerInvariant()))) parsed[i] = new Parsed(2, stem, 0, "");
        }
        var groups = new List<LinkGroup>();
        var buckets = new Dictionary<(string, string, int, string), List<int>>();
        var order = new List<(string, string, int, string)>();
        for (int i = 0; i < items.Count; i++)
        {
            if (parsed[i] is not { } p) continue;
            var key = (items[i].Host.ToLowerInvariant(), p.Stem.ToLowerInvariant(), p.Pattern, p.Ext);
            if (!buckets.TryGetValue(key, out var list)) { buckets[key] = list = new(); order.Add(key); }
            list.Add(i);
        }
        foreach (var key in order)
        {
            var members = buckets[key];
            if (members.Count < MinMembers) continue;
            if (members.Select(i => parsed[i]!.Number).Distinct().Count() != members.Count) continue; // two links with the same part number are not one set
            var sorted = members.OrderBy(i => parsed[i]!.Number).ThenBy(i => i).ToList();
            groups.Add(new LinkGroup(items[sorted[0]].Host, parsed[sorted[0]]!.Stem, sorted));
        }
        // a link belongs to one group only (the first claim wins) and a group needs two members after that
        var claimed = new HashSet<int>(); var result = new List<LinkGroup>();
        foreach (var g in groups)
        {
            var free = g.Members.Where(claimed.Add).ToList();
            if (free.Count >= MinMembers) result.Add(g with { Members = free });
        }
        return result;
    }

    /// <summary>The package name to suggest: the shared stem, or a name made from the host when the names are only "Part_N".</summary>
    public static string SuggestName(LinkGroup group, Func<string, string> fromHost) => group.Stem.Length >= 2 ? group.Stem : fromHost(group.Host);
}
