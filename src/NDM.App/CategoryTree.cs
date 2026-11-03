using NDM.App.Localization;

namespace NDM.App;

/// <summary>One entry of the category list below All downloads: a built-in category, a custom category, or Other.</summary>
public sealed record CategoryEntry(string Key, string Name, bool IsCustom, string IconKey);

/// <summary>1.10.0: the category list of the sidebar. Keys: "b:Video" (built-in), "c:My name" (custom), "other".
/// The category of a file comes from <see cref="CategoryRules.Classify"/>, which follows the order of the save-folder rules (custom address rules, custom extensions, built-in address rules, built-in extensions), so the filter and the folder agree.
/// Behaviour with other filters: choosing a category shows its files in every status; choosing a status entry (or All downloads) clears the category; the search box narrows inside whatever is chosen.</summary>
public static class CategoryTree
{
    public const string FilterPrefix = "C:";
    public const string Other = CategoryRules.OtherKey;
    public static string BuiltInKey(string id) => "b:" + id;
    public static string CustomKey(string name) => "c:" + name;

    /// <summary>Key of the category a file belongs to.</summary>
    public static string KeyOf(AppSettings s, string fileName, string? url, string? referer)
    {
        var d = CategoryRules.Classify(s, fileName, url, referer);
        return d.Category is null ? Other : d.IsCustom ? CustomKey(d.Category) : BuiltInKey(d.Category);
    }

    /// <summary>Entries in display order: built-in categories that are switched on, custom categories that are switched on (list order), then Other.</summary>
    public static IReadOnlyList<CategoryEntry> Entries(AppSettings s)
    {
        var list = new List<CategoryEntry>();
        foreach (var id in CategoryRules.TreeOrder)
            if (CategoryRules.BuiltInEnabled(s, id)) list.Add(new(BuiltInKey(id), NameOf(BuiltInKey(id)), false, id));
        foreach (var c in s.CustomCategories)
            if (c.Enabled) list.Add(new(CustomKey(c.Name), c.Name, true, "Custom"));
        list.Add(new(Other, NameOf(Other), false, "Other"));
        return list;
    }

    public static string NameOf(string key) => key == Other ? Loc.T("nav.cat.other") : key.StartsWith("b:", StringComparison.Ordinal) ? Loc.T("cat.builtin." + key[2..].ToLowerInvariant()) : key.StartsWith("c:", StringComparison.Ordinal) ? key[2..] : key;

    /// <summary>Text that changes when the category setup changes (names, extensions, address rules, switches); the sidebar re-sorts the files when it changes.</summary>
    public static string Signature(AppSettings s)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(s.EnableCategoryFolders ? '1' : '0');
        foreach (var id in CategoryRules.BuiltInIds)
            sb.Append('|').Append(id).Append(CategoryRules.BuiltInEnabled(s, id) ? '+' : '-').Append(string.Join(",", CategoryRules.BuiltInExtensions(s, id))).Append('#').Append(string.Join(",", CategoryRules.BuiltInRules(s, id)));
        foreach (var c in s.CustomCategories)
            sb.Append('|').Append(c.Name).Append(c.Enabled ? '+' : '-').Append(string.Join(",", c.Extensions)).Append('#').Append(string.Join(",", c.UrlRules));
        return sb.ToString();
    }
    public static string IconKeyOf(CategoryEntry e) => e.IconKey;
}
