param(
    [Parameter(Mandatory=$true)][string]$SolidWorksDir,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = New-Item -ItemType Directory -Path $OutputDirectory -Force
$archive = Join-Path $output.FullName 'solidworks-reducer-plugin-windows-x64.zip'
if (Test-Path -LiteralPath $archive) { throw "Choose a different output directory. File already exists: $archive" }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('reducer-package-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $scratch 'solidworks-reducer-plugin'
$plugin = Join-Path $stage 'solidworks_plugin'
$bin = Join-Path $plugin 'bin'

try {
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    # Package only files required by the add-in and its design engine.
    $files = @(
        'complete_design.py', 'design_service.py', 'report_generator.py',
        'model_generator/axial_layout.py', 'model_generator/gear_geometry.py',
        'model_generator/gear_operating.py', 'model_generator/housing_fasteners.py',
        'model_generator/housing_height.py', 'model_generator/housing_params.py',
        'model_generator/reinforcement_layout.py', 'model_generator/shaft_params.py',
        'model_generator/vent_layout.py',
        'images/kv-textbook-chart.png', 'images/gear-load-table-10-3.png',
        'images/gear-load-table-10-4.png', 'images/gear-load-table-10-4-continuation.jpg',
        'images/kfbeta-textbook-chart.png', 'images/reducer.jpg',
        'images/material_props.jpg', 'images/shaft-material-table-15-3.png'
    )
    $files += 'images/' + [string][char]0x9F7F + [char]0x8F6E + [char]0x6750 + [char]0x6599 + '.png'
    foreach ($file in $files) {
        $destination = Join-Path $stage $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination
    }
    $scripts = @('build-paths.ps1','install.ps1','unregister.ps1','setup.ps1','run.ps1')
    Copy-Item -LiteralPath ($scripts | ForEach-Object { Join-Path $PSScriptRoot $_ }) -Destination $plugin
    Get-ChildItem -LiteralPath $PSScriptRoot -File |
        Where-Object { $_.Extension -eq '.py' -or ($_.Extension -eq '.json' -and $_.Name -ne 'parameters.v1-backup.json') } |
        Copy-Item -Destination $plugin
    $sample = Join-Path $plugin 'parameters_two_stage.json'
    $sampleText = [IO.File]::ReadAllText($sample)
    [IO.File]::WriteAllText($sample, [regex]::Replace($sampleText, '"reference_files"\s*:\s*\[[^\]]*\]', '"reference_files": []'))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third_party\Reducer-Designer-LICENSE.txt') -Destination $plugin
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.md') -Destination (Join-Path $stage 'README.md')
    $cmdNames = @('setup', 'uninstall')
    foreach ($name in $cmdNames) {
        $arguments = if ($name -eq 'uninstall') { ' -Uninstall' } else { '' }
        $cmd = '@echo off' + "`r`n" +
            'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0solidworks_plugin\setup.ps1"' + $arguments + "`r`npause`r`n"
        [IO.File]::WriteAllText((Join-Path $stage ($name + '.cmd')), $cmd)
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build.ps1'), (Join-Path $PSScriptRoot 'src') -Destination $scratch -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'material_catalog_defaults.json') -Destination $scratch
    # The build loads the add-in to generate its icon. Exit that process before cleanup.
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scratch 'build.ps1') -SolidWorksDir $SolidWorksDir
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    . (Join-Path $PSScriptRoot 'build-paths.ps1')
    $build = Read-PluginPath (Join-Path $scratch 'latest-build.txt')
    $addinName = Split-Path -Leaf (Read-PluginPath (Join-Path $build 'addin-dll.txt'))
    Get-ChildItem -LiteralPath $build -File |
        Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('ReducerLauncher.exe','ReducerLauncher.exe.config','ComProbe.exe') } |
        Copy-Item -Destination $bin
    [IO.File]::WriteAllText((Join-Path $plugin 'latest-build.txt'), 'bin')
    [IO.File]::WriteAllText((Join-Path $bin 'addin-dll.txt'), $addinName)

    & py -3.12 -m pip install --disable-pip-version-check --only-binary=:all: --platform win_amd64 --python-version 3.12 --target (Join-Path $plugin 'runtime') 'Pillow==11.1.0'
    if ($LASTEXITCODE -ne 0) { throw 'Could not package Pillow for 64-bit Python 3.12.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $tempArchive = Join-Path $scratch 'release.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $tempArchive, [IO.Compression.CompressionLevel]::Optimal, $true)
    Copy-Item -LiteralPath $tempArchive -Destination $archive
    Get-Item -LiteralPath $archive
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolved = Resolve-Path -LiteralPath $scratch -ErrorAction SilentlyContinue
    if ($resolved -and $resolved.Path.StartsWith($tempRoot + 'reducer-package-', [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved.Path -Recurse -Force
    }
}
