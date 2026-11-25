param([string]$OutputDirectory, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$build = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'latest-build.txt')).Trim() }
$launcher = Join-Path $build 'ReducerLauncher.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$addin = [IO.File]::ReadAllText((Join-Path $build 'addin-dll.txt')).Trim()
$pauseTest = Join-Path $build 'GenerationControlSmoke.exe'
& $compiler /nologo /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:$addin /out:$pauseTest /win32manifest:"$PSScriptRoot\src\Launcher.manifest" (Join-Path $PSScriptRoot 'tests\GenerationControlSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Pause test compilation failed.' }
Copy-Item -LiteralPath "$launcher.config" -Destination "$pauseTest.config"
& $pauseTest
if ($LASTEXITCODE -ne 0) { throw 'Pause/resume test failed.' }
$parameterNames = @('parameters.json','parameters_v2.json','parameters_v3.json','parameters_v4.json')
foreach ($name in $parameterNames) {
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $name))) { continue }
    & $launcher --validate (Join-Path $PSScriptRoot $name)
    if ($LASTEXITCODE -ne 0) { throw "Validation failed: $name" }
}
$twoStageParameters = Join-Path $PSScriptRoot 'parameters_two_stage.json'
& $launcher --validate $twoStageParameters
if ($LASTEXITCODE -ne 0) { throw 'Two-stage parameter validation failed.' }
$twoStageTest = Join-Path $build 'TwoStageSmoke.exe'
& $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:$addin /out:$twoStageTest (Join-Path $PSScriptRoot 'tests\TwoStageSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Two-stage plan test compilation failed.' }
& $twoStageTest $twoStageParameters
if ($LASTEXITCODE -ne 0) { throw 'Two-stage plan test failed.' }
$test = Join-Path $build 'EditorSmoke.exe'
& $compiler /nologo /platform:x64 /reference:System.Windows.Forms.dll /reference:$addin /out:$test (Join-Path $PSScriptRoot 'tests\EditorSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Editor test compilation failed.' }
$planTest = Join-Path $build 'AssemblyPlanSmoke.exe'
& $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:$addin /out:$planTest (Join-Path $PSScriptRoot 'tests\AssemblyPlanSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Assembly plan test compilation failed.' }
foreach ($name in $parameterNames) {
    $testParameters = Join-Path $PSScriptRoot $name
    if (!(Test-Path -LiteralPath $testParameters)) { continue }
    & $test $testParameters
    if ($LASTEXITCODE -ne 0) { throw "Editor test failed: $name" }
    & $planTest $testParameters
    if ($LASTEXITCODE -ne 0) { throw "Assembly plan test failed: $name" }
}
$python = Join-Path (Split-Path $PSScriptRoot) '.venv-mcp\Scripts\python.exe'
if (!(Test-Path -LiteralPath $python)) { $python = 'python' }
& $python (Join-Path $PSScriptRoot 'tests\check_parameters.py') $launcher
if ($LASTEXITCODE -ne 0) { throw 'Parameter rejection test failed.' }
& $python (Join-Path $PSScriptRoot 'tests\check_housing_recipe.py')
if ($LASTEXITCODE -ne 0) { throw 'Housing recipe check failed.' }
$housingTest = Join-Path $build 'HousingOptimizationSmoke.exe'
& $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:$addin /out:$housingTest (Join-Path $PSScriptRoot 'tests\HousingOptimizationSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Housing optimizer test compilation failed.' }
& $housingTest (Join-Path $PSScriptRoot 'parameters_v4.json')
if ($LASTEXITCODE -ne 0) { throw 'Housing optimizer test failed.' }
$designTest = Join-Path $build 'DesignBridgeSmoke.exe'
& $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:$addin /out:$designTest (Join-Path $PSScriptRoot 'tests\DesignBridgeSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Design bridge test compilation failed.' }
& $designTest
if ($LASTEXITCODE -ne 0) { throw 'Design bridge integration test failed.' }
$wizardTest = Join-Path $build 'DesignWizardSmoke.exe'
$interop = Join-Path $build 'SolidWorks.Interop.sldworks.dll'
& $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:$interop /reference:$addin /out:$wizardTest (Join-Path $PSScriptRoot 'tests\DesignWizardSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Design wizard test compilation failed.' }
& $wizardTest
if ($LASTEXITCODE -ne 0) { throw 'Design wizard smoke test failed.' }
& $python (Join-Path $PSScriptRoot 'tests\check_two_stage_design.py')
if ($LASTEXITCODE -ne 0) { throw 'Two-stage design calculation and report test failed.' }
$twoWizardTest = Join-Path $build 'TwoStageDesignWizardSmoke.exe'
& $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:$interop /reference:$addin /out:$twoWizardTest (Join-Path $PSScriptRoot 'tests\TwoStageDesignWizardSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Two-stage design wizard test compilation failed.' }
& $twoWizardTest
if ($LASTEXITCODE -ne 0) { throw 'Two-stage design wizard smoke test failed.' }
if ($OutputDirectory) {
    $outputParameters = Join-Path $OutputDirectory 'parameters.json'
    $outputData = Get-Content -LiteralPath $outputParameters -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($outputData.schema_version -eq 5) {
        & $python (Join-Path $PSScriptRoot 'tests\check_two_stage_native.py') $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Native two-stage mate verification failed.' }
        return
    }
    $bearingPositionTest = Join-Path $build 'BearingNativePositionCheck.exe'
    & $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:$interop /reference:$addin /out:$bearingPositionTest (Join-Path $PSScriptRoot 'tests\BearingNativePositionCheck.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Bearing geometry test compilation failed.' }
    if ($outputData.bearings -and $outputData.bearings.Count -gt 0) {
        & $bearingPositionTest $outputParameters $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Native bearing rolling positions/raceway geometry check failed.' }
    }
    & $python (Join-Path $PSScriptRoot 'tests\check_manifest.py') $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Native model verification failed.' }
    $outputManifest = Get-Content -LiteralPath (Join-Path $OutputDirectory 'parts_manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($outputData.schema_version -eq 4 -and $outputManifest.native_mates -and (Test-Path -LiteralPath (Join-Path $OutputDirectory 'native_audit.json'))) {
        & $python (Join-Path $PSScriptRoot 'tests\check_one_stage_native.py') $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Native one-stage mate verification failed.' }
    }
    if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'gear_section_audit.json')) {
        $gearTest = Join-Path $build 'GearNativeVolumeCheck.exe'
        & $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll /reference:$addin /out:$gearTest (Join-Path $PSScriptRoot 'tests\GearNativeVolumeCheck.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Gear boundary check compilation failed.' }
        & $gearTest $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Native gear boundary verification failed.' }
    }
}
