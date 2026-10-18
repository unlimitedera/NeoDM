using System.Diagnostics;
using System.IO;
using System.Text.Json;

using NDM.App.Localization;
namespace NDM.App;

public sealed record GuideBrowser(string Id, string Name, string Scheme, string ExeName, string[] DefaultPaths, string DeveloperModeWhere)
{
    public string Url => Scheme + "://extensions";
}

public enum ExtensionFolderKind { Installed, Development, Missing }
public sealed record ExtensionFolder(ExtensionFolderKind Kind, string? Path);

/// <summary>One numbered step of the install guide. The text is shared with docs/EXTENSION-INSTALL.md (a test compares them).</summary>
public sealed record GuideStep(int Number, string Title, string[] Lines, string? Image);

/// <summary>Content and helpers for Help > Browser extension install guide.</summary>
public static class ExtensionGuide
{
    public static readonly GuideBrowser Chrome = new("Chrome", "Chrome", "chrome", "chrome.exe",
        new[] { @"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe", @"%LocalAppData%\Google\Chrome\Application\chrome.exe" },
        "guide.where.top_right");
    public static readonly GuideBrowser Edge = new("Edge", "Edge", "edge", "msedge.exe",
        new[] { @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" },
        "guide.where.left");
    public static readonly GuideBrowser Brave = new("Brave", "Brave", "brave", "brave.exe",
        new[] { @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe" },
        "guide.where.top_right");
    public static readonly GuideBrowser[] Browsers = { Chrome, Edge, Brave };

    public const string ExtensionName = "NeoDM Integration";
    /// <summary>The manifest name is a chrome.i18n message reference since 1.5.0.</summary>
    public const string ExtensionNameMessage = "__MSG_ext_name__";
    public const string ImageDeveloperMode = "step-developer-mode.png", ImageLoadUnpacked = "step-load-unpacked.png", ImageFolderPicker = "step-folder-picker.png", ImageExtensionCard = "step-extension-card.png";
    public static readonly string[] Images = { ImageDeveloperMode, ImageLoadUnpacked, ImageFolderPicker, ImageExtensionCard };
    public static Uri ImageUri(string name) => new("pack://application:,,,/NeoDM;component/Assets/Guide/" + name);

    public static string PendingApproval => Loc.T("guide.pending");
    public static string PendingApprovalDoc => Loc.T("guide.pending_doc");
    public static string SameForAllBrowsers => Loc.T("guide.same_browsers");
    public static string RemoveWhy => Loc.T("guide.remove_why");
    public static string DeveloperModeAll => Loc.T("guide.devmode_all");
    public static string DeveloperModeLine(GuideBrowser browser) => Loc.T("guide.devmode_line", browser.Name, Loc.T(browser.DeveloperModeWhere));

    /// <summary>Steps in order. <paramref name="appVersion"/> is the version step 4 tells the user to look for; a null browser gives the text for all three browsers.</summary>
    public static IReadOnlyList<GuideStep> Steps(string appVersion, GuideBrowser? browser = null) => new[]
    {
        new GuideStep(0, Loc.T("guide.step0.title"), new[]
        {
            Loc.T("guide.step0.l1"),
            Loc.T("guide.step0.l2"),
            Loc.T("guide.step0.l3"),
            RemoveWhy,
        }, ImageExtensionCard),
        new GuideStep(1, Loc.T("guide.step1.title"), new[] { browser is null ? DeveloperModeAll : DeveloperModeLine(browser) }, ImageDeveloperMode),
        new GuideStep(2, Loc.T("guide.step2.title"), new[] { Loc.T("guide.step2.l1") }, ImageLoadUnpacked),
        new GuideStep(3, Loc.T("guide.step3.title"), new[] { Loc.T("guide.step3.l1") }, ImageFolderPicker),
        new GuideStep(4, Loc.T("guide.step4.title", appVersion), new[]
        {
            Loc.T("guide.step4.l1"),
            Loc.T("guide.step4.l2"),
        }, ImageExtensionCard),
        new GuideStep(5, Loc.T("guide.step5.title"), new[]
        {
            Loc.T("guide.step5.l1"),
            Loc.T("guide.step5.l2"),
        }, null),
    };

    public static readonly (string Problem, string Answer)[] Troubleshooting =
    {
        (Loc.T("guide.trouble1.q"), Loc.T("guide.trouble1.a")),
        (Loc.T("guide.trouble2.q"), Loc.T("guide.trouble2.a")),
        (Loc.T("guide.trouble3.q"), Loc.T("guide.trouble3.a")),
    };

    public static string NotFoundMessage => Loc.T("guide.not_found");

    /// <summary>The unpacked extension folder: the installed layout first (extension\unpacked next to NeoDM.exe), then the development tree
    /// (an extension folder in a parent of the app folder), else Missing. A folder counts only when it holds the NeoDM Integration manifest.</summary>
    public static ExtensionFolder ResolveFolder(string? baseDirectory = null, Func<string, bool>? isExtensionFolder = null)
    {
        isExtensionFolder ??= IsExtensionFolder;
        string start = (baseDirectory ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            string installed = Path.GetFullPath(Path.Combine(start, "extension", "unpacked"));
            if (isExtensionFolder(installed)) return new(ExtensionFolderKind.Installed, installed);
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string dev = Path.Combine(dir.FullName, "extension");
                if (isExtensionFolder(dev)) return new(ExtensionFolderKind.Development, dev);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        return new(ExtensionFolderKind.Missing, null);
    }

    public static bool IsExtensionFolder(string folder)
    {
        try
        {
            string manifest = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifest)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("name", out var name) && name.GetString() is ExtensionName or ExtensionNameMessage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Path of the browser program, or null when it is not installed. Looks at the App Paths registry key (machine, then user), then the default install folders.</summary>
    public static string? FindBrowser(GuideBrowser browser, Func<string, string?>? registryPath = null, Func<string, bool>? exists = null)
    {
        registryPath ??= AppPathFromRegistry;
        exists ??= File.Exists;
        var candidates = new List<string>();
        if (registryPath(browser.ExeName) is { Length: > 0 } registered) candidates.Add(registered.Trim('"'));
        candidates.AddRange(browser.DefaultPaths.Select(Environment.ExpandEnvironmentVariables));
        return candidates.FirstOrDefault(p => p.Length > 0 && !p.Contains('%') && exists(p));
    }

    private static string? AppPathFromRegistry(string exeName)
    {
        foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (key?.GetValue(null) as string is { Length: > 0 } path) return path;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    /// <summary>Test seam for starting programs.</summary>
    public static Action<string, string> StartProgram { get; set; } = (file, arguments) =>
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false });
    };

    /// <summary>Opens the browser's extensions page. A running browser opens it in a new tab of the same instance, so nothing is closed or restarted.
    /// Returns an empty string on success, otherwise the sentence to show.</summary>
    public static string OpenExtensionsPage(GuideBrowser browser, Func<GuideBrowser, string?>? find = null)
    {
        string? exe = (find ?? (b => FindBrowser(b)))(browser);
        if (exe is null) return Loc.T("guide.browser_missing", browser.Name);
        try { StartProgram(exe, browser.Url); return ""; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return Loc.T("guide.browser_failed", browser.Name); }
    }

    public static string OpenFolder(string? path)
    {
        if (path is null || !Directory.Exists(path)) return NotFoundMessage;
        try { StartProgram("explorer.exe", "\"" + path + "\""); return ""; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return Loc.T("guide.folder_failed"); }
    }
}
