# Install-time (and uninstall-time) helper run by the MSI as a deferred, elevated custom action.
#  install   : writes host manifest, bridge config, extension update.xml; removes stale per-user com.ndm.bridge keys; optionally the ExtensionInstallForcelist policy for detected browsers
#  -Uninstall: removes only the policy value this installer wrote (value name 7421 whose data starts with the NeoDM extension ID)
param([Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][string]$ExtId, [string]$AppExe, [string]$Version, [int]$Policy = 0, [switch]$Uninstall, [switch]$BuildTime)
$ErrorActionPreference = 'Stop'
$Dir = $Dir.TrimEnd('\', '.')
$policyName = '7421'
$browsers = @(
  @{ Exe = 'chrome.exe'; Key = 'SOFTWARE\Policies\Google\Chrome' },
  @{ Exe = 'msedge.exe'; Key = 'SOFTWARE\Policies\Microsoft\Edge' },
  @{ Exe = 'brave.exe';  Key = 'SOFTWARE\Policies\BraveSoftware\Brave' })

if ($Uninstall) {
  foreach ($b in $browsers) {
    $list = "HKLM:\$($b.Key)\ExtensionInstallForcelist"
    if (-not (Test-Path -LiteralPath $list)) { continue }
    $v = (Get-ItemProperty -LiteralPath $list -ErrorAction SilentlyContinue).$policyName
    if ($v -and $v.StartsWith("$ExtId;")) { Remove-ItemProperty -LiteralPath $list -Name $policyName }
    # Drop keys we may have created, but only when empty (never touch other policies).
    foreach ($k in @($list, "HKLM:\$($b.Key)", "HKLM:\$(Split-Path $b.Key -Parent)")) {
      if ($k -in 'HKLM:\SOFTWARE\Policies', 'HKLM:\SOFTWARE') { continue }
      $i = Get-Item -LiteralPath $k -ErrorAction SilentlyContinue
      if ($i -and $i.ValueCount -eq 0 -and $i.SubKeyCount -eq 0) { Remove-Item -LiteralPath $k }
    }
  }
  return
}

# This script runs as SYSTEM. Refuse to write anywhere a standard user could have planted a link or chosen the place:
#  - the install folder must be under Program Files (the MSI enforces this too; checked again here because the folder is an input),
#  - no directory on the way, and none of the files written, may be a reparse point (symlink or junction).
# build-installer.ps1 passes -BuildTime to generate the payload files in its own scratch folder.
function Assert-SafeTarget([string]$Root) {
  if ($BuildTime) { return }
  $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
  $allowed = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramW6432) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') + '\' }
  $base = $allowed | Where-Object { ($full + '\').StartsWith($_, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
  if (-not $base) { throw "Install folder is not under Program Files: $full" }
  if ($Root -match '(^|[\\/])\.\.([\\/]|$)') { throw "Install folder contains '..': $Root" }
  $probe = $full
  while ($probe -and $probe.Length -gt 3 -and ($probe + '\').Length -gt $base.Length) {   # the folders between Program Files and the install folder (Program Files itself is machine-owned)
    $item = Get-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse point: $probe" }
    $probe = Split-Path -Parent $probe
  }
  foreach ($sub in 'host', 'extension') {
    $d = Join-Path $full $sub
    $item = Get-Item -LiteralPath $d -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse point: $d" }
  }
  foreach ($file in 'host\bridge-config.json', 'host\com.ndm.bridge.json', 'extension\update.xml') {
    $item = Get-Item -LiteralPath (Join-Path $full $file) -Force -ErrorAction SilentlyContinue
    if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing reparse point: $($item.FullName)" }
  }
}
Assert-SafeTarget $Dir

$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText("$Dir\host\bridge-config.json", (@{ extensionId = $ExtId; appPath = "$Dir\$AppExe" } | ConvertTo-Json), $utf8)
[IO.File]::WriteAllText("$Dir\host\com.ndm.bridge.json", (@{ name = 'com.ndm.bridge'; description = 'NeoDM native download bridge'; path = "$Dir\host\NDM.NativeHost.exe"; type = 'stdio'; allowed_origins = @("chrome-extension://$ExtId/") } | ConvertTo-Json), $utf8)
$base = 'file:///' + (($Dir -replace '\\', '/') -replace ' ', '%20') + '/extension'
$xml = "<?xml version='1.0' encoding='UTF-8'?>`r`n<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'><app appid='$ExtId'><updatecheck codebase='$base/NeoDM.crx' version='$Version'/></app></gupdate>`r`n"
[IO.File]::WriteAllText("$Dir\extension\update.xml", $xml, $utf8)

# A per-user com.ndm.bridge key wins over the machine key in Chrome, Edge, Brave and Chromium. Remove the ones whose manifest file no longer exists
# (left behind by an older copy of the app). Keys that point to an existing manifest stay. Only the com.ndm.bridge subkey is touched.
foreach ($vendor in 'Google\Chrome', 'Microsoft\Edge', 'BraveSoftware\Brave-Browser', 'Chromium') {
  try {
    $userKey = "HKCU:\SOFTWARE\$vendor\NativeMessagingHosts\com.ndm.bridge"
    if (-not (Test-Path -LiteralPath $userKey)) { continue }
    $target = (Get-ItemProperty -LiteralPath $userKey -ErrorAction SilentlyContinue).'(default)'
    if ([string]::IsNullOrWhiteSpace($target) -or -not (Test-Path -LiteralPath $target -PathType Leaf)) { Remove-Item -LiteralPath $userKey -Recurse -Force }
  } catch { }
}

if ($Policy -eq 1) {
  foreach ($b in $browsers) {
    $ap = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$($b.Exe)" -ErrorAction SilentlyContinue
    if (-not $ap -or -not (Test-Path -LiteralPath $ap.'(default)')) { continue }
    $list = "HKLM:\$($b.Key)\ExtensionInstallForcelist"
    New-Item -Path $list -Force | Out-Null
    Set-ItemProperty -LiteralPath $list -Name $policyName -Value "$ExtId;$base/update.xml" -Type String
  }
}
