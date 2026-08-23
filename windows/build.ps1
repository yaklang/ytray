# YTray for Windows — build script
# Usage: pwsh -File windows/build.ps1 [-Release] [-Test] [-Package] [-Installer] [-Architecture amd64|386]
# Requires: Visual Studio 2022 BuildTools (MSBuild + Roslyn csc) and .NET Framework 4.8.1 reference assemblies.
# NuGet packages are restored automatically by MSBuild.

[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Test,
    [switch]$Package,
    [switch]$Installer,
    [ValidateSet('amd64', '386')]
    [string]$Architecture = 'amd64',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

# Locate MSBuild from VS 2022 BuildTools (x86 install path).
$msbuildCandidates = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
$msbuildOnPath = Get-Command 'MSBuild.exe' -ErrorAction SilentlyContinue
if (-not $msbuild -and $msbuildOnPath) { $msbuild = $msbuildOnPath.Source }
if (-not $msbuild) { throw "MSBuild.exe not found. Install Visual Studio 2022 Build Tools or set VSPATH." }
Write-Host "MSBuild: $msbuild" -ForegroundColor Cyan

$root = Split-Path -Parent $PSScriptRoot
$windowsDir = $PSScriptRoot
$solution = Join-Path $windowsDir 'YTray.sln'
$config = if ($Release) { 'Release' } else { 'Debug' }
$platformTarget = if ($Architecture -eq '386') { 'x86' } else { 'x64' }
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Invalid version: $Version"
}
if ($Installer -and -not $Package) { throw '-Installer requires -Package.' }

if ($Package) {
    if (-not $Release) { throw "-Package requires -Release." }
    $bundledExtensionDir = Join-Path $windowsDir 'src\Assets\BundledExtension'
    & (Join-Path $windowsDir 'prepare-yakit-browser-agent.ps1') -OutputDirectory $bundledExtensionDir
}

Write-Host "Building $solution ($config)..." -ForegroundColor Yellow
& $msbuild $solution -p:Configuration=$config -p:PlatformTarget=$platformTarget -restore -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if ($Test) {
    $vstestCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe"
    )
    $vstest = $vstestCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    $vstestOnPath = Get-Command 'vstest.console.exe' -ErrorAction SilentlyContinue
    if (-not $vstest -and $vstestOnPath) { $vstest = $vstestOnPath.Source }
    if (-not $vstest) { throw "vstest.console.exe not found." }
    Write-Host "Running tests..." -ForegroundColor Yellow
    $testDll = Join-Path $windowsDir "tests\bin\$config\YTray.Tests.dll"
    & $vstest $testDll
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

if ($Package) {
    $releaseExe = Join-Path $windowsDir 'src\bin\Release\YTray.exe'
    if (-not (Test-Path $releaseExe -PathType Leaf)) { throw "Release executable was not produced: $releaseExe" }

    $artifactDir = Join-Path $windowsDir "artifacts\$Architecture"
    if (Test-Path $artifactDir) { Remove-Item -Recurse -Force $artifactDir }
    New-Item -ItemType Directory -Path $artifactDir | Out-Null
    $artifactExe = Join-Path $artifactDir "YTray-$Version-windows-$Architecture.exe"
    Copy-Item $releaseExe $artifactExe

    $files = @(Get-ChildItem -Path $artifactDir -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne "YTray-$Version-windows-$Architecture.exe") {
        throw "Windows portable artifact was not produced with the expected versioned name."
    }
    if ((Get-Item $artifactExe).Length -lt 1MB) {
        throw "Packaged YTray.exe is unexpectedly small; embedded dependencies may be missing."
    }

    $hash = (Get-FileHash $artifactExe -Algorithm SHA256).Hash
    Write-Host "Single-file artifact: $artifactExe" -ForegroundColor Cyan
    Write-Host "SHA-256: $hash" -ForegroundColor Cyan

    if ($Installer) {
        & (Join-Path $windowsDir 'azure-sign.ps1') $artifactExe
        $isccCandidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
        )
        $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $iscc) { throw 'Inno Setup 6 was not found.' }
        $iss = Join-Path $windowsDir 'Packaging\YTray.iss'
        & $iscc "/DAppVersion=$Version" "/DArchitecture=$Architecture" "/DSourceExe=$artifactExe" "/DOutputDir=$artifactDir" $iss
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup packaging failed.' }
        $setup = Join-Path $artifactDir "YTray-$Version-windows-$Architecture-setup.exe"
        if (-not (Test-Path $setup -PathType Leaf)) { throw "Installer was not produced: $setup" }
        & (Join-Path $windowsDir 'azure-sign.ps1') $setup
        Write-Host "Installer: $setup" -ForegroundColor Cyan
        Write-Host "Installer SHA-256: $((Get-FileHash $setup -Algorithm SHA256).Hash)" -ForegroundColor Cyan
    }
}

Write-Host "Done." -ForegroundColor Green
