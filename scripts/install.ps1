[CmdletBinding()]
param([string]$Root = '', [string]$AppPath = '')
$ErrorActionPreference = 'Stop'
if (-not $Root) { $Root = Split-Path -Parent $PSScriptRoot }
$Root = [IO.Path]::GetFullPath($Root)
$hostDir = Join-Path $Root 'dist\host'
$hostExe = Join-Path $hostDir 'NDM.NativeHost.exe'
if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) { throw "Build first: missing $hostExe" }
if (-not $AppPath) { $AppPath = Join-Path $Root 'dist\app\NeoDM.exe' }
$AppPath = [IO.Path]::GetFullPath($AppPath)
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) { throw "Missing app: $AppPath" }
$manifest = Get-Content -LiteralPath (Join-Path $Root 'extension\manifest.json') -Raw | ConvertFrom-Json
$sha = [Security.Cryptography.SHA256]::Create()
try { $hash = $sha.ComputeHash([Convert]::FromBase64String($manifest.key)) } finally { $sha.Dispose() }
$id = -join ($hash[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
# tools/extension-id.txt is the single recorded ID (written by tools/pack-extension.cjs --init-key); it must agree with the manifest key.
$idFile = Join-Path $Root 'tools\extension-id.txt'
if (Test-Path -LiteralPath $idFile -PathType Leaf) {
  $recorded = (Get-Content -LiteralPath $idFile -Raw).Trim()
  if ($recorded -ne $id) { throw "Extension ID mismatch: manifest key gives $id but tools/extension-id.txt says $recorded" }
}
# Trusted IDs: the development ID plus the Chrome Web Store ID from tools/extension-store-id.txt (when present and valid).
$ids = @($id)
$storeFile = Join-Path $Root 'tools\extension-store-id.txt'
if (Test-Path -LiteralPath $storeFile -PathType Leaf) {
  $storeId = (Get-Content -LiteralPath $storeFile -Raw).Trim()
  if ($storeId -cnotmatch '^[a-p]{32}$') { throw "tools/extension-store-id.txt is not a valid extension ID: $storeId" }
  if ($storeId -ne $id) { $ids += $storeId }
}
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $hostDir 'bridge-config.json'), (@{ extensionId=$id; extensionIds=@($ids); appPath=$AppPath } | ConvertTo-Json), $utf8)
$nativeManifest = Join-Path $hostDir 'com.ndm.bridge.json'
[IO.File]::WriteAllText($nativeManifest, (@{ name='com.ndm.bridge'; description='NDM native download bridge'; path=$hostExe; type='stdio'; allowed_origins=@($ids | ForEach-Object { "chrome-extension://$_/" }) } | ConvertTo-Json), $utf8)
# Same host manifest for each Chromium browser; only HKCU keys named com.ndm.bridge are written.
foreach ($vendor in 'Google\Chrome','Microsoft\Edge','BraveSoftware\Brave-Browser') {
  $registry = "HKCU:\Software\$vendor\NativeMessagingHosts\com.ndm.bridge"
  New-Item -Path $registry -Force | Out-Null
  Set-Item -Path $registry -Value $nativeManifest
  if ((Get-Item -LiteralPath $registry).GetValue('') -ne $nativeManifest) { throw "Registration verification failed for $vendor" }
}
Write-Host "Registered com.ndm.bridge for $id"
Write-Host 'Load extension/ unpacked in chrome://extensions with Developer mode enabled (Chrome/Edge/Brave block off-store CRX installs without policy). No browser profile settings were changed.'
