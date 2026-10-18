using System.Text;

namespace NDM.App.Localization;

/// <summary>Layout test language: accented letters, about 30% longer, wrapped in brackets. Placeholders and access keys survive. Enabled with NEODM_PSEUDO=1.</summary>
public static class PseudoLocale
{
    private const string From = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string To = "àƀçðéƒĝĥíĵķĺɱñöþqŕšţûṽŵẋýžÀßÇÐÉFĜĤÍĴĶĹMÑÖÞQŔŠŢÛṼŴẊÝŽ";

    public static bool Requested => Environment.GetEnvironmentVariable("NEODM_PSEUDO") == "1";

    public static string Transform(string english)
    {
        if (string.IsNullOrEmpty(english)) return english;
        var sb = new StringBuilder("[");
        int letters = 0;
        for (int i = 0; i < english.Length; i++)
        {
            char c = english[i];
            if (c == '{') // placeholder: copy through the closing brace
            {
                int end = english.IndexOf('}', i);
                if (end > i) { sb.Append(english, i, end - i + 1); i = end; continue; }
            }
            int at = From.IndexOf(c);
            if (at >= 0) { sb.Append(To[at]); letters++; } else sb.Append(c);
        }
        int pad = (int)Math.Ceiling(letters * 0.3);
        if (letters > 0) sb.Append(' ').Append('~', Math.Max(1, pad));
        return sb.Append(']').ToString();
    }
}
