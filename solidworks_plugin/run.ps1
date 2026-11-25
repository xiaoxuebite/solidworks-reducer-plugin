param([switch]$LoadPlugin, [switch]$RefreshParameters, [switch]$Editor, [switch]$Design, [switch]$TwoDesign, [string]$ParameterFile)
$ErrorActionPreference = 'Stop'
try {
    $latest = Join-Path $PSScriptRoot 'latest-build.txt'
    if (!(Test-Path -LiteralPath $latest)) { & (Join-Path $PSScriptRoot 'build.ps1') }
    if ($ParameterFile) { $parameters = [IO.Path]::GetFullPath($ParameterFile) }
    elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'parameters_v4.json')) { $parameters = Join-Path $PSScriptRoot 'parameters_v4.json' }
    elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'parameters_v3.json')) { $parameters = Join-Path $PSScriptRoot 'parameters_v3.json' }
    elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'parameters_v2.json')) { $parameters = Join-Path $PSScriptRoot 'parameters_v2.json' }
    else { $parameters = Join-Path $PSScriptRoot 'parameters.json' }
    if (!$Design -and !$TwoDesign -and !$LoadPlugin -and ($RefreshParameters -or !(Test-Path -LiteralPath $parameters))) {
        $python = Join-Path (Split-Path $PSScriptRoot) '.venv-mcp\Scripts\python.exe'
        if (!(Test-Path -LiteralPath $python)) { $python = 'python' }
        $exporter = 'export_parameters.py'
        if ((Split-Path $parameters -Leaf) -eq 'parameters_v4.json') { $exporter = 'export_all.py' }
        elseif (Test-Path -LiteralPath $parameters) {
            $existing = Get-Content -LiteralPath $parameters -Raw | ConvertFrom-Json
            if ($existing.schema_version -ge 4) { $exporter = 'export_all.py' }
        }
        & $python (Join-Path $PSScriptRoot $exporter) --output $parameters
        if ($LASTEXITCODE -ne 0) { throw 'Parameter export failed.' }
    }
    $build = [IO.File]::ReadAllText($latest).Trim()
    $launcher = Join-Path $build 'ReducerLauncher.exe'
    if ($Design) { & $launcher --design } elseif ($TwoDesign) { & $launcher --two-design } elseif ($LoadPlugin) { & $launcher --load } elseif ($Editor) { & $launcher --editor $parameters } else { & $launcher $parameters }
    if ($LASTEXITCODE -ne 0) { throw 'SOLIDWORKS operation failed; see the error above.' }
} catch { Write-Error $_; exit 1 }
