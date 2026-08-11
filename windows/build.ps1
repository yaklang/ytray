# YTray for Windows — build script
# Usage: pwsh -File windows/build.ps1 [-Release] [-Test]
# Requires: Visual Studio 2022 BuildTools (MSBuild + Roslyn csc) and .NET Framework 4.8.1 reference assemblies.
# NuGet packages are restored automatically by MSBuild.

[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

# Locate MSBuild from VS 2022 BuildTools (x86 install path).
$msbuildCandidates = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild.exe not found. Install Visual Studio 2022 Build Tools or set VSPATH." }
Write-Host "MSBuild: $msbuild" -ForegroundColor Cyan

$root = Split-Path -Parent $PSScriptRoot
$windowsDir = $PSScriptRoot
$solution = Join-Path $windowsDir 'YTray.sln'
$config = if ($Release) { 'Release' } else { 'Debug' }

Write-Host "Building $solution ($config)..." -ForegroundColor Yellow
& $msbuild $solution -p:Configuration=$config -restore -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if ($Test) {
    $vstestCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe"
    )
    $vstest = $vstestCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $vstest) { throw "vstest.console.exe not found." }
    Write-Host "Running tests..." -ForegroundColor Yellow
    $testDll = Join-Path $windowsDir "tests\bin\$config\YTray.Tests.dll"
    & $vstest $testDll
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Write-Host "Done." -ForegroundColor Green