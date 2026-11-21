# Copyright (c) 2026 unlimitedera. MIT License. https://github.com/unlimitedera/NeoDM. Keep this notice; see NOTICE.
param([Parameter(Mandatory=$true)][string]$HostExe)
$ErrorActionPreference = 'Stop'
$env:NDM_HOST_EXE = (Resolve-Path -LiteralPath $HostExe).Path
try { & node --test (Join-Path (Split-Path -Parent $PSScriptRoot) 'tests\extension-tests.cjs'); if ($LASTEXITCODE -ne 0) { throw 'Host or extension tests failed' } }
finally { Remove-Item Env:\NDM_HOST_EXE -ErrorAction SilentlyContinue }
