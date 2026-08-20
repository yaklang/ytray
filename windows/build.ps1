# YTray for Windows — build script
# Usage: pwsh -File windows/build.ps1 [-Release] [-Test] [-Package]
# Requires: Visual Studio 2022 BuildTools (MSBuild + Roslyn csc) and .NET Framework 4.8.1 reference assemblies.
# NuGet packages are restored automatically by MSBuild.

[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Test,
    [switch]$Package
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

if ($Package) {
    if (-not $Release) { throw "-Package requires -Release." }
    $bundledExtensionDir = Join-Path $windowsDir 'src\Assets\BundledExtension'
    & (Join-Path $windowsDir 'prepare-yakit-browser-agent.ps1') -OutputDirectory $bundledExtensionDir
}

Write-Host "Building $solution ($config)..." -ForegroundColor Yellow
& $msbuild $solution -p:Configuration=$config -restore -nologo -v:minimal
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

    $artifactDir = Join-Path $windowsDir 'artifacts'
    if (Test-Path $artifactDir) { Remove-Item -Recurse -Force $artifactDir }
    New-Item -ItemType Directory -Path $artifactDir | Out-Null
    $artifactExe = Join-Path $artifactDir 'YTray.exe'
    Copy-Item $releaseExe $artifactExe

    $files = @(Get-ChildItem -Path $artifactDir -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'YTray.exe') {
        throw "Windows release artifact must contain exactly one file: YTray.exe"
    }
    if ((Get-Item $artifactExe).Length -lt 1MB) {
        throw "Packaged YTray.exe is unexpectedly small; embedded dependencies may be missing."
    }

    $hash = (Get-FileHash $artifactExe -Algorithm SHA256).Hash
    Write-Host "Single-file artifact: $artifactExe" -ForegroundColor Cyan
    Write-Host "SHA-256: $hash" -ForegroundColor Cyan
}

Write-Host "Done." -ForegroundColor Green
