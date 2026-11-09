# Build with the .NET SDK; releases use the Framework included in Win10 1809+.
[CmdletBinding()]
param(
    [switch]$Release, [switch]$Test, [switch]$Package, [switch]$Installer,
    [ValidateSet('amd64', '386')][string]$Architecture = 'amd64',
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$windowsDir = $PSScriptRoot
$root = Split-Path -Parent $windowsDir
$dotnet = Join-Path $windowsDir '.dependencies/dotnet10/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $windowsDir
try {
    $config = if ($Release) { 'Release' } else { 'Debug' }
    $platformTarget = if ($Architecture -eq '386') { 'x86' } else { 'x64' }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "Invalid version: $Version" }
    if ($Installer -and -not $Package) { throw '-Installer requires -Package.' }
    if ($Package -and -not $Release) { throw '-Package requires -Release.' }
    if ($Package) { & (Join-Path $windowsDir 'prepare-yakit-browser-agent.ps1') -OutputDirectory (Join-Path $windowsDir 'src/Assets/BundledExtension') }
    & (Join-Path $windowsDir 'fetch-winsparkle.ps1') -Architecture $Architecture
    & $dotnet build (Join-Path $windowsDir 'src/YTray.csproj') -c $config -p:PlatformTarget=$platformTarget -p:YTrayVersion=$Version -p:UseSharedCompilation=false -m:1 --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($Test) {
        & $dotnet build (Join-Path $windowsDir 'tests/ControlWorkerFixture/ControlWorkerFixture.csproj') -p:PlatformTarget=x64 -p:UseSharedCompilation=false -m:1 --nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw 'Worker fault fixture build failed.' }
        if ($platformTarget -eq 'x86') {
            & $dotnet build (Join-Path $windowsDir 'tests/ControlWorkerFixture/ControlWorkerFixture.csproj') -p:PlatformTarget=x86 -p:OutputPath=bin/x86/Debug/ -p:AppendTargetFrameworkToOutputPath=false -p:UseSharedCompilation=false -m:1 --nologo -v:quiet
            if ($LASTEXITCODE -ne 0) { throw 'x86 Job host build failed.' }
        }
        $testResults = Join-Path $windowsDir "artifacts/tests/$Architecture"
        & $dotnet test (Join-Path $windowsDir 'tests/YTray.Tests.csproj') -c $config -p:PlatformTarget=$platformTarget -p:UseSharedCompilation=false -m:1 --nologo -v:minimal --logger 'trx;LogFileName=results.trx' --results-directory $testResults
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        [xml]$report = Get-Content (Join-Path $testResults 'results.trx') -Raw
        if ([int]$report.TestRun.ResultSummary.Counters.executed -eq 0) { throw 'No tests were executed.' }
    }
    if ($Package) {
        $sourceExe = Join-Path $windowsDir "src/bin/$config/YTray.exe"
        $sourceConfig = $sourceExe + '.config'
        if (-not (Test-Path -LiteralPath $sourceExe) -or -not (Test-Path -LiteralPath $sourceConfig)) { throw 'Native application output is incomplete.' }
        $size = (Get-Item -LiteralPath $sourceExe).Length
        if ($size -gt 20MB) { throw 'YTray exceeds the 20 MiB release budget; check embedded dependencies.' }
        $artifactDir = Join-Path $windowsDir "artifacts/$Architecture"
        New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
        $artifactExe = Join-Path $artifactDir "YTray-$Version-windows-$Architecture.exe"
        Copy-Item -LiteralPath $sourceExe -Destination $artifactExe -Force
        Copy-Item -LiteralPath $sourceConfig -Destination ($artifactExe + '.config') -Force
        Write-Host "Native artifact: $artifactExe ($([Math]::Round($size / 1MB, 2)) MiB)"
        Write-Host "SHA-256: $((Get-FileHash -LiteralPath $artifactExe -Algorithm SHA256).Hash)"
        if ($Installer) {
            & (Join-Path $windowsDir 'azure-sign.ps1') $artifactExe
            $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "${env:ProgramFiles}\Inno Setup 6\ISCC.exe", (Join-Path $windowsDir '.dependencies/inno-tools/compiler/ISCC.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (-not $iscc) { throw 'Inno Setup 6 was not found.' }
            & $iscc "/DAppVersion=$Version" "/DArchitecture=$Architecture" "/DSourceExe=$artifactExe" "/DOutputDir=$artifactDir" (Join-Path $windowsDir 'Packaging/YTray.iss')
            if ($LASTEXITCODE -ne 0) { throw 'Inno Setup packaging failed.' }
            $setup = Join-Path $artifactDir "YTray-$Version-windows-$Architecture-setup.exe"
            & (Join-Path $windowsDir 'azure-sign.ps1') $setup
            Write-Host "Offline installer: $setup ($([Math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 2)) MiB)"
        }
    }
} finally { Pop-Location }
