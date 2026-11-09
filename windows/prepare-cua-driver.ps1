[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$manifest = Get-Content (Join-Path $PSScriptRoot 'resources/cua-driver/runtime-manifest.json') -Raw | ConvertFrom-Json
$destination = Join-Path $PSScriptRoot '.dependencies/cua-driver-0.38.0'
New-Item -ItemType Directory -Force $destination | Out-Null
$archive = Join-Path $destination 'driver.zip'
if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest $manifest.url -OutFile $archive }
if ((Get-Item -LiteralPath $archive).Length -ne $manifest.archive_size -or
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.archive_sha256) {
    throw 'Pinned Cua Driver archive digest mismatch.'
}
Write-Output "Verified Cua Driver $($manifest.version), $($manifest.architecture). The archive will be embedded when packaging."
