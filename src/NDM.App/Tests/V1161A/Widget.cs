using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NDM.App;
using NDM.Core;

internal static partial class V1161ATests
{
    /// <summary>Renders the local page in a WebView2 control (a private scratch profile) and only LOOKS: no click, no script on the widget. Google's public test key stands in for a host's key,
    /// because a host's own key is only visible on its second step. The check says whether the widget loads from the 127.0.0.1 origin and whether it shows an invalid-domain error.</summary>
    private static void WidgetRender(string scratch)
    {
        Task? t = null; Application.Current.Dispatcher.BeginInvoke(new Action(() => t = WidgetRenderAsync(scratch)));
        var end = DateTime.UtcNow.AddSeconds(90); while ((t is null || !t.IsCompleted) && DateTime.UtcNow < end) Pump(50);
        if (t is null || !t.IsCompleted) throw new TimeoutException("widget render did not finish"); t.GetAwaiter().GetResult();
    }

    private static async Task WidgetRenderAsync(string scratch)
    {
        var key = Environment.GetEnvironmentVariable("NEODM_TEST_SITEKEY") ?? "6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI";
        bool turnstile = Environment.GetEnvironmentVariable("NEODM_TEST_KIND") == "turnstile";
        using var l = new CaptchaListener(turnstile ? new HostCaptcha(HostCaptchaKind.Turnstile, key) : Re(key), PageText, TimeSpan.FromMinutes(2));
        var web = new WebView2(); var win = new Window { Width = 520, Height = 420, Content = web, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 0 };
        win.Show();
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(scratch, "wv"));
        await web.EnsureCoreWebView2Async(env);
        var loaded = new TaskCompletionSource<bool>(); web.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult(true);
        web.CoreWebView2.Navigate(l.Url); await loaded.Task;
        string info = "";
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(700);
            info = await web.CoreWebView2.ExecuteScriptAsync("(() => { const f = document.querySelectorAll('iframe'); return JSON.stringify({ frames: f.length, api: typeof window.turnstile + '/' + typeof window.grecaptcha, box: Array.from(document.querySelectorAll('.cf-turnstile,.g-recaptcha')).map(x => x.childElementCount + ':' + (x.shadowRoot ? 'shadow' : 'noshadow') + ':' + x.getBoundingClientRect().height), src: Array.from(f).map(x => (x.src||'').split('?')[0]).slice(0,3), text: (document.body.innerText||'').slice(0,200) }); })()");
            if (info.Contains("frames\\\":1") || info.Contains("frames\\\":2")) break;
        }
        Console.WriteLine("WIDGET RENDER (key " + (key == "6LeIxAcTAAAAAJcZVRqyHh71UMIEGNQ_MXjiZKhI" ? "Google public test key" : "custom") + ", origin http://127.0.0.1): " + info);
        Check(l.PageServed >= 1, "the local page was served to the embedded browser control");
        bool widget = info.Contains(turnstile ? "challenges.cloudflare.com" : "recaptcha", StringComparison.OrdinalIgnoreCase); bool invalid = info.Contains("Invalid domain", StringComparison.OrdinalIgnoreCase) || info.Contains("invalid domain", StringComparison.OrdinalIgnoreCase) || info.Contains("ERROR for site owner", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"WIDGET RESULT: iframe from the widget service present={widget}, invalid-domain message present={invalid}");
        Directory.CreateDirectory("F:/NDM/artifacts/shots-1161a"); await using (var fs = File.Create("F:/NDM/artifacts/shots-1161a/widget-127001.png")) await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        win.Close();
    }
}
