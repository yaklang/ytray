# Build with the .NET 10 SDK. Release artifacts carry their own desktop runtime.
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
$previousDotnetRoot = $env:DOTNET_ROOT
$previousDotnetRootX86 = $env:DOTNET_ROOT_X86
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
Push-Location $windowsDir
try {
    $sdkVersion = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw 'Install the .NET 10 SDK (see windows/global.json).' }
    $config = if ($Release) { 'Release' } else { 'Debug' }
    $platformTarget = if ($Architecture -eq '386') { 'x86' } else { 'x64' }
    $rid = if ($Architecture -eq '386') { 'win-x86' } else { 'win-x64' }
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-.][0-9A-Za-z][0-9A-Za-z.-]*)?$') { throw "Invalid version: $Version" }
    if ($Installer -and -not $Package) { throw '-Installer requires -Package.' }
    if ($Package -and -not $Release) { throw '-Package requires -Release.' }
    if ($Package) {
        & (Join-Path $windowsDir 'prepare-yakit-browser-agent.ps1') -OutputDirectory (Join-Path $windowsDir 'src/Assets/BundledExtension')
        & (Join-Path $windowsDir 'prepare-cua-driver.ps1')
    }
    & (Join-Path $windowsDir 'fetch-winsparkle.ps1') -Architecture $Architecture
    # Publish compiles the application itself. Only build the separate framework-
    # dependent output when it is requested directly or required by the tests.
    if (-not $Package -or $Test) {
        & $dotnet build (Join-Path $windowsDir 'src/YTray.csproj') -c $config -p:PlatformTarget=$platformTarget -p:YTrayVersion=$Version -p:UseSharedCompilation=false -m:1 --nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    if ($Test) {
        if ($platformTarget -eq 'x86') { & (Join-Path $windowsDir 'prepare-test-runtime.ps1') -Dotnet $dotnet }
        & $dotnet build (Join-Path $windowsDir 'tests/FakeDriver/FakeDriver.csproj') -p:PlatformTarget=x64 -p:UseSharedCompilation=false -m:1 --nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw 'FakeDriver build failed.' }
        if ($platformTarget -eq 'x86') {
            & $dotnet build (Join-Path $windowsDir 'tests/FakeDriver/FakeDriver.csproj') -p:PlatformTarget=x86 -p:OutputPath=bin/x86/Debug/ -p:UseSharedCompilation=false -m:1 --nologo -v:quiet
            if ($LASTEXITCODE -ne 0) { throw 'x86 Job host build failed.' }
        }
        $testResults = Join-Path $windowsDir "artifacts/tests/$Architecture"
        & $dotnet test (Join-Path $windowsDir 'tests/YTray.Tests.csproj') -c $config -p:PlatformTarget=$platformTarget -p:UseSharedCompilation=false -m:1 --nologo -v:minimal --logger 'trx;LogFileName=results.trx' --results-directory $testResults
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    if ($Package) {
        $publishDir = Join-Path $windowsDir ('artifacts/publish/' + $Architecture + '/' + [Guid]::NewGuid().ToString('N'))
        & $dotnet publish (Join-Path $windowsDir 'src/YTray.csproj') -c Release -r $rid -p:PlatformTarget=$platformTarget -p:YTrayVersion=$Version -p:PublishProfile=Portable -p:UseSharedCompilation=false -m:1 -o $publishDir --nologo -v:quiet
        if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
        $publishedExe = Join-Path $publishDir 'YTray.exe'
        $publishFiles = @(Get-ChildItem -LiteralPath $publishDir -File)
        if ($publishFiles.Count -ne 1 -or $publishFiles[0].Name -ne 'YTray.exe') { throw 'Publish must produce one self-contained YTray.exe.' }
        if ((Get-Item -LiteralPath $publishedExe).Length -lt 30MB) { throw 'Published runtime is missing or unexpectedly small.' }
        $artifactDir = Join-Path $windowsDir "artifacts/$Architecture"
        New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
        $artifactExe = Join-Path $artifactDir "YTray-$Version-windows-$Architecture.exe"
        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $windowsDir 'artifacts')) + [IO.Path]::DirectorySeparatorChar
        $publishedExe = [IO.Path]::GetFullPath($publishedExe)
        $artifactExe = [IO.Path]::GetFullPath($artifactExe)
        if (-not $publishedExe.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or -not $artifactExe.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact path escaped the workspace.' }
        Move-Item -LiteralPath $publishedExe -Destination $artifactExe -Force
        Write-Host "Self-contained artifact: $artifactExe"
        Write-Host "SHA-256: $((Get-FileHash -LiteralPath $artifactExe -Algorithm SHA256).Hash)"
        if ($Installer) {
            & (Join-Path $windowsDir 'azure-sign.ps1') $artifactExe
            $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "${env:ProgramFiles}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if (-not $iscc) { throw 'Inno Setup 6 was not found.' }
            & $iscc "/DAppVersion=$Version" "/DArchitecture=$Architecture" "/DSourceExe=$artifactExe" "/DOutputDir=$artifactDir" (Join-Path $windowsDir 'Packaging/YTray.iss')
            if ($LASTEXITCODE -ne 0) { throw 'Inno Setup packaging failed.' }
            $setup = Join-Path $artifactDir "YTray-$Version-windows-$Architecture-setup.exe"
            & (Join-Path $windowsDir 'azure-sign.ps1') $setup
            Write-Host "Installer: $setup"
        }
    }
} finally {
    Pop-Location
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:DOTNET_ROOT_X86 = $previousDotnetRootX86
}
