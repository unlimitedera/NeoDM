$ErrorActionPreference = 'Stop'
# Lists the visible top-level windows with a title as "class|title" lines (used by the real-browser tests to notice any external window that pops up).
Add-Type -TypeDefinition 'using System;using System.Text;using System.Runtime.InteropServices;public class W{public delegate bool P(IntPtr h,IntPtr l);[DllImport("user32.dll")]public static extern bool EnumWindows(P p,IntPtr l);[DllImport("user32.dll")]public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);[DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr h);[DllImport("user32.dll")]public static extern int GetClassName(IntPtr h,StringBuilder s,int n);}'
$r = New-Object System.Collections.Generic.List[string]
[W]::EnumWindows({ param($h, $l) if ([W]::IsWindowVisible($h)) { $t = New-Object Text.StringBuilder 256; $c = New-Object Text.StringBuilder 256; [void][W]::GetWindowText($h, $t, 256); [void][W]::GetClassName($h, $c, 256); if ($t.Length -gt 0) { $r.Add($c.ToString() + '|' + $t.ToString()) } }; $true }, [IntPtr]::Zero) | Out-Null
$r -join [Environment]::NewLine
