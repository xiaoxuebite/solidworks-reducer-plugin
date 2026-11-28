$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-paths.ps1')
$build = Read-PluginPath (Join-Path $PSScriptRoot 'latest-build.txt')
$dll = Read-PluginPath (Join-Path $build 'addin-dll.txt')
if (!(Test-Path -LiteralPath $dll)) { throw "Plugin build is missing: $dll" }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$installer = Join-Path $PSScriptRoot 'install.ps1'
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    & $installer
    if ($LASTEXITCODE -ne 0) { throw 'Plugin installation failed.' }
} else {
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $quotedInstaller = '"' + $installer + '"'
    $process = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Normal -Wait -PassThru `
        -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$quotedInstaller)
    if ($process.ExitCode -ne 0) { throw "Administrator installer failed or was cancelled: $($process.ExitCode)" }
}
$server = 'HKLM:\SOFTWARE\Classes\CLSID\{6B5C4699-06DB-4A1C-A17F-C267532598D5}\InprocServer32'
$codebase = (Get-ItemProperty -LiteralPath $server).CodeBase
$installed = ([uri]$codebase).LocalPath
if (![String]::Equals([IO.Path]::GetFullPath($installed),[IO.Path]::GetFullPath($dll),[StringComparison]::OrdinalIgnoreCase)) {
    throw "Machine registration did not update: $installed"
}
Write-Host "System plugin registration updated: $dll"
Write-Host 'Restart SOLIDWORKS after saving any open documents, then load the reducer add-in.'
