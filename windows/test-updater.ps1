# Isolated native WinSparkle signature checks for each process architecture.
param([ValidateSet('amd64', '386')][string]$Architecture = 'amd64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ytray-native-update-' + [Guid]::NewGuid())
New-Item -ItemType Directory $temporary | Out-Null
try {
    & (Join-Path $PSScriptRoot 'fetch-winsparkle.ps1') -Architecture $Architecture
    $engineArch = if ($Architecture -eq '386') { 'Win32' } else { 'x64' }
    $platform = if ($Architecture -eq '386') { 'x86' } else { 'x64' }
    Copy-Item (Join-Path $PSScriptRoot ".dependencies\WinSparkle-0.9.4\$engineArch\Release\WinSparkle.dll") $temporary
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    # The framework compiler is sufficient for the independent C# fixture.
    $fixture = Join-Path $temporary 'UpdaterFixture.exe'
    & $csc /nologo "/platform:$platform" "/out:$fixture" (Join-Path $root 'script\fixtures\WindowsUpdaterFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile updater fixture' }
    python (Join-Path $root 'script\test-windows-updater.py') $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Native updater checks failed' }
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
