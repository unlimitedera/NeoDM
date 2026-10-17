using System.Diagnostics;
using System.IO;
using System.Text.Json;

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
        "at the top right of the page");
    public static readonly GuideBrowser Edge = new("Edge", "Edge", "edge", "msedge.exe",
        new[] { @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" },
        "on the left side of the page");
    public static readonly GuideBrowser Brave = new("Brave", "Brave", "brave", "brave.exe",
        new[] { @"%ProgramFiles%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%ProgramFiles(x86)%\BraveSoftware\Brave-Browser\Application\brave.exe", @"%LocalAppData%\BraveSoftware\Brave-Browser\Application\brave.exe" },
        "at the top right of the page");
    public static readonly GuideBrowser[] Browsers = { Chrome, Edge, Brave };

    public const string ExtensionName = "NeoDM Integration";
    public const string ImageDeveloperMode = "step-developer-mode.png", ImageLoadUnpacked = "step-load-unpacked.png", ImageFolderPicker = "step-folder-picker.png", ImageExtensionCard = "step-extension-card.png";
    public static readonly string[] Images = { ImageDeveloperMode, ImageLoadUnpacked, ImageFolderPicker, ImageExtensionCard };
    public static Uri ImageUri(string name) => new("pack://application:,,,/NeoDM;component/Assets/Guide/" + name);

    public const string PendingApproval = "The Chrome Web Store listing is waiting for approval. Until then, install the extension manually with these steps.";
    public const string PendingApprovalDoc = "NeoDM Integration has been submitted to the Chrome Web Store and is waiting for approval. Until it is approved, install it manually with Load unpacked, following the guide.";
    public const string SameForAllBrowsers = "The screenshots are from Chrome. Edge and Brave use the same layout, so they apply to all three browsers.";
    public const string RemoveWhy = "Two copies of the extension fight over the same downloads, and the browser keeps loading the folder it was first installed from.";
    public const string DeveloperModeAll = "Turn on Developer mode. In Chrome and Brave the toggle is at the top right of the page. In Edge it is on the left side of the page.";
    public static string DeveloperModeLine(GuideBrowser browser) => $"Turn on Developer mode. In {browser.Name} the toggle is {browser.DeveloperModeWhere}.";

    /// <summary>Steps in order. <paramref name="appVersion"/> is the version step 4 tells the user to look for; a null browser gives the text for all three browsers.</summary>
    public static IReadOnlyList<GuideStep> Steps(string appVersion, GuideBrowser? browser = null) => new[]
    {
        new GuideStep(0, "If you already installed NeoDM Integration, remove the old one first.", new[]
        {
            "Open the extensions page with the address above.",
            "Find NeoDM Integration.",
            "Click Remove and confirm.",
            RemoveWhy,
        }, ImageExtensionCard),
        new GuideStep(1, "Turn on Developer mode.", new[] { browser is null ? DeveloperModeAll : DeveloperModeLine(browser) }, ImageDeveloperMode),
        new GuideStep(2, "Click Load unpacked.", new[] { "The button appears after Developer mode is on." }, ImageLoadUnpacked),
        new GuideStep(3, "Choose the NeoDM extension folder.", new[] { "Paste the path below into the folder picker, or browse to it, then click Select Folder." }, ImageFolderPicker),
        new GuideStep(4, $"Check that NeoDM Integration shows version {appVersion} and is switched on.", new[]
        {
            "Pin it from the puzzle piece menu in the toolbar.",
            "Open its popup and click Test connection. It should say Host connected.",
        }, ImageExtensionCard),
        new GuideStep(5, "After a NeoDM update that changes the extension, install it again.", new[]
        {
            "Repeat step 0, then step 2. Or click the reload arrow on the NeoDM Integration card.",
            "Chrome and Edge show a notice about developer mode extensions each time the browser starts. Close it. The extension keeps working.",
        }, null),
    };

    public static readonly (string Problem, string Answer)[] Troubleshooting =
    {
        ("The browser blocks the extension and mentions its store.", "That is expected for a .crx file. Do not open the .crx. Use Load unpacked with the folder from step 3."),
        ("Test connection says the host is unavailable.", "Install NeoDM with its setup program, which registers the browser connection. Then restart the browser and try again."),
        ("The extension stopped working after I moved or deleted the folder.", "The browser remembers the folder path. Remove the extension and load the folder again from step 3."),
    };

    public const string NotFoundMessage = "The extension folder was not found. Reinstall NeoDM to restore it.";

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
            return document.RootElement.TryGetProperty("name", out var name) && name.GetString() == ExtensionName;
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
        if (exe is null) return $"{browser.Name} was not found on this computer. Copy the address and paste it into the browser you use.";
        try { StartProgram(exe, browser.Url); return ""; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return $"{browser.Name} could not be started. Copy the address and paste it into the browser."; }
    }

    public static string OpenFolder(string? path)
    {
        if (path is null || !Directory.Exists(path)) return NotFoundMessage;
        try { StartProgram("explorer.exe", "\"" + path + "\""); return ""; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return "The folder could not be opened. Copy the path instead."; }
    }
}
