param([string]$SolidWorksDir = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS', [switch]$NoPromote)
$ErrorActionPreference = 'Stop'
$redist = Join-Path $SolidWorksDir 'api\redist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bin = Join-Path $PSScriptRoot ('bin\' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff'))
New-Item -ItemType Directory -Force $bin | Out-Null
$references = @('System.Windows.Forms.dll','System.Drawing.dll','System.Web.Extensions.dll','Microsoft.CSharp.dll')
foreach ($name in @('sldworks','swconst','swpublished')) {
    $dll = Join-Path $redist "SolidWorks.Interop.$name.dll"
    if (!(Test-Path -LiteralPath $dll)) { throw "Missing SW API: $dll" }
    $references += $dll
    Copy-Item -LiteralPath $dll -Destination $bin -Force
}
$refArgs = $references | ForEach-Object { '/reference:' + $_ }
$versionDay = [int]((Get-Date).Date - [datetime]'2020-01-01').TotalDays
$versionTime = [int]([datetime]::Now.TimeOfDay.TotalSeconds / 2)
$versionFile = Join-Path $bin 'Version.cs'
$addinDll = Join-Path $bin ('ReducerAddin_' + (Split-Path $bin -Leaf) + '.dll')
[IO.File]::WriteAllText($versionFile, "[assembly: System.Reflection.AssemblyVersion(""1.0.$versionDay.$versionTime"")]")
& $compiler /nologo /target:library /platform:x64 /debug /out:$addinDll "/resource:$PSScriptRoot\material_catalog_defaults.json,ReducerSW.MaterialCatalogDefaults.json" @refArgs "$PSScriptRoot\src\ReducerAddin.cs" "$PSScriptRoot\src\ToolbarIcons.cs" "$PSScriptRoot\src\GenerationControl.cs" "$PSScriptRoot\src\ModelPresentation.cs" "$PSScriptRoot\src\RotaryGeometry.cs" "$PSScriptRoot\src\AssemblyBuilder.cs" "$PSScriptRoot\src\OneStageMates.cs" "$PSScriptRoot\src\HardwareParts.cs" "$PSScriptRoot\src\CosmeticThreads.cs" "$PSScriptRoot\src\ParameterEditor.cs" "$PSScriptRoot\src\TwoStageEditor.cs" "$PSScriptRoot\src\TwoStageDesignWizard.cs" "$PSScriptRoot\src\BearingParts.cs" "$PSScriptRoot\src\GearParts.cs" "$PSScriptRoot\src\AuxiliaryParts.cs" "$PSScriptRoot\src\HousingParts.cs" "$PSScriptRoot\src\TwoStageParts.cs" "$PSScriptRoot\src\TwoStageHardware.cs" "$PSScriptRoot\src\TwoStageMates.cs" "$PSScriptRoot\src\NativeAudit.cs" "$PSScriptRoot\src\GearSectionAudit.cs" "$PSScriptRoot\src\DesignBridge.cs" "$PSScriptRoot\src\DesignWizard.cs" "$PSScriptRoot\src\MaterialCatalog.cs" $versionFile
if ($LASTEXITCODE -ne 0) { throw 'Add-in compilation failed' }
$appIcon = [IO.Path]::Combine($bin, 'ReducerLauncher.ico')
$iconType = [Reflection.Assembly]::LoadFrom($addinDll).GetType('ReducerSW.ToolbarIcons', $true)
$iconType.GetMethod('WriteApplicationIcon', [Reflection.BindingFlags]'NonPublic,Static').Invoke($null, [object[]]@($appIcon))
& $compiler /nologo /target:exe /platform:x64 /out:"$bin\ReducerLauncher.exe" /win32manifest:"$PSScriptRoot\src\Launcher.manifest" /win32icon:$appIcon @refArgs /reference:$addinDll "$PSScriptRoot\src\Launcher.cs"
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed' }
Copy-Item -LiteralPath "$PSScriptRoot\src\Launcher.config" -Destination "$bin\ReducerLauncher.exe.config"
Write-Host "Built: $bin"
if (!$NoPromote) { [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'latest-build.txt'), $bin) }
[IO.File]::WriteAllText((Join-Path $bin 'addin-dll.txt'), $addinDll)
