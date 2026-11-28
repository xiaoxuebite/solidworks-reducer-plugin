param([switch]$Uninstall, [switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
if (!$Uninstall -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'runtime'))) {
    if (![Environment]::Is64BitOperatingSystem) { throw '64-bit Windows is required.' }
    $framework = Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
    if ($framework -lt 528040) { throw '.NET Framework 4.8 or later is required.' }
    $installed = Get-ChildItem 'HKLM:\SOFTWARE\SolidWorks' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '^SOLIDWORKS (\d{4})$' -and [int]$Matches[1] -ge 2024 } |
        ForEach-Object {
            $folder = Get-ItemPropertyValue ($_.PSPath + '\Setup') -Name 'SolidWorks Folder' -ErrorAction SilentlyContinue
            if ($folder -and (Test-Path -LiteralPath (Join-Path $folder 'SLDWORKS.exe'))) { $_.PSChildName }
        }
    if (!$installed) { throw 'Install 64-bit SOLIDWORKS 2024 or later before installing the add-in.' }
    . (Join-Path $PSScriptRoot 'build-paths.ps1')
    $build = Read-PluginPath (Join-Path $PSScriptRoot 'latest-build.txt')
    $dll = Read-PluginPath (Join-Path $build 'addin-dll.txt')
    foreach ($file in @($dll, (Join-Path $build 'ReducerLauncher.exe'), (Join-Path $build 'ComProbe.exe'))) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Extract the full ZIP. Missing: $file" }
    }
    $python = if ($env:REDUCER_SW_PYTHON) {
        [Environment]::ExpandEnvironmentVariables($env:REDUCER_SW_PYTHON.Trim().Trim('"'))
    } else { 'py.exe' }
    $pythonArgs = @()
    if (!$env:REDUCER_SW_PYTHON) { $pythonArgs += '-3.12' }
    if (!(Get-Command $python -ErrorAction SilentlyContinue)) { throw 'Install 64-bit Python 3.12 with the Python launcher, then run setup again.' }
    & $python @pythonArgs -c "import struct, sys; assert sys.version_info[:2] == (3, 12) and struct.calcsize('P') == 8; sys.path.insert(0, sys.argv[1]); from PIL import Image; Image.new('RGB', (1, 1))" (Join-Path $PSScriptRoot 'runtime')
    if ($LASTEXITCODE -ne 0) { throw 'Could not load the bundled Pillow dependency with 64-bit Python 3.12.' }
    Write-Host "Preflight passed. SOLIDWORKS detected: $($installed -join ', ')."
}
if ($CheckOnly) { return }

if (Get-Process SLDWORKS,ReducerLauncher -ErrorAction SilentlyContinue) {
    throw 'Save your work and close SOLIDWORKS and the reducer design windows, then run this command again.'
}
$script = if ($Uninstall) { 'unregister.ps1' } else { 'install.ps1' }
$scriptPath = Join-Path $PSScriptRoot $script
$admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Clean up the calling account even when UAC requires a different admin account.
    if ($Uninstall) { & $scriptPath -CurrentUserOnly }
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $process = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"')
    )
    if ($process.ExitCode -ne 0) { throw "Administrator operation failed with exit code $($process.ExitCode)." }
} else {
    & $scriptPath
}
if ($Uninstall) { Write-Host 'Uninstalled. Your project, report and model files are still in place.' }
else { Write-Host 'Installed. Start SOLIDWORKS and enable Reducer Generator in Tools > Add-ins.' }
