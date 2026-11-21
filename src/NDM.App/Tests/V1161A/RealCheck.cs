// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
using System.Diagnostics;
using NDM.App;
using NDM.Core;

internal static partial class V1161ATests
{
    /// <summary>The approved one-time check on four tiny test links: plain requests, the site's own timer waited in real time, the step-1 form posted as a browser would,
    /// the step-2 page read. The human step is a recorder that SKIPS: nothing is clicked, solved or posted with a token, nothing is downloaded. Only field NAMES are printed, never values, cookies or tokens.</summary>
    private static void RealCheck(string scratch)
    {
        var only = Environment.GetEnvironmentVariable("NEODM_REAL_ONLY"); var urls = new[] { "https://datanodes.to/li77lxy835bq/Part_1.zip", "https://datanodes.to/uock9c61or8s/Part_2.zip", "https://datanodes.to/1yrwl4sc8fro/Part_3.zip", "https://datanodes.to/pg0a1r450vpd/Part_4.zip" }.Where(u => only is null || u.EndsWith(only)).ToArray();
        var keyFile = Path.Combine(scratch, "sitekey.txt");
        foreach (var url in urls)
        {
            var name = Path.GetFileName(url); var sw = Stopwatch.StartNew(); var rec = new Recorder();
            var engine = new HostFormEngine
            {
                Captcha = rec,
                InspectReply = (step, status, text) => { File.WriteAllText(Path.Combine("F:/NDM/artifacts/tmp", name + ".step" + step + "-reply.html"), text); Console.WriteLine($"REAL {name} step {step} POST answer: status={status} bytes={text.Length} file={Path.Combine("F:/NDM/artifacts/tmp", name + ".step" + step + "-reply.html")}"); },
                Inspect = (step, page) => Console.WriteLine($"REAL {name} step {step}: form action-path={(Uri.TryCreate(page.Form!.Action, UriKind.RelativeOrAbsolute, out var a) && a.IsAbsoluteUri ? a.AbsolutePath : page.Form.Action)} method={page.Form.Method} fields=[{string.Join(",", page.Form.Fields.Select(f => f.Name + (f.Value.Length == 0 ? "(empty)" : "")))}] submit=\"{page.Form.SubmitLabel}\" submitDisabled={page.Form.SubmitDisabled} countdown={(page.CountdownSeconds?.ToString() ?? "none")} captcha={page.Captcha.Kind} sitekeyPresent={page.Captcha.SiteKey is { Length: > 0 }} error={(page.ErrorText is null ? "none" : "yes")} links={page.Links.Count}"),
            };
            var item = Item(url, name); var task = Task.Run(() => engine.ResolveAsync(item, () => false, CancellationToken.None));
            while (!task.IsCompleted) Pump(100);
            var r = task.GetAwaiter().GetResult();
            Console.WriteLine($"REAL {name}: stopped at state={item.State} unrecognised={r.Unrecognised} waitedSeconds={engine.LastWaitedSeconds} elapsed={sw.Elapsed.TotalSeconds:0.0}s captchaStepReached={rec.Calls} kind={rec.Kind} sitekeyPresent={rec.Key is { Length: > 0 }} detail={item.DetailKey ?? r.Detail ?? "none"}");
            if (rec.Key is { Length: > 0 }) File.WriteAllText(keyFile, rec.Key); // a site key is public page markup, not a secret; used only by the widget check
        }
        Console.WriteLine("REAL CHECK DONE (nothing clicked, nothing solved, nothing downloaded)");
    }

    private sealed class Recorder : IHumanCaptchaStep
    {
        public int Calls; public HostCaptchaKind Kind; public string? Key;
        public Task<CaptchaOutcome> RunAsync(HostCaptcha captcha, Uri page, CancellationToken token, Func<bool> skip) { Calls++; Kind = captcha.Kind; Key = captcha.SiteKey; return Task.FromResult(CaptchaOutcome.Skip); }
    }
}
