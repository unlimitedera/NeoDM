[CmdletBinding()]
param(
    [switch]$SkipTests,
    # Kept as a harmless alias: torrents are part of the normal product, so this switch changes nothing.
    [switch]$Torrent
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
}

# Product version: single source of truth is Directory.Build.props (see docs/VERSIONING.md).
$versionMatch = [regex]::Match((Get-Content -LiteralPath 'Directory.Build.props' -Raw), '<Version>\s*(\d+\.\d+\.\d+)\s*</Version>')
if (-not $versionMatch.Success) { throw 'Directory.Build.props has no <Version>x.y.z</Version>' }
$version = $versionMatch.Groups[1].Value
Write-Host "== NeoDM $version =="

# Sync the extension manifest version; the signed CRX is repacked below when it changes.
$manifestPath = Join-Path $PSScriptRoot 'extension\manifest.json'
$manifestText = [IO.File]::ReadAllText($manifestPath)
$manifestVersion = [regex]::Match($manifestText, '"version"\s*:\s*"([^"]*)"').Groups[1].Value
$manifestChanged = $manifestVersion -ne $version
if ($manifestChanged) {
    $manifestText = [regex]::Replace($manifestText, '("version"\s*:\s*")[^"]*(")', "`${1}$version`$2", 1)
    [IO.File]::WriteAllText($manifestPath, $manifestText, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Extension manifest version $manifestVersion -> $version"
}

if (-not $SkipTests) {
    # Every existing suite asserts English text: force the language before any window is created (the app honours NEODM_LANG). The Localization suite drives the language itself.
    $env:NEODM_LANG = 'en'
    & node tools/check-locales.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Locale files are invalid (see tools/check-locales.cjs output)' }
    Invoke-DotNet @('run','--project','tests/NDM.Tests/NDM.Tests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/NDM.App.ProtocolTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Ui/NDM.App.UiTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Advanced/NDM.App.AdvancedTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Options/NDM.App.OptionsTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/OptionsDialog/NDM.App.Tests.OptionsDialog.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/CompletionActions/NDM.App.CompletionActionTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Fidelity/NDM.App.FidelityTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Manage/NDM.App.ManageTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Completion/NDM.App.CompletionTrayTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/About/NDM.App.AboutTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Extension/NDM.App.ExtensionTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Themes/NDM.App.ThemeTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Layout/NDM.App.LayoutTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Localization/NDM.App.LocalizationTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Release/NDM.App.ReleaseTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.App/Tests/Bridge/NDM.App.BridgeTests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','tests/NDM.Torrent.Tests/NDM.Torrent.Tests.csproj','-c','Release')
    Invoke-DotNet @('run','--project','src/NDM.NativeHost/NDM.NativeHost.csproj','--','--self-test')
    & npm ci --prefix tests --ignore-scripts --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { throw 'Extension test dependencies failed' }
    & node --test tests/extension-tests.cjs tests/extension-advanced-tests.cjs tests/extension-v130-tests.cjs tests/extension-v132-tests.cjs tests/extension-v133-tests.cjs tests/extension-v142-tests.cjs tests/extension-v143-tests.cjs tests/extension-v145-tests.cjs tests/extension-v146-tests.cjs tests/extension-v147-tests.cjs tests/extension-v148-tests.cjs tests/extension-i18n-tests.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed' }
}

Invoke-DotNet @('publish','src/NDM.App/NDM.App.csproj','-c','Release','-r','win-x64','--self-contained','true','-o','dist/app')
Invoke-DotNet @('publish','src/NDM.NativeHost/NDM.NativeHost.csproj','-c','Release','-r','win-x64','--self-contained','true','-o','dist/host')
if ($manifestChanged -or -not (Test-Path -LiteralPath 'dist\NeoDM.crx')) {
    & node tools/pack-extension.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Extension packing failed' }
}
foreach ($dll in 'NDM.Torrent.dll','MonoTorrent.Client.dll') {
    if (-not (Test-Path -LiteralPath "dist\app\$dll")) { throw "dist\app is missing $dll (torrent support must ship in the normal build)" }
}

# Repo-wide scan: product names that must never appear in sources, docs, scripts, tests or first-party build outputs.
# The patterns are assembled from fragments so this script does not contain the words itself.
$bannedA = 'I' + 'DM'; $bannedB = 'Internet Download ' + 'Manager'; $bannedC = 'q' + 'bit'
$banned = [regex]::new("\b$bannedA\b|$bannedB|$bannedC", 'IgnoreCase')
$skipDirs = @('node_modules','bin','obj','artifacts','keys','.git','payload')
$binaryExt = @('.png','.ico','.crx','.pdb','.zip','.msi','.pem','.exe','.dll','.pri','.nupkg')
$firstParty = '^(NeoDM|NDM\.[A-Za-z.]*)\.(exe|dll)$'
$hits = New-Object System.Collections.Generic.List[string]
function Test-Bytes([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $views = @([Text.Encoding]::GetEncoding(28591).GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
    if ($bytes.Length -gt 1) { $views += [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) }
    foreach ($text in $views) { $m = $banned.Match($text); if ($m.Success) { return $m.Value } }
    return $null
}
$stack = New-Object System.Collections.Generic.Stack[string]; $stack.Push($PSScriptRoot)
$scanned = 0
while ($stack.Count -gt 0) {
    $dir = $stack.Pop()
    foreach ($entry in Get-ChildItem -LiteralPath $dir -Force) {
        if ($entry.PSIsContainer) { if ($skipDirs -notcontains $entry.Name) { $stack.Push($entry.FullName) }; continue }
        $ext = $entry.Extension.ToLowerInvariant()
        $rel = $entry.FullName.Substring($PSScriptRoot.Length + 1)
        $isBinary = $binaryExt -contains $ext
        if ($isBinary -and -not (($ext -eq '.exe' -or $ext -eq '.dll') -and $entry.Name -match $firstParty)) { continue }
        if (-not $isBinary -and $entry.Length -gt 20MB) { continue }
        $scanned++
        $found = $null
        if ($isBinary) { $found = Test-Bytes $entry.FullName }
        else { $m = $banned.Match([IO.File]::ReadAllText($entry.FullName)); if ($m.Success) { $found = $m.Value } }
        if ($found) { $hits.Add("$rel : '$found'") }
    }
}
Write-Host "Forbidden-name scan: $scanned files, $($hits.Count) hit(s)"
if ($hits.Count -gt 0) { $hits | ForEach-Object { Write-Host "  $_" }; throw 'Forbidden product names found (see list above)' }

Write-Host "Built NeoDM $version : dist/app/NeoDM.exe"
