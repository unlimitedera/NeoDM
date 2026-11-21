# Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
[CmdletBinding(SupportsShouldProcess=$true)]
param()
$ErrorActionPreference = 'Stop'
foreach ($vendor in 'Google\Chrome','Microsoft\Edge','BraveSoftware\Brave-Browser') {
  $key = "HKCU:\Software\$vendor\NativeMessagingHosts\com.ndm.bridge"
  if ((Test-Path -LiteralPath $key) -and $PSCmdlet.ShouldProcess($key,'Remove only NeoDM native host registration')) {
    Remove-Item -LiteralPath $key -Recurse
    if (Test-Path -LiteralPath $key) { throw "NeoDM registration removal failed for $vendor" }
  }
}
Write-Host 'NeoDM registration removed (or was absent). No other host registrations, Chrome profiles or download files touched.'
