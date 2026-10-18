namespace NDM.App.Localization;

/// <summary>CLDR plural categories for whole numbers. Keys use the suffixes _zero, _one, _two, _few, _many and _other.</summary>
public static class PluralRules
{
    public static readonly IReadOnlyList<string> Categories = new[] { "zero", "one", "two", "few", "many", "other" };

    /// <summary>Categories a language uses for whole numbers (the ones a translator may supply).</summary>
    public static IReadOnlyList<string> Used(string tagOrRule) => Rule(tagOrRule) switch
    {
        "ar" => new[] { "zero", "one", "two", "few", "many", "other" },
        "ru" or "pl" => new[] { "one", "few", "many", "other" },
        "he" => new[] { "one", "two", "other" },
        "zh" or "ja" or "ko" or "id" => new[] { "other" },
        _ => new[] { "one", "other" },
    };

    private static string Rule(string tagOrRule)
    {
        var info = LanguageCatalog.Find(tagOrRule);
        return info?.Plural ?? tagOrRule.Split('-')[0].ToLowerInvariant();
    }

    /// <summary>Plural category of <paramref name="n"/> in the language <paramref name="tag"/>. Unknown languages use the English rule.</summary>
    public static string Category(string tag, long n)
    {
        long a = Math.Abs(n), m10 = a % 10, m100 = a % 100;
        switch (Rule(tag))
        {
            case "ar":
                if (a == 0) return "zero";
                if (a == 1) return "one";
                if (a == 2) return "two";
                if (m100 is >= 3 and <= 10) return "few";
                if (m100 is >= 11 and <= 99) return "many";
                return "other";
            case "ru":
                if (m10 == 1 && m100 != 11) return "one";
                if (m10 is >= 2 and <= 4 && m100 is not (>= 12 and <= 14)) return "few";
                return "many";
            case "pl":
                if (a == 1) return "one";
                if (m10 is >= 2 and <= 4 && m100 is not (>= 12 and <= 14)) return "few";
                return "many";
            case "fr" or "pt" or "hi" or "fa":
                return a <= 1 ? "one" : "other";
            case "he":
                return a == 1 ? "one" : a == 2 ? "two" : "other";
            case "zh" or "ja" or "ko" or "id":
                return "other";
            default: // en, es, de, it, nl, tr
                return a == 1 ? "one" : "other";
        }
    }
}
