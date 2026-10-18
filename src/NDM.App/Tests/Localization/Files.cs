using System.IO;
using System.Text.RegularExpressions;
using NDM.App.Localization;

internal static partial class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);
    private static readonly Regex PluralSuffix = new(@"^(.+)_(zero|one|two|few|many|other)$", RegexOptions.Compiled);
    internal static string Group(string key) { var m = PluralSuffix.Match(key); return m.Success ? m.Groups[1].Value : key; }
    internal static string[] Placeholders(string value) => Placeholder.Matches(value).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

    /// <summary>Everything that can be wrong with one locale file compared with en.json. Empty list = valid.</summary>
    internal static List<string> ValidateLocale(string tag, Dictionary<string, string> file, Dictionary<string, string> en)
    {
        var problems = new List<string>();
        var enGroups = en.Keys.GroupBy(Group).ToDictionary(g => g.Key, g => g.ToList());
        var used = PluralRules.Used(tag);
        foreach (var (key, value) in file)
        {
            if (!en.ContainsKey(key) && !(PluralSuffix.IsMatch(key) && enGroups.ContainsKey(Group(key)))) { problems.Add("extra key " + key); continue; }
            if (PluralSuffix.IsMatch(key) && enGroups.ContainsKey(Group(key)) && !used.Contains(PluralSuffix.Match(key).Groups[2].Value)) problems.Add($"plural category of {key} is not used by {tag}");
            if (string.IsNullOrWhiteSpace(value)) problems.Add("empty value " + key);
            if (value != value.Trim() && en.TryGetValue(key, out var e0) && e0 == e0.Trim()) problems.Add("leading or trailing whitespace in " + key);
            if (value.Contains('\u27e6') || value.Contains('\u27e7')) problems.Add("unconverted marker in " + key);
        }
        foreach (var (group, keys) in enGroups)
        {
            bool plural = keys.Any(PluralSuffix.IsMatch);
            if (!plural)
            {
                string key = keys[0];
                if (!file.TryGetValue(key, out var value)) { problems.Add("missing key " + key); continue; }
                if (!Placeholders(value).SequenceEqual(Placeholders(en[key]))) problems.Add($"placeholders differ in {key}: [{string.Join(",", Placeholders(value))}] vs en [{string.Join(",", Placeholders(en[key]))}]");
                if (en[key].Contains("NeoDM") && !value.Contains("NeoDM")) problems.Add("brand name NeoDM lost in " + key);
                if (en[key].Length > 0 && value.Length > 0 && Regex.Matches(en[key], @"\r?\n").Count != Regex.Matches(value, @"\r?\n").Count) problems.Add("line break count differs in " + key);
            }
            else
            {
                // A plural group needs the language's own categories, at least "other", and every form may only use placeholders the English forms use.
                var enPlaceholders = keys.SelectMany(k => Placeholders(en[k])).Distinct().ToArray();
                foreach (var cat in used)
                {
                    if (!file.TryGetValue(group + "_" + cat, out var value)) { problems.Add($"missing plural form {group}_{cat}"); continue; }
                    if (!Placeholders(value).All(enPlaceholders.Contains)) problems.Add($"unknown placeholder in {group}_{cat}");
                    if (cat is "other" or "many" && !Placeholders(value).SequenceEqual(enPlaceholders.OrderBy(x => x, StringComparer.Ordinal))) problems.Add($"placeholders differ in {group}_{cat}");
                }
            }
        }
        return problems;
    }

    private static void LocaleFiles()
    {
        var en = ReadLocale(Path.Combine(LocalesDir, "en.json"));
        Check(en.Count > 800 && en.All(p => p.Value.Length > 0), $"en.json has {en.Count} non-empty keys");
        Check(en.Keys.All(k => Regex.IsMatch(k, @"^[a-z0-9]+(\.[a-z0-9_]+)+$")), "every key is dotted lowercase");
        foreach (var path in Directory.GetFiles(LocalesDir, "*.json").OrderBy(x => x))
        {
            string tag = Path.GetFileNameWithoutExtension(path);
            if (tag == "en") continue;
            var problems = ValidateLocale(tag, ReadLocale(path), en);
            Check(problems.Count == 0, $"{tag}.json is complete and valid against en.json ({problems.Count} problems: {string.Join("; ", problems.Take(8))})");
        }
        // The validator itself must catch the faults translators make.
        var bad = new Dictionary<string, string>(ReadLocale(Path.Combine(LocalesDir, "ar.json")));
        string some = en.Keys.First(k => !PluralSuffix.IsMatch(k) && Placeholders(en[k]).Length == 1);
        bad.Remove(en.Keys.First(k => !PluralSuffix.IsMatch(k) && k != some));
        bad[some] = "no placeholder here"; bad["not.a.key"] = "x"; bad["main.menu.file"] = " ";
        var found = ValidateLocale("ar", bad, en);
        Check(found.Any(p => p.StartsWith("missing key")) && found.Any(p => p.StartsWith("placeholders differ")) && found.Any(p => p.StartsWith("extra key")) && found.Any(p => p.StartsWith("empty value")), "the validator reports missing, extra, empty and placeholder faults");
        var wrongCategory = new Dictionary<string, string>(ReadLocale(Path.Combine(LocalesDir, "ar.json"))); wrongCategory.Remove("main.count_downloads_two");
        Check(ValidateLocale("ar", wrongCategory, en).Any(p => p.Contains("main.count_downloads_two")), "a missing Arabic plural category is reported");

        // Every key used in code and XAML exists.
        var usedKeys = new SortedSet<string>(); var groups = en.Keys.Select(Group).ToHashSet();
        foreach (var file in Directory.GetFiles(AppDir, "*.*", SearchOption.AllDirectories).Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\") && !f.Contains(@"\Tests\")))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"Loc\.(?:T|Plural|Has|Lookup)\(\s*""([a-z0-9._]+)""")) usedKeys.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\{loc:T\s+([a-z0-9._]+)\}")) usedKeys.Add(m.Groups[1].Value);
        }
        var unknown = usedKeys.Where(k => !k.EndsWith(".") && !en.ContainsKey(k) && !groups.Contains(k)).ToList(); // "theme." etc. are prefixes of computed keys
        var prefixes = usedKeys.Where(k => k.EndsWith(".")).ToList();
        Check(prefixes.All(pfx => en.Keys.Any(k => k.StartsWith(pfx))), "every computed-key prefix has keys in en.json");
        Check(usedKeys.Count > 700 && unknown.Count == 0, $"{usedKeys.Count} keys used in code and XAML all exist in en.json ({string.Join(", ", unknown.Take(5))})");
        var neverUsed = en.Keys.Select(Group).Distinct().Where(g => !usedKeys.Contains(g)).ToList();
        Console.WriteLine($"INFO: {neverUsed.Count} en.json keys are built dynamically or unused");
    }
}
