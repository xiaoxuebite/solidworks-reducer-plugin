param([switch]$CurrentUserOnly)

$ErrorActionPreference = 'Stop'
$id = '{6B5C4699-06DB-4A1C-A17F-C267532598D5}'
$paths = @(
    "HKCU:\Software\Classes\CLSID\$id"
    'HKCU:\Software\Classes\ReducerSW.Addin'
    "HKCU:\Software\SolidWorks\AddIns\$id"
    "HKCU:\Software\SolidWorks\AddInsStartup\$id"
)
if (!$CurrentUserOnly) {
    $admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run the uninstall command file or open PowerShell as administrator.'
    }
    $paths += @(
        "HKLM:\SOFTWARE\Classes\CLSID\$id"
        'HKLM:\SOFTWARE\Classes\ReducerSW.Addin'
        "HKLM:\SOFTWARE\SolidWorks\AddIns\$id"
        "HKLM:\SOFTWARE\SolidWorks\AddInsStartup\$id"
    )
}
# These are the exact keys registered for Reducer Generator.
foreach ($path in $paths) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
