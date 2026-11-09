# The x86 CI test host needs a matching desktop runtime. It is private to the
# build workspace; end users run the self-contained executable instead.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Dotnet)
$ErrorActionPreference = 'Stop'
$desktopRuntime = & $Dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft.WindowsDesktop.App 10\.' } | Select-Object -Last 1
if (-not $desktopRuntime) { throw 'The .NET 10 SDK must include a desktop runtime.' }
$runtimeVersion = ($desktopRuntime -split ' ')[1]
$runtimeDirectory = Join-Path $PSScriptRoot '.dependencies/dotnet10-x86'
$runtimeFile = Join-Path $runtimeDirectory "shared/Microsoft.WindowsDesktop.App/$runtimeVersion/WindowsBase.dll"
$coreRuntimeFile = Join-Path $runtimeDirectory "shared/Microsoft.NETCore.App/$runtimeVersion/coreclr.dll"
if (-not (Test-Path -LiteralPath $runtimeFile) -or -not (Test-Path -LiteralPath $coreRuntimeFile)) {
    $installer = Join-Path $PSScriptRoot '.dependencies/dotnet-install.ps1'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $installer) | Out-Null
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Runtime dotnet -Version $runtimeVersion -Architecture x86 -InstallDir $runtimeDirectory -NoPath
    & $installer -Runtime windowsdesktop -Version $runtimeVersion -Architecture x86 -InstallDir $runtimeDirectory -NoPath
    if (-not (Test-Path -LiteralPath $runtimeFile) -or -not (Test-Path -LiteralPath $coreRuntimeFile)) { throw 'The private x86 desktop test runtime was not installed.' }
}
$env:DOTNET_ROOT_X86 = $runtimeDirectory
