using System.Diagnostics;
using System.IO;
using System.Text;
using NDM.App;

internal static partial class SecurityTests
{
    private static (int Exit, string Output) RunPowerShell(string args, string programFiles)
    {
        var psi = new ProcessStartInfo("powershell.exe", args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.Environment["ProgramFiles"] = programFiles; psi.Environment["ProgramW6432"] = programFiles; psi.Environment["ProgramFiles(x86)"] = programFiles + " (x86)";
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit(60_000);
        return (p.ExitCode, output);
    }
    private static (int Exit, string Output) Configure(string dir, string programFiles, bool buildTime = false) =>
        RunPowerShell($"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(RepoRoot, "installer", "configure.ps1")}\" -Dir \"{dir}\" -AppExe NeoDM.exe -ExtId abcdefghijklmnopabcdefghijklmnop -Version 1.5.3" + (buildTime ? " -BuildTime" : ""), programFiles);
    private static void Mklink(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!; p.WaitForExit(); if (p.ExitCode != 0) throw new Exception("mklink failed");
    }
    private static void MakeInstallTree(string dir) { Directory.CreateDirectory(Path.Combine(dir, "host")); Directory.CreateDirectory(Path.Combine(dir, "extension")); }

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiOpenPackageW(string path, out IntPtr product);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiSetPropertyW(IntPtr product, string name, string value);
    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)] private static extern int MsiEvaluateConditionW(IntPtr product, string condition);
    [System.Runtime.InteropServices.DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiCloseHandle(IntPtr handle);

    /// <summary>Evaluates the real condition text stored in a built MSI with the Windows Installer engine (the package is opened, not installed).</summary>
    private static void MsiConditionTests(string msi)
    {
        var identity = MsiDatabase.Read(msi);
        string version = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props")), "<Version>([^<]+)</Version>").Groups[1].Value;
        if (identity is null || identity.ProductVersion != version) { Console.WriteLine($"SKIP: MSI condition evaluation: {msi} is not the {version} package yet (build-installer.ps1 runs after the suites)"); return; }
        string condition = ReadSequenceCondition(msi, "InstallExecuteSequence", "BadInstallFolder");
        Check(condition.Length > 0 && !condition.Contains("&lt;") && !condition.Contains("&quot;") && condition.Contains("~<<") && condition.Contains("><"), "MSI: the stored BadInstallFolder condition is plain operators, not XML entities: " + condition);
        Check(ReadSequenceCondition(msi, "InstallUISequence", "BadInstallFolder") == condition, "MSI: the UI sequence uses the same rule");
        Check(MsiOpenPackageW(msi, out var product) == 0, "MSI: the package opens for condition evaluation");
        try
        {
            string core = condition.Replace("NOT Installed AND NOT REMOVE~=\"ALL\" AND ", "");
            bool Bad(string folder, string pf64 = "C:\\Program Files\\", string pf32 = "C:\\Program Files (x86)\\")
            {
                MsiSetPropertyW(product, "ProgramFiles64Folder", pf64); MsiSetPropertyW(product, "ProgramFilesFolder", pf32); MsiSetPropertyW(product, "INSTALLFOLDER", folder);
                int r = MsiEvaluateConditionW(product, core);
                if (r is not (0 or 1)) throw new Exception("condition did not evaluate (" + r + "): " + core);
                return r == 1;
            }
            foreach (var good in new[] { @"C:\Program Files\NeoDM\", @"C:\Program Files\NeoDM", @"c:\program files\neodm\", @"C:\Program Files (x86)\NeoDM\", @"C:\Program Files\Some Vendor\NeoDM\" })
                Check(!Bad(good), "MSI engine: install folder allowed: " + good);
            foreach (var bad in new[] { @"D:\NeoDM\", @"C:\Users\me\NeoDM\", @"C:\NeoDM\", @"C:\Program Files-evil\NeoDM\", @"C:\Program FilesX\", @"C:\Program Files\..\Users\me\x\", @"C:\Program Files\NeoDM\..\..\Temp\", @"\\server\share\NeoDM\", @"D:\Program Files\NeoDM\", @"C:\ProgramData\NeoDM\" })
                Check(Bad(bad), "MSI engine: install folder refused: " + bad);
            Check(Bad(@"D:\Program Files\NeoDM\", pf64: @"D:\Program Files\") == false, "MSI engine: Program Files on another drive is accepted when that is where Windows keeps it");
        }
        finally { MsiCloseHandle(product); }
    }
    private static string ReadSequenceCondition(string msi, string table, string action) =>
        MsiDatabase.QueryString(msi, $"SELECT `Condition` FROM `{table}` WHERE `Action`='{action}'") ?? "";

    private static void InstallerFolderTests(string root)
    {
        MsiConditionTests(Path.Combine(RepoRoot, "artifacts", "NeoDM-Setup.msi"));
        // ---- the MSI source
        string wxs = File.ReadAllText(Path.Combine(RepoRoot, "installer", "NeoDM.wxs"));
        Check(wxs.Contains("ProgramFiles64Folder") && wxs.Contains("INSTALLFOLDER ~<< ProgramFiles64Folder") && wxs.Contains("INSTALLFOLDER ~<< ProgramFilesFolder") && wxs.Contains("INSTALLFOLDER >< \"..\"") && !wxs.Contains("&lt;&lt;"), "WiX: the install folder must start with a Program Files folder and contain no '..' (the define holds raw operators: a preprocessor define is not XML-decoded)");
        Check(wxs.Contains("<CustomAction Id=\"BadInstallFolder\" Error=\"") && wxs.Contains("<InstallExecuteSequence>\r\n      <Custom Action=\"BadInstallFolder\" After=\"CostFinalize\"") || wxs.Contains("<Custom Action=\"BadInstallFolder\" After=\"CostFinalize\""), "WiX: a hard condition stops the install when the folder is not allowed (MSI error text)");
        Check(System.Text.RegularExpressions.Regex.Matches(wxs, "Action=\"BadInstallFolder\"").Count >= 2, "WiX: the rule is in both the UI and the execute sequence (a silent install is stopped too)");
        Check(wxs.Contains("BadFolderDlg") && wxs.Contains("Value=\"BadFolderDlg\"") && wxs.Contains("Value=\"InstallDirDlg\" Order=\"7\""), "WiX: the folder page shows a message and stays on the page");
        Check(wxs.Contains("Id=\"HardenFolder\"") && wxs.Contains("/inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX") && wxs.Contains("Action=\"HardenFolder\" After=\"InstallFiles\"") && wxs.Contains("Custom Action=\"Configure\" After=\"HardenFolder\""), "WiX: SYSTEM and Administrators get full control, Users read and run, before Configure runs");
        Check(wxs.Contains("<CustomAction Id=\"HardenFolder\"") && wxs.Contains("Return=\"ignore\""), "WiX: hardening the ACL is best effort");
        Check(File.ReadAllText(Path.Combine(RepoRoot, "build-installer.ps1")).Contains("-BuildTime"), "build-installer.ps1 is the only caller of -BuildTime");

        // ---- configure.ps1 (run with ProgramFiles pointed at a scratch folder, so no real Program Files write is needed)
        string pf = Path.Combine(root, "PF"); Directory.CreateDirectory(pf);
        string good = Path.Combine(pf, "NeoDM"); MakeInstallTree(good);
        var ok = Configure(good + "\\.", pf);
        Check(ok.Exit == 0 && File.Exists(Path.Combine(good, "host", "bridge-config.json")) && File.Exists(Path.Combine(good, "host", "com.ndm.bridge.json")) && File.Exists(Path.Combine(good, "extension", "update.xml")), "configure.ps1: an install folder under Program Files is configured: " + ok.Output.Trim());
        string outside = Path.Combine(root, "outside"); MakeInstallTree(outside);
        var bad = Configure(outside + "\\.", pf);
        Check(bad.Exit != 0 && bad.Output.Contains("not under Program Files") && !Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories).Any(), "configure.ps1: a folder outside Program Files is refused and nothing is written");
        string prefix = pf + "-evil"; MakeInstallTree(Path.Combine(prefix, "NeoDM"));
        Check(Configure(Path.Combine(prefix, "NeoDM") + "\\.", pf).Exit != 0 && !Directory.EnumerateFiles(prefix, "*", SearchOption.AllDirectories).Any(), "configure.ps1: a folder that only shares the text prefix of Program Files is refused");
        string trick = pf + "\\NeoDM\\..\\..\\outside";
        var dotted = Configure(trick, pf);
        Check(dotted.Exit != 0 && !File.Exists(Path.Combine(outside, "host", "bridge-config.json")), "configure.ps1: '..' segments are refused");
        var build = Configure(outside + "\\.", pf, buildTime: true);
        Check(build.Exit == 0 && File.Exists(Path.Combine(outside, "host", "bridge-config.json")), "configure.ps1 -BuildTime: build-installer.ps1 can still generate its payload in its own folder: " + build.Output.Trim());

        // reparse points
        string redirected = Path.Combine(root, "redirected-host"); Directory.CreateDirectory(redirected);
        string linked = Path.Combine(pf, "NeoDM-linked-host"); Directory.CreateDirectory(Path.Combine(linked, "extension")); Mklink(Path.Combine(linked, "host"), redirected);
        var viaLink = Configure(linked + "\\.", pf);
        Check(viaLink.Exit != 0 && viaLink.Output.Contains("reparse point") && !Directory.EnumerateFiles(redirected).Any(), "configure.ps1: a junction as the host folder is refused and nothing is written through it");
        string target = Path.Combine(root, "real-install"); MakeInstallTree(target);
        string linkedRoot = Path.Combine(pf, "NeoDM-linked-root"); Mklink(linkedRoot, target);
        var viaRoot = Configure(linkedRoot + "\\.", pf);
        Check(viaRoot.Exit != 0 && viaRoot.Output.Contains("reparse point") && !Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any(), "configure.ps1: a junction as the install folder is refused");
        string linkedParent = Path.Combine(pf, "parent-link"); Mklink(linkedParent, target);
        var viaParent = Configure(Path.Combine(linkedParent, "x") + "\\.", pf);
        Check(viaParent.Exit != 0 && !Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any(), "configure.ps1: a junction higher up the path is refused");
        // the -Uninstall mode does not write files and is not blocked
        var un = RunPowerShell($"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(RepoRoot, "installer", "configure.ps1")}\" -Dir \"{outside}\" -ExtId abcdefghijklmnopabcdefghijklmnop -Uninstall", pf);
        Check(un.Exit == 0, "configure.ps1 -Uninstall still works: " + un.Output.Trim());
        foreach (var link in new[] { Path.Combine(linked, "host"), linkedRoot, linkedParent }) { try { Directory.Delete(link); } catch (IOException) { } }
    }
}
