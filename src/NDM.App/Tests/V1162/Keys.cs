// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.Core;
using NDM.Core.Captcha;

internal static partial class V1162Tests
{
    private static Dictionary<string, string> Form(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('='); if (eq > 0) d[pair[..eq]] = pair[(eq + 1)..];
        }
        return d;
    }

    /// <summary>The page's report: eight numbers, all of them needed, all of them sane.</summary>
    private static void Report()
    {
        var good = Form("do=loaded&x=120&y=64&w=300&h=65&vw=1024&vh=768&dpr=1.5&eleft=-1200&etop=40&ew=1552&eh=1236");
        Check(CaptchaReport.TryParse(good, out var report, out _), "a full report is accepted");
        Check(report.Widget == new CssRect(120, 64, 300, 65) && report.Viewport == new CssRect(0, 0, 1024, 768), "the widget box and the viewport are read apart");
        Check(report.Dpr == 1.5 && report.BrowserWindow == new PixelRect(-1200, 40, 1552, 1236), "the pixel ratio and the browser window are read");
        Check(report.IsUsable, "a full report is usable");
        Check(CaptchaGeometry.Target(report, new PixelRect(-1920, 0, 3840, 1080), IntPtr.Zero) is { } t && t.X == -787 && t.Y == 269, "the report turns into the widget's middle on the screen");
        foreach (var missing in new[] { "x", "w", "vw", "vh", "dpr", "ew", "eh" })
        {
            var bad = new Dictionary<string, string>(good); bad.Remove(missing);
            Check(!CaptchaReport.TryParse(bad, out _, out var why) && why.Length > 0, "no " + missing + ": refused with a reason");
        }
        Check(!CaptchaReport.TryParse(Form("do=loaded&x=1&y=1&w=0&h=0&vw=800&vh=600&dpr=1&eleft=0&etop=0&ew=800&eh=600"), out _, out _), "an empty widget box is refused");
        Check(!CaptchaReport.TryParse(Form("do=loaded&x=1&y=1&w=9&h=9&vw=800&vh=600&dpr=99&eleft=0&etop=0&ew=800&eh=600"), out _, out _), "a pixel ratio far out of range is refused");
        Check(!CaptchaReport.TryParse(Form("do=loaded&x=1&y=1&w=9&h=9&vw=800&vh=600&dpr=1&eleft=0&etop=0&ew=0&eh=0"), out _, out _), "a window with no size is refused");
        Check(!CaptchaReport.TryParse(Form("do=loaded&x=abc&y=1&w=9&h=9&vw=800&vh=600&dpr=1&eleft=0&etop=0&ew=800&eh=600"), out _, out _), "an unreadable number is refused");
        Check(!CaptchaReport.TryParse(Form("do=loaded&x=1,5&y=1&w=9&h=9&vw=800&vh=600&dpr=1&eleft=0&etop=0&ew=800&eh=600"), out _, out _), "a comma decimal is not taken for a point");
        Check(CaptchaReport.TryParse(Form("do=loaded&x=-5&y=-5&w=9&h=9&vw=800&vh=600&dpr=2&eleft=-9&etop=-9&ew=800&eh=600"), out var neg, out _) && neg.IsUsable, "a widget scrolled above the page keeps negative numbers");
    }

    /// <summary>Keys are read from the three widget shapes, deduplicated, in page order, and the ones already known to fail are left out.</summary>
    private static void Discovery()
    {
        var turnstile = SiteKeyDiscovery.Find(Fx("widget-turnstile.html"));
        Check(turnstile.Count == 1 && turnstile[0].Kind == HostCaptchaKind.Turnstile && turnstile[0].SiteKey == "0x4AAAAAAAfixturefixtureturnstile" && turnstile[0].Source == "widget-element", "turnstile: one key from the widget element");
        var recaptcha = SiteKeyDiscovery.Find(Fx("widget-recaptcha.html"));
        Check(recaptcha.Count == 1 && recaptcha[0].Kind == HostCaptchaKind.ReCaptcha && recaptcha[0].SiteKey == "6LfFIXTURERECAPTCHAFIXTURERECAPTCHA1234", "recaptcha: the key is read");
        var hcaptcha = SiteKeyDiscovery.Find(Fx("widget-hcaptcha.html"));
        Check(hcaptcha.Count == 1 && hcaptcha[0].Kind == HostCaptchaKind.HCaptcha && hcaptcha[0].Source == "site-key-attribute", "hcaptcha: the key is read even without the widget class on the same element");
        var many = SiteKeyDiscovery.Find(Fx("widget-many.html"));
        Check(many.Count >= 2 && many.Select(c => c.SiteKey).Distinct().Count() == many.Count, "several widgets: every key once, in page order");
        Check(many[0].SiteKey == "0x4AAAAAAAfixturefirstturnstilekey", "the first widget on the page comes first");
        var failed = new[] { many[0].SiteKey };
        var afterSkip = SiteKeyDiscovery.Find(Fx("widget-many.html"), failed);
        Check(afterSkip.Count == many.Count - 1 && afterSkip.All(c => c.SiteKey != many[0].SiteKey), "a key that already failed is skipped");
        var render = SiteKeyDiscovery.Find("<script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js?render=0x4AAAAAAAfixturerenderkey\"></script>");
        Check(render.Count == 1 && render[0].Source == "loader-address", "a key named in the loader address is found");
        Check(SiteKeyDiscovery.Find("<div class=\"cf-turnstile\" data-sitekey=\"short\"></div>").Count == 0, "a too short key is ignored");
        Check(SiteKeyDiscovery.Find("<div class=\"g-recaptcha\"></div>").Count == 0, "a widget with no key gives no candidate");
        Check(SiteKeyDiscovery.Find(null).Count == 0 && SiteKeyDiscovery.Find("").Count == 0, "no markup: no candidates");
        Check(SiteKeyDiscovery.Find("<div class=\"not-a-widget\" data-sitekey=\"6LfFIXTURERECAPTCHAFIXTURERECAPTCHA1234\"></div><script src=\"https://www.google.com/recaptcha/api.js\"></script>").Single().Kind == HostCaptchaKind.ReCaptcha, "a bare key takes its kind from the page's own loader");

        var memory = new FailedSiteKeys();
        memory.Add("a"); memory.Add("b"); memory.Add("a");
        Check(memory.Count == 2 && memory.Contains("a") && memory.Contains("b") && !memory.Contains("c"), "the failed set remembers each key once");
        Check(memory.Contains(null) == false, "a null key is never remembered");
        for (int i = 0; i < FailedSiteKeys.Max + 10; i++) memory.Add("k" + i);
        Check(memory.Count == FailedSiteKeys.Max && !memory.Contains("a"), "the failed set stays bounded and drops the oldest");
        memory.Clear(); Check(memory.Count == 0, "the failed set can be cleared");
    }
}
