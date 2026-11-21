// Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
namespace NDM.App;

/// <summary>Command line for explorer.exe. Explorer parses its own arguments, so the whole switch is one string with the path in quotes; adding it to ProcessStartInfo.ArgumentList would escape the quotes and Explorer would not find the file.</summary>
public static class ExplorerArguments
{
    /// <summary>/select,"C:\path\file.ext" (quote characters cannot occur in a Windows path and are dropped).</summary>
    public static string Select(string fullPath) => "/select,\"" + fullPath.Replace("\"", "") + "\"";
}
