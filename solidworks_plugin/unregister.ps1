$ErrorActionPreference = 'Stop'
$paths = @(
    'HKCU:\Software\Classes\CLSID\{6B5C4699-06DB-4A1C-A17F-C267532598D5}',
    'HKCU:\Software\Classes\ReducerSW.Addin',
    'HKCU:\Software\SolidWorks\AddIns\{6B5C4699-06DB-4A1C-A17F-C267532598D5}'
)
# Remove only this add-in's explicit, constant registration paths.
foreach ($path in $paths) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
Write-Host 'Per-user ReducerSW COM registration removed. Restart SOLIDWORKS to release loaded assemblies.'
