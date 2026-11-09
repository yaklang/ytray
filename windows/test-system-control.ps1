[CmdletBinding()]
param([ValidateSet('Protocol','Lifecycle','Desktop','Authorization','AlwaysOn')][string]$Suite = 'Protocol', [ValidateSet('amd64','386')][string]$Architecture = 'amd64', [ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [switch]$Interactive)
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $PSScriptRoot '.dependencies/dotnet10/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$platformTarget = if ($Architecture -eq '386') { 'x86' } else { 'x64' }
if ($platformTarget -eq 'x86') { & (Join-Path $PSScriptRoot 'prepare-test-runtime.ps1') -Dotnet $dotnet }
if ($Suite -eq 'Desktop' -and -not $Interactive) { throw 'Desktop input tests require -Interactive and only target the independent fixture.' }
$output = Join-Path $PSScriptRoot ('artifacts/control-tests/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $output | Out-Null
& (Join-Path $PSScriptRoot 'fetch-winsparkle.ps1') -Architecture $Architecture
& $dotnet build (Join-Path $PSScriptRoot 'tests/FakeDriver/FakeDriver.csproj') -p:PlatformTarget=x64 --nologo -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'FakeDriver build failed.' }
if ($platformTarget -eq 'x86') {
    & $dotnet build (Join-Path $PSScriptRoot 'tests/FakeDriver/FakeDriver.csproj') -p:PlatformTarget=x86 -p:OutputPath=bin/x86/Debug/ --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'x86 Job host build failed.' }
}
if ($Suite -eq 'Desktop') {
    & $dotnet build (Join-Path $PSScriptRoot 'tests/ControlFixture/ControlFixture.csproj') --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed.' }
    $env:YTRAY_CONTROL_INTERACTIVE = '1'
    $env:YTRAY_CONTROL_FIXTURE_EXE = Join-Path $PSScriptRoot 'tests/ControlFixture/bin/Debug/net10.0-windows10.0.17763.0/YTray.ControlFixture.exe'
    $env:YTRAY_CONTROL_TEST_OUTPUT = $output
}
if ($Suite -eq 'AlwaysOn') { $env:YTRAY_CONTROL_LONGRUN = '1' }
try {
    & $dotnet test (Join-Path $PSScriptRoot 'tests/YTray.Tests.csproj') -c $Configuration -p:PlatformTarget=$platformTarget --nologo -v:quiet --filter "TestCategory=Control$Suite" --logger "trx;LogFileName=results.trx" --results-directory $output
    if ($LASTEXITCODE -ne 0) { throw 'System control tests failed.' }
} finally { Remove-Item Env:YTRAY_CONTROL_INTERACTIVE,Env:YTRAY_CONTROL_FIXTURE_EXE,Env:YTRAY_CONTROL_TEST_OUTPUT,Env:YTRAY_CONTROL_LONGRUN -ErrorAction SilentlyContinue }
