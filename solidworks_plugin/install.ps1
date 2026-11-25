# Run once in an administrator PowerShell window; compilation/generation needs no elevation.
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installer once from an administrator PowerShell window. Direct generation does not require installation.'
}
$latest = Join-Path $PSScriptRoot 'latest-build.txt'
if (!(Test-Path -LiteralPath $latest)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$build = [IO.File]::ReadAllText($latest).Trim()
$dll = [IO.File]::ReadAllText((Join-Path $build 'addin-dll.txt')).Trim()
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
& $regasm $dll /codebase /nologo
if ($LASTEXITCODE -ne 0) { throw 'COM registration failed.' }
# RegAsm writes through HKCR, which can resolve to a pre-existing HKCU override.
# Write the machine registration explicitly so elevated SW also sees this build.
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($dll)
$classId = '{6B5C4699-06DB-4A1C-A17F-C267532598D5}'
$classKey = 'HKLM:\SOFTWARE\Classes\CLSID\' + $classId
$serverKey = $classKey + '\InprocServer32'
New-Item -Path $serverKey -Force | Out-Null
Set-Item -LiteralPath $serverKey -Value 'mscoree.dll'
New-ItemProperty -LiteralPath $serverKey -Name ThreadingModel -Value Both -PropertyType String -Force | Out-Null
$registration = @{
    Class = 'ReducerSW.ReducerAddin'
    Assembly = $assemblyName.FullName
    RuntimeVersion = 'v4.0.30319'
    CodeBase = ([uri]$dll).ToString()
}
$versionKey = $serverKey + '\' + $assemblyName.Version.ToString()
New-Item -Path $versionKey -Force | Out-Null
foreach ($entry in $registration.GetEnumerator()) {
    New-ItemProperty -LiteralPath $serverKey -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $versionKey -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
}
# RegAsm's callback may create a per-user override. Keep both scopes identical.
$userServer = 'HKCU:\Software\Classes\CLSID\' + $classId + '\InprocServer32'
foreach ($userPath in @($userServer, ($userServer + '\' + $assemblyName.Version.ToString()))) {
    New-Item -Path $userPath -Force | Out-Null
    foreach ($entry in $registration.GetEnumerator()) {
        New-ItemProperty -LiteralPath $userPath -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
    }
}
$progKey = $classKey + '\ProgId'
New-Item -Path $progKey -Force | Out-Null
Set-Item -LiteralPath $progKey -Value 'ReducerSW.Addin'
$progClassKey = 'HKLM:\SOFTWARE\Classes\ReducerSW.Addin\CLSID'
New-Item -Path $progClassKey -Force | Out-Null
Set-Item -LiteralPath $progClassKey -Value $classId
if ((Get-ItemProperty -LiteralPath $serverKey).Assembly -ne $assemblyName.FullName) { throw 'Machine COM registration verification failed.' }
if ((Get-ItemProperty -LiteralPath $serverKey).CodeBase -ne $registration.CodeBase) { throw 'Machine COM path verification failed.' }
$key = 'HKLM:\SOFTWARE\SolidWorks\AddIns\{6B5C4699-06DB-4A1C-A17F-C267532598D5}'
New-Item -Path $key -Force | Out-Null
Set-Item -LiteralPath $key -Value 0
New-ItemProperty -LiteralPath $key -Name Title -Value 'Reducer Generator' -PropertyType String -Force | Out-Null
New-ItemProperty -LiteralPath $key -Name Description -Value 'Native reducer prototype for SOLIDWORKS 2024' -PropertyType String -Force | Out-Null
# A separate process without a reference to the add-in tests actual COM activation.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$probe = Join-Path $build 'ComProbe.exe'
$interop = Join-Path $build 'SolidWorks.Interop.sldworks.dll'
& $compiler /nologo /platform:x64 /reference:Microsoft.CSharp.dll /reference:$interop /out:$probe (Join-Path $PSScriptRoot 'src\ComProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'COM diagnostic compilation failed.' }
& $probe
if ($LASTEXITCODE -ne 0) { throw 'COM activation test failed. Installation is not complete.' }
Write-Host 'Installed. In your normal session, run run.ps1 -LoadPlugin. If needed, save your work and restart SOLIDWORKS, then enable Reducer Generator in Tools > Add-ins.'
Write-Host "Verified machine registration: $dll"
