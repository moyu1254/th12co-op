[CmdletBinding()]
param(
    [switch]$NativeOnly,
    [switch]$LauncherOnly,
    [switch]$SmokeTest,
    [switch]$SelfContained,
    [switch]$PrepareGame,
    [string]$OriginalGame = 'C:\Program Files (x86)\上海アリス幻樂団\東方星蓮船'
)

$ErrorActionPreference = 'Stop'
if ($NativeOnly -and $LauncherOnly) { throw 'Choose either NativeOnly or LauncherOnly.' }
if ($SmokeTest -and $LauncherOnly) { throw 'SmokeTest requires a native build.' }
$projectRoot = $PSScriptRoot
$buildRoot = Join-Path $projectRoot 'build'
$outputRoot = Join-Path $projectRoot 'dist'
[IO.Directory]::CreateDirectory($buildRoot) | Out-Null
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null

if (-not $LauncherOnly) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools with the x86 compiler and Windows SDK are required.' }
    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $vsRoot) { throw 'No Visual Studio installation with the x86 C++ compiler was found.' }
    $vcvars = Join-Path ([string]$vsRoot).Trim() 'VC\Auxiliary\Build\vcvars32.bat'
    $nativeSource = Join-Path $projectRoot 'src\native\th12coop.cpp'
    $injectorSource = Join-Path $projectRoot 'src\injector\injector.cpp'
    foreach ($required in @($vcvars, $nativeSource, $injectorSource)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing build input: $required" }
    }
    $nativeName = if ($SmokeTest) { 'th12coop-smoke' } else { 'th12coop-native' }
    $nativeDefine = if ($SmokeTest) { '/DCOOP_SMOKE_TEST' } else { '' }
    $nativeDll = Join-Path $outputRoot "$nativeName.dll"
    $nativeObject = Join-Path $buildRoot "$nativeName.obj"
    $nativeImport = Join-Path $buildRoot "$nativeName.lib"
    $injectorExe = Join-Path $outputRoot 'injector.exe'
    $injectorObject = Join-Path $buildRoot 'injector.obj'
    $batchPath = Join-Path $buildRoot ("compile-$nativeName.cmd")
    $commands = @(
        '@echo off',
        'chcp 65001 >nul',
        ('call "{0}" >nul' -f $vcvars),
        'if errorlevel 1 exit /b %errorlevel%',
        ('cl /nologo /LD /MT /EHsc /O2 /W4 /std:c++17 /utf-8 /DUNICODE /D_UNICODE {4} /Fo"{0}" /Fe"{1}" "{2}" /link /MACHINE:X86 /INCREMENTAL:NO /IMPLIB:"{3}" kernel32.lib user32.lib' -f $nativeObject, $nativeDll, $nativeSource, $nativeImport, $nativeDefine),
        'if errorlevel 1 exit /b %errorlevel%',
        ('cl /nologo /MT /EHsc /O2 /W4 /std:c++17 /utf-8 /DUNICODE /D_UNICODE /Fo"{0}" /Fe"{1}" "{2}" /link /MACHINE:X86 /INCREMENTAL:NO bcrypt.lib kernel32.lib' -f $injectorObject, $injectorExe, $injectorSource),
        'exit /b %errorlevel%'
    )
    if ($SmokeTest) {
        $quietSource = Join-Path $projectRoot 'src\injector\quiet_test.cpp'
        $quietObject = Join-Path $buildRoot 'th12coop-quiet-test.obj'
        $quietDll = Join-Path $outputRoot 'th12coop-quiet-test.dll'
        $quietImport = Join-Path $buildRoot 'th12coop-quiet-test.lib'
        $quietTestObject = Join-Path $buildRoot 'quiet-guard-self-test.obj'
        $quietTest = Join-Path $buildRoot 'quiet-guard-self-test.exe'
        $commands = $commands[0..($commands.Count - 2)] + @(
            'if errorlevel 1 exit /b %errorlevel%',
            ('cl /nologo /LD /MT /EHsc /O2 /W4 /std:c++17 /utf-8 /DUNICODE /D_UNICODE /Fo"{0}" /Fe"{1}" "{2}" /link /MACHINE:X86 /INCREMENTAL:NO /IMPLIB:"{3}" kernel32.lib user32.lib ole32.lib wer.lib' -f $quietObject, $quietDll, $quietSource, $quietImport),
            'if errorlevel 1 exit /b %errorlevel%',
            ('cl /nologo /MT /EHsc /O2 /W4 /std:c++17 /utf-8 /DUNICODE /D_UNICODE /DQUIET_GUARD_SELF_TEST /Fo"{0}" /Fe"{1}" "{2}" /link /MACHINE:X86 /INCREMENTAL:NO kernel32.lib user32.lib ole32.lib wer.lib' -f $quietTestObject, $quietTest, $quietSource),
            'if errorlevel 1 exit /b %errorlevel%',
            ('"{0}"' -f $quietTest),
            'exit /b %errorlevel%'
        )
    }
    [IO.File]::WriteAllLines($batchPath, $commands, [Text.UTF8Encoding]::new($false))
    & $env:COMSPEC /d /c $batchPath
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
    foreach ($binary in @($nativeDll, $injectorExe)) {
        $bytes = [IO.File]::ReadAllBytes($binary)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
        if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x014c) { throw "Expected an x86 binary: $binary" }
    }
    Write-Host 'Verified x86 native DLL and injector.'
}

if (-not $NativeOnly) {
    $localDotnet = Join-Path $projectRoot 'research\dotnet10\dotnet.exe'
    $dotnetCommand = if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { $localDotnet } else { 'dotnet' }
    $launcherProjects = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\launcher') -Filter '*.csproj' -File)
    if ($launcherProjects.Count -ne 1) { throw 'Expected exactly one launcher project in src/launcher.' }
    $standalone = if ($SelfContained) { 'true' } else { 'false' }
    $runtimeArguments = @()
    if ($SelfContained) { $runtimeArguments += '-p:RuntimeFrameworkVersion=10.0.14' }
    & $dotnetCommand publish $launcherProjects[0].FullName -c Release -r win-x64 --self-contained $standalone -o $outputRoot --nologo @runtimeArguments
    if ($LASTEXITCODE -ne 0) { throw "Launcher build failed with exit code $LASTEXITCODE." }
}

# Recheck after managed publishing: Windows filenames are case-insensitive, so
# TH12Coop.dll is the managed assembly and must never be the native DLL's name.
if (-not $LauncherOnly) {
    foreach ($binary in @($nativeDll, $injectorExe)) {
        $bytes = [IO.File]::ReadAllBytes($binary)
        $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
        if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x014c) { throw "Published native binary is not x86: $binary" }
    }
    Write-Host 'Verified native outputs after launcher publishing.'
}

if ($PrepareGame) { & (Join-Path $projectRoot 'scripts\Prepare-Game.ps1') -OriginalGame $OriginalGame }
Copy-Item -LiteralPath (Join-Path $projectRoot 'profiles\th12-online.cfg') -Destination (Join-Path $outputRoot 'th12-online.cfg') -Force
# Remove artifacts from the superseded media transport so they cannot enter a
# new input-synchronization package after an incremental build.
foreach ($obsolete in @('audio_capture.exe','NAudio.dll','NAudio.Asio.dll','NAudio.Core.dll','NAudio.Midi.dll','NAudio.Wasapi.dll','NAudio.WinForms.dll','NAudio.WinMM.dll')) {
    $obsoletePath = Join-Path $outputRoot $obsolete
    if (Test-Path -LiteralPath $obsoletePath -PathType Leaf) { Remove-Item -LiteralPath $obsoletePath }
}
$licensesRoot = Join-Path $projectRoot 'licenses'
if (Test-Path -LiteralPath $licensesRoot -PathType Container) {
    $licenseOutput = Join-Path $outputRoot 'licenses'
    [IO.Directory]::CreateDirectory($licenseOutput) | Out-Null
    Get-ChildItem -LiteralPath $licensesRoot -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenseOutput $_.Name) -Force
    }
}
if ($SelfContained -and -not $NativeOnly) {
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $outputRoot 'TH12Coop.runtimeconfig.json') -Raw | ConvertFrom-Json
    $packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $licenseOutput = Join-Path $outputRoot 'licenses'
    [IO.Directory]::CreateDirectory($licenseOutput) | Out-Null
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        if ($framework.name -eq 'Microsoft.NETCore.App') {
            $runtimePackage = Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64\$($framework.version)"
            Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $licenseOutput 'dotnet-runtime-LICENSE.txt') -Force
            Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenseOutput 'dotnet-runtime-THIRD-PARTY-NOTICES.txt') -Force
        } elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') {
            $runtimePackage = Join-Path $packageRoot "microsoft.windowsdesktop.app.runtime.win-x64\$($framework.version)"
            Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE') -Destination (Join-Path $licenseOutput 'windowsdesktop-LICENSE.txt') -Force
        }
    }
}
Write-Host "Build output: $outputRoot"
