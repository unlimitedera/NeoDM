using System.IO;
using System.Text.RegularExpressions;

/// <summary>Hard-coded-string guard: fails on a user-visible English literal in XAML or in the C# calls that put text on screen, unless the allowlist
/// (hardcoded-allowlist.txt, one "file | text | reason" line per entry) says why it stays.</summary>
internal static partial class LocalizationTests
{
    internal sealed record Hit(string File, int Line, string Kind, string Text);

    private static readonly Regex XamlAttr = new(@"\b(Text|Content|Header|ToolTip|Title|Watermark|PlaceholderText|Label|AutomationProperties\.Name|AutomationProperties\.HelpText)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex XamlInner = new(@">\s*([^<>{}\r\n][^<>{}]*?)\s*</", RegexOptions.Compiled);
    private static readonly Regex CsAssign = new(@"\b(?:Text|Content|Header|Title|ToolTip|StatusText|NoticeText|Message|Detail)\s*=\s*\$?(""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled);
    private static readonly Regex CsCall = new(@"\b(?:MessageBox\.Show|Dialogs\.\w+|ShowNotice|ShowStatus|SetStatus|Status|Balloon|ShowBalloon|ShowBalloonTip|Notify|ShowError|ShowPrompt)\s*\(([^;]*)", RegexOptions.Compiled);
    private static readonly Regex CsLiteral = new(@"\$?""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex Throws = new(@"new\s+(?:UserMessage|Format|InvalidOperation|InvalidData|Argument)\w*Exception\(\s*\$?(""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled);

    private static readonly Regex KeyShape = new(@"^[a-z0-9]+(\.[a-z0-9_]+)+$", RegexOptions.Compiled);
    private static bool Visible(string s, bool needsSpace = false)
    {
        s = s.Trim(); if (s.Length == 0 || KeyShape.IsMatch(s)) return false;
        if (needsSpace && !Regex.IsMatch(s, @"\s")) return false; // one-word C# literals are ids (status names, keys), sentences have spaces
        s = Regex.Replace(s, @"\{[^}]*\}", ""); // {0}, {Binding ...}
        s = Regex.Replace(s, @"\\[nrt]", " ");
        return Regex.IsMatch(s, @"[A-Za-z]{2,}");
    }

    internal static List<Hit> ScanSources(string appDir)
    {
        var hits = new List<Hit>();
        foreach (var file in Directory.GetFiles(appDir, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\") || file.Contains(@"\Tests\") || file.Contains(@"\Locales\")) continue;
            string rel = Path.GetRelativePath(appDir, file).Replace('\\', '/');
            if (file.EndsWith(".xaml"))
            {
                if (rel.StartsWith("Themes/")) continue;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in XamlAttr.Matches(lines[i]))
                        if (!m.Groups[2].Value.StartsWith("{") && Visible(m.Groups[2].Value)) hits.Add(new Hit(rel, i + 1, "xaml " + m.Groups[1].Value, m.Groups[2].Value));
                    foreach (Match m in XamlInner.Matches(lines[i]))
                        if (!lines[i].TrimStart().StartsWith("<!--") && Visible(m.Groups[1].Value)) hits.Add(new Hit(rel, i + 1, "xaml text", m.Groups[1].Value));
                }
            }
            else if (file.EndsWith(".cs") && !rel.StartsWith("Localization/"))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i]; if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///")) continue;
                    foreach (Match m in CsAssign.Matches(line)) { string v = Unquote(m.Groups[1].Value); if (Visible(v, true)) hits.Add(new Hit(rel, i + 1, "cs assign", v)); }
                    foreach (Match call in CsCall.Matches(line))
                        foreach (Match lit in CsLiteral.Matches(call.Groups[1].Value)) if (Visible(lit.Groups[1].Value, true)) hits.Add(new Hit(rel, i + 1, "cs call", lit.Groups[1].Value));
                    foreach (Match m in Throws.Matches(line)) { string v = Unquote(m.Groups[1].Value); if (Visible(v, true)) hits.Add(new Hit(rel, i + 1, "cs throw", v)); }
                }
            }
        }
        return hits;
    }
    private static string Unquote(string literal) => literal.Length >= 2 ? literal[1..^1] : literal;

    internal static List<(string File, string Text, string Reason)> ReadAllowlist()
    {
        var list = new List<(string, string, string)>();
        foreach (var raw in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "hardcoded-allowlist.txt")))
        {
            string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || parts[2].Length < 8) throw new Exception("allowlist line needs 'file | text | reason': " + raw);
            list.Add((parts[0], parts[1], parts[2]));
        }
        return list;
    }

    private static void Guard()
    {
        var allow = ReadAllowlist();
        Check(allow.All(a => a.Reason.Length >= 8), $"allowlist: all {allow.Count} entries carry a reason");
        var hits = ScanSources(AppDir);
        var unexplained = hits.Where(h => !allow.Any(a => (a.File == "*" || a.File == h.File) && (a.Text == "*" || h.Text.Contains(a.Text)))).ToList();
        Check(unexplained.Count == 0, $"hard-coded strings: {hits.Count} literals scanned, {unexplained.Count} not allowlisted\n  " + string.Join("\n  ", unexplained.Take(40).Select(h => $"{h.File}:{h.Line} [{h.Kind}] {h.Text}")));
        var stale = allow.Where(a => a.Text != "*" && !hits.Any(h => (a.File == "*" || a.File == h.File) && h.Text.Contains(a.Text))).ToList();
        Check(stale.Count == 0, $"allowlist has no stale entries ({string.Join("; ", stale.Take(5).Select(a => a.File + "|" + a.Text))})");
        // The scanner must see a planted literal.
        string probeDir = Path.Combine(Path.GetTempPath(), "locprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDir);
        try
        {
            File.WriteAllText(Path.Combine(probeDir, "Probe.xaml"), "<Window><Button Content=\"Save changes\"/><TextBlock Text=\"{loc:T a.b}\"/><TextBlock>Plain words</TextBlock></Window>");
            File.WriteAllText(Path.Combine(probeDir, "Probe.cs"), "class P { void M() { MessageBox.Show(\"Something failed\"); x.Text = \"Ready now\"; Status(Loc.T(\"a.b\")); } }");
            var probe = ScanSources(probeDir);
            Check(probe.Count == 4 && probe.Any(h => h.Text == "Save changes") && probe.Any(h => h.Text == "Plain words") && probe.Any(h => h.Text == "Something failed") && probe.Any(h => h.Text == "Ready now"), $"the guard finds planted literals and ignores bindings ({probe.Count} found)");
        }
        finally { Directory.Delete(probeDir, true); }
    }
}
