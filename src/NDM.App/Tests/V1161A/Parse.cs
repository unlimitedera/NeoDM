// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using NDM.Core;

internal static partial class V1161ATests
{
    private static void Parse()
    {
        // the real first step of the pilot host (trimmed copy of a plain GET): no captcha on this step, the button is disabled until the site's own check ends
        var p1 = HostFormParser.Parse(Fx("datanodes-download1.html"), "https://datanodes.to/download");
        Check(p1.Recognised && p1.Form!.Op == "download1", "download1: form recognised by its op field");
        Check(p1.Form!.Get("id") == "li77lxy835bq" && p1.Form.Get("fname") == "Part 1.zip" && p1.Form.Get("referer") == "https://datanodes.to/users", "download1: hidden fields read (the decoy field outside the form and the comment are ignored)");
        Check(p1.Form.SubmitName == "method_free" && p1.Form.SubmitDisabled && p1.Form.SubmitValue == "Free Download >>", "download1: submit method_free (disabled until the site's timer ends)");
        Check(!p1.Captcha.Present && p1.CountdownSeconds is null, "download1: no captcha and no stated countdown in the markup");
        Check(HostQuirks.For("datanodes.to") is { MinWaitSeconds: >= 6 }, "quirk table: the pilot host's own wait is data");
        var post1 = HostPostBuilder.Build(p1.Form, p1.Captcha, null);
        Check(post1.Select(k => k.Key).SequenceEqual(new[] { "op", "usr_login", "id", "fname", "referer", "method_free" }), "download1: POST body fields in page order with the submit button");
        Check(HostPostBuilder.Target(p1.Form, new Uri("https://datanodes.to/download"))!.AbsoluteUri == "https://datanodes.to/download", "download1: empty action posts to the page itself");

        var p2 = HostFormParser.Parse(Fx("xfs-download2-recaptcha.html"), "https://fixture.example/abc/Part_2.zip");
        Check(p2.Form is { Op: "download2" } && p2.Form.Get("rand") == "r4nd0mfixt" && p2.Form.Get("dl_token") == "fixture-dl-token", "download2: op, rand, dl_token read");
        Check(p2.CountdownSeconds == 6, "download2: countdown from the page's timer element");
        Check(p2.Captcha.Kind == HostCaptchaKind.ReCaptcha && p2.Captcha.SiteKey!.StartsWith("6Lf") && p2.Captcha.BrowserStepCan, "download2: reCAPTCHA v2 with a site key, the browser step can carry it");
        var post2 = HostPostBuilder.Build(p2.Form!, p2.Captcha, "TOKEN-1");
        Check(post2.Any(k => k.Key == "g-recaptcha-response" && k.Value == "TOKEN-1") && post2.Last().Key == "method_free", "download2: token goes into g-recaptcha-response, once");
        Check(!HostPostBuilder.Build(p2.Form!, p2.Captcha, null).Any(k => k.Key == "g-recaptcha-response"), "download2: no token, no response field");

        var p3 = HostFormParser.Parse(Fx("xfs-download2-nocaptcha.html"), "https://fixture.example/x/Part_3.zip");
        Check(p3.Form is { Op: "download2" } && p3.CountdownSeconds == 3 && !p3.Captcha.Present, "no-captcha host: countdown from a script variable, no captcha");
        Check(p3.Form!.SubmitName == "method_free" && p3.Form.SubmitValue == "Create Download Link", "no-captcha host: submit input recognised");
        Check(HostPostBuilder.Target(p3.Form, new Uri("https://fixture.example/x/Part_3.zip"))!.AbsoluteUri == "https://fixture.example/dl", "action resolved against the page");

        Check(HostFormParser.ParseCountdown("<div countdown=\"9\"></div>") == 9, "countdown attribute (plugin-style) read");
        Check(HostFormParser.ParseCountdown("<span id=\"countdown\">500</span>") == 120, "countdown capped at 120 seconds");
        Check(HostFormParser.ParseCountdown("<b>Please wait 12 seconds</b>") == 12, "countdown from the words on the page");
        Check(HostFormParser.ParseCountdown("<p>nothing here</p>") is null, "no countdown when the page states none");

        Check(HostFormParser.DetectCaptcha("<div class=\"h-captcha\" data-sitekey=\"10000000-ffff-ffff-ffff-000000000001\"></div>").Kind == HostCaptchaKind.HCaptcha, "hCaptcha detected");
        Check(HostFormParser.DetectCaptcha("<div class=\"cf-turnstile\" data-sitekey=\"0x4AAAAAAAfixturefixture1\"></div>").Kind == HostCaptchaKind.Turnstile, "Turnstile detected");
        Check(HostFormParser.DetectCaptcha("<img src=\"/captchas/123.jpg\"><input name=\"code\">").Kind == HostCaptchaKind.Image, "image captcha detected (the browser step cannot carry it)");
        Check(!HostFormParser.DetectCaptcha("<img src=\"/captchas/123.jpg\">").BrowserStepCan, "image captcha: browser step not possible");
        Check(!HostFormParser.DetectCaptcha("<!-- <div class=\"g-recaptcha\" data-sitekey=\"6LfAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"></div> -->").Present, "a captcha inside a comment is not a captcha");

        var j = HostFormParser.ExtractLinks("{\"status\":\"ok\",\"url\":\"https:\\/\\/cdn.fixture.example\\/d\\/Part_2.zip\"}", null, "https://fixture.example/a");
        Check(j.Count == 1 && j[0] == "https://cdn.fixture.example/d/Part_2.zip", "link from a JSON reply (escaped slashes)");
        var h = HostFormParser.ExtractLinks(Fx("xfs-reply-html.html"), null, "https://fixture.example/a");
        Check(h.Count >= 2 && h.Contains("https://cdn.fixture.example/d/abc123/Part_3.zip") && h.Contains("https://cdn2.fixture.example/d/abc/Part_3.zip"), "links from an anchor and a url-encoded value");
        Check(!h.Any(x => x.Contains("premium")), "the premium anchor is not a download link");
        var err = HostFormParser.Parse(Fx("xfs-error-referer.html"), "https://fixture.example/a");
        Check(err.ReferrerRefused && err.ErrorText!.Contains("Not allowed"), "'Not allowed from domain' recognised");
        Check(HostQuirks.RefererFor(new Uri("https://datanodes.to/x"), false) == "https://datanodes.to/users" && HostQuirks.RefererFor(new Uri("https://datanodes.to/x"), true) == "https://datanodes.to/", "referer: table first, own origin on retry");
        Check(!HostFormParser.Parse("<form action=\"/search\"><input name=\"q\"><input type=\"submit\" value=\"Search\"></form>", "https://a.example/").Recognised, "a search form is not a download form");

        // the real second step of the pilot host (trimmed copy of the page after the step-1 post; rand and token replaced): a script component, not a form, with Cloudflare Turnstile
        var p4 = HostFormParser.Parse(Fx("datanodes-download2.html"), "https://datanodes.to/li77lxy835bq/Part_1.zip");
        Check(p4.Recognised && p4.Form!.FromComponent && p4.Form.Op == "download2" && p4.Form.Get("id") == "li77lxy835bq" && p4.Form.Get("rand") == "r4ndfixturedatanodes" && p4.Form.Get("dl_token") == "fixture-dl-token-0001", "script page: the form fields are rebuilt from the component's attributes");
        Check(p4.Form!.Get("referer") == "https://datanodes.to/users" && p4.Form.Get("method_free") == "Free Download >>", "script page: referer and free method read");
        Check(p4.CountdownSeconds == 5, "script page: the countdown attribute (:countdown) is read");
        Check(p4.Captcha.Kind == HostCaptchaKind.Turnstile && p4.Captcha.SiteKey == "0x4AAAAAAD8U9nktqncPIkBM" && p4.Captcha.BrowserStepCan, "script page: Turnstile with its site key found inside the component's captcha-html");
        var post4 = HostPostBuilder.Build(p4.Form, p4.Captcha, "TOKEN-T");
        Check(post4.Count(k => k.Value == "TOKEN-T") == 1 && post4.Select(k => k.Key).Take(6).SequenceEqual(new[] { "op", "id", "rand", "referer", "method_free", "dl_token" }), "script page: one token, in the response field, with the standard fields");
        Check(!HostFormParser.Parse("<my-widget rand=\"x\" class=\"a\"></my-widget>", "https://a.example/").Recognised, "a component without code and token is not taken for a download step");
    }
}
