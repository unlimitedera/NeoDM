[CmdletBinding()]
param([string]$AppExe = 'NeoDM.exe', [string]$PolicyDefault = "0")
# Builds artifacts\NeoDM-Setup.msi from dist\ (run build.ps1 first). Requires: dotnet tool install --global wix --version 5.0.2
# and: wix extension add -g WixToolset.UI.wixext/5.0.2 ; wix extension add -g WixToolset.Util.wixext/5.0.2
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$inst = Join-Path $root 'installer'
$AppDir = Join-Path $root 'dist\app'; $HostDir = Join-Path $root 'dist\host'; $ExtDir = Join-Path $root 'extension'
$Crx = Join-Path $root 'dist\NeoDM.crx'
foreach ($p in @("$AppDir\$AppExe", "$HostDir\NDM.NativeHost.exe", $Crx, "$ExtDir\manifest.json")) { if (-not (Test-Path -LiteralPath $p)) { throw "Missing input: $p" } }
# Trusted extension IDs: the development/unpacked ID (tools/extension-id.txt, from the manifest key) first, then the Chrome Web Store ID (tools/extension-store-id.txt).
$devExtId = (Get-Content -LiteralPath (Join-Path $root 'tools\extension-id.txt') -Raw).Trim()
$storeExtId = (Get-Content -LiteralPath (Join-Path $root 'tools\extension-store-id.txt') -Raw).Trim()
foreach ($id in $devExtId, $storeExtId) { if ($id -cnotmatch '^[a-p]{32}$') { throw "Not a valid extension ID: $id" } }
if ($devExtId -eq $storeExtId) { throw 'The store extension ID must differ from the development ID' }
$extId = "$devExtId,$storeExtId"
# Single source of truth: Directory.Build.props (docs/VERSIONING.md). The extension manifest and the built app must agree (build.ps1 syncs them).
$version = [regex]::Match((Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw), '<Version>\s*(\d+\.\d+\.\d+)\s*</Version>').Groups[1].Value
if (-not $version) { throw 'Directory.Build.props has no <Version>' }
$manifestVersion = (Get-Content -LiteralPath "$ExtDir\manifest.json" -Raw | ConvertFrom-Json).version
if ($manifestVersion -ne $version) { throw "Extension manifest version $manifestVersion != product version $version; run build.ps1" }
$appInfo = (Get-Item -LiteralPath "$AppDir\$AppExe").VersionInfo.ProductVersion
if ($appInfo -notlike "$version*") { throw "dist\app is version $appInfo, expected $version; run build.ps1" }
$payload = Join-Path $inst 'payload'; New-Item -ItemType Directory -Force $payload | Out-Null
# Placeholders for generated files (rewritten at install time by configure.ps1 so the MSI owns/removes them).
New-Item -ItemType Directory -Force "$payload\x\host", "$payload\x\extension" | Out-Null
& (Join-Path $inst 'configure.ps1') -Dir "$payload\x" -AppExe $AppExe -ExtId $extId -Version $version -BuildTime
Move-Item -Force "$payload\x\host\*.json" $payload; Move-Item -Force "$payload\x\extension\update.xml" $payload
Remove-Item -Recurse -Force "$payload\x"
# MIT license as RTF for the license dialog.
$lic = Get-Content -LiteralPath (Join-Path $root 'LICENSE') -Raw
$rtf = '{\rtf1\ansi\deff0{\fonts{\f0 Segoe UI;}}\f0\fs20 ' + (($lic -replace '\\', '\\\\' -replace '\{', '\{' -replace '\}', '\}') -replace "`r?`n", '\par ') + '}'
$rtfPath = Join-Path $payload 'license.rtf'; [IO.File]::WriteAllText($rtfPath, $rtf, [Text.Encoding]::ASCII)
$licTxt = Join-Path $root 'LICENSE'
# Installer artwork generated from the NeoDM icon (original artwork, no third-party images): UI banner 493x58, welcome/exit dialog 493x312.
Add-Type -AssemblyName System.Drawing
$IconFile = Join-Path $root 'src\NDM.App\Assets\NDM.ico'
if (-not (Test-Path -LiteralPath $IconFile)) { throw "Missing installer icon: $IconFile" }
function New-InstallerBitmap([string]$Path, [int]$W, [int]$H, [bool]$Dialog) {
    $icon = New-Object System.Drawing.Icon($IconFile, 48, 48)
    $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'ClearTypeGridFit'
    $g.Clear([System.Drawing.Color]::White)
    if ($Dialog) {
        $panel = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle(0, 0, 164, $H)), [System.Drawing.Color]::FromArgb(31, 41, 55), [System.Drawing.Color]::FromArgb(37, 99, 235), 90)
        $g.FillRectangle($panel, 0, 0, 164, $H)
        $g.DrawIcon($icon, (New-Object System.Drawing.Rectangle(58, 40, 48, 48)))
        $f = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Bold)
        $g.DrawString('NeoDM', $f, [System.Drawing.Brushes]::White, 38, 100)
        $f.Dispose(); $panel.Dispose()
    } else {
        $g.DrawIcon($icon, (New-Object System.Drawing.Rectangle(($W - 56), 5, 48, 48)))
        $bar = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(37, 99, 235))
        $g.FillRectangle($bar, 0, ($H - 3), $W, 3); $bar.Dispose()
    }
    $g.Dispose(); $icon.Dispose(); $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
}
$BannerBmp = Join-Path $payload 'banner.bmp'; $DialogBmp = Join-Path $payload 'dialog.bmp'
New-InstallerBitmap $BannerBmp 493 58 $false
New-InstallerBitmap $DialogBmp 493 312 $true
$out = Join-Path $root 'artifacts'; New-Item -ItemType Directory -Force $out | Out-Null
$msi = Join-Path $out 'NeoDM-Setup.msi'
& wix build (Join-Path $inst 'NeoDM.wxs') -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -o $msi `
  -d AppDir=$AppDir -d HostDir=$HostDir -d ExtDir=$ExtDir -d CrxFile=$Crx -d AppExe=$AppExe -d ExtId=$extId -d Version=$version `
  -d LicenseRtf=$rtfPath -d LicenseTxt=$licTxt -d IconFile=$IconFile -d BannerBmp=$BannerBmp -d DialogBmp=$DialogBmp -d PolicyDefault=$PolicyDefault -bindpath $inst
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
Remove-Item -Recurse -Force $payload
Write-Host "Built $msi (version $version)"
Write-Host ("SHA256 " + (Get-FileHash -Algorithm SHA256 $msi).Hash)
# Release assets for the self-updater (docs/UPDATES.md): versioned installer name + SHA256SUMS.txt in "hash *name" form.
$versioned = Join-Path $out "NeoDM-Setup-$version.msi"; Copy-Item -LiteralPath $msi -Destination $versioned -Force
$sumLines = @(((Get-FileHash -Algorithm SHA256 $versioned).Hash.ToLowerInvariant()) + ' *' + (Split-Path -Leaf $versioned))
[IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), (($sumLines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
# Detached ECDSA P-256 signature over SHA256SUMS.txt (the updater refuses a release without a valid SHA256SUMS.txt.sig). Key: keys\neodm-update-signing.pem, git-ignored; back it up.
& node (Join-Path $root 'tools\sign-release.cjs') (Join-Path $out 'SHA256SUMS.txt')
if ($LASTEXITCODE -ne 0) { throw 'Signing SHA256SUMS.txt failed (is keys\neodm-update-signing.pem present?)' }
Write-Host "Release assets: $versioned, SHA256SUMS.txt and SHA256SUMS.txt.sig (upload all three)"
