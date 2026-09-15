# This fixture installs and removes applications only on disposable CI runners.
param([ValidateSet('amd64', '386')][string]$Architecture = 'amd64')
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true') { throw 'Upgrade verification requires an isolated CI runner' }
$root = Join-Path $env:RUNNER_TEMP ('ytray-upgrade-' + [Guid]::NewGuid())
$install = Join-Path $root 'app'
[void][IO.Directory]::CreateDirectory($root)
$baseline = Join-Path $root 'baseline.exe'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'YTray'
$sentinel = Join-Path $dataDirectory ('upgrade-fixture-' + [Guid]::NewGuid() + '.txt')
$expected = 'Browser profiles, settings and runtime installations remain outside the app payload.'
function Install-Package([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Upgrade installer timed out' }
    if ($process.ExitCode -ne 0) { throw "Upgrade installer failed: $($process.ExitCode)" }
}
try {
    Invoke-WebRequest "https://aliyun-oss.yaklang.com/ytray/0.1.16/YTray-0.1.16-windows-$Architecture-setup.exe" -OutFile $baseline
    $baselineHash = if ($Architecture -eq '386') { 'B372A548DD9A70CE4442D5AD60986AA23A08255376D460FC05B228AFAB2136EF' } else { 'F3D404EA47438B0E811C5D21C237950ECBD736DD00EC78C1150288D2B14B5C93' }
    if ((Get-FileHash $baseline).Hash -ne $baselineHash) { throw 'Baseline release checksum mismatch' }
    Install-Package $baseline @('/VERYSILENT', '/NORESTART', ('/DIR="{0}"' -f $install))
    [void][IO.Directory]::CreateDirectory($dataDirectory)
    [IO.File]::WriteAllText($sentinel, $expected)
    # Avoid first-run prompts in the relaunched app. This runner has no user data.
    $state = Join-Path $dataDirectory 'state.json'
    [IO.File]::WriteAllText($state, '{"Settings":{"LaunchAtLoginSetupCompleted":true,"CheckForAppUpdates":false,"HomeURL":"https://example.test/preserved"}}')
    $version = (Get-Content (Join-Path (Split-Path $PSScriptRoot) 'VERSION') -Raw).Trim()
    $artifact = Join-Path $PSScriptRoot "artifacts\$Architecture\YTray-$version-windows-$Architecture.exe"
    $setup = Join-Path $PSScriptRoot "artifacts\$Architecture\YTray-$version-windows-$Architecture-setup.exe"
    Install-Package $setup @('/SILENT', '/SP-', '/NORESTART', '/NOFORCECLOSEAPPLICATIONS', '/YTRAYAUTOUPDATE=1', ('/DIR="{0}"' -f $install))
    $exe = Join-Path $install 'YTray.exe'
    if ((Get-FileHash $exe).Hash -ne (Get-FileHash $artifact).Hash) { throw 'Upgrade installed the wrong executable' }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $reopened = @(Get-Process -Name YTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
        if (-not $reopened) { Start-Sleep -Milliseconds 250 }
    } while (-not $reopened -and [DateTime]::UtcNow -lt $deadline)
    if (-not $reopened) { throw 'Updated YTray was not reopened' }
    if ([IO.File]::ReadAllText($sentinel) -ne $expected) { throw 'Upgrade changed profile data' }
    $restored = Get-Content $state -Raw | ConvertFrom-Json
    if ($restored.Settings.HomeURL -ne 'https://example.test/preserved') { throw 'Upgrade changed browser settings' }
    $marker = Join-Path $root 'standalone.json'
    $probe = Start-Process $exe -ArgumentList @('--verify-standalone', ('"' + $marker + '"')) -PassThru
    if (-not $probe.WaitForExit(30000) -or $probe.ExitCode -ne 0) { throw 'Installed update engine failed to load' }
    $verified = Get-Content $marker -Raw | ConvertFrom-Json
    if ($verified.version -ne $version) { throw 'Installed version mismatch' }
    Write-Host "Windows $Architecture: upgrade from 0.1.16, exact executable, automatic relaunch, native engine and data preservation verified"
} finally {
    $exe = Join-Path $install 'YTray.exe'
    Get-Process -Name YTray -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
    $uninstaller = Join-Path $install 'unins000.exe'
    if (Test-Path $uninstaller) { Install-Package $uninstaller @('/VERYSILENT', '/NORESTART') }
    Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $root -Recurse -Force
}
