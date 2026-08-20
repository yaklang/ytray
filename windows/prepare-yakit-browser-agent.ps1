[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$manifestUrl = 'https://aliyun-oss.yaklang.com/chrome-extension/manifest.json'

function Invoke-WithRetry([scriptblock]$Action) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try { return & $Action }
        catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

$manifestResponse = Invoke-WithRetry { Invoke-WebRequest -Uri $manifestUrl -UseBasicParsing }
$manifest = $manifestResponse.Content | ConvertFrom-Json
$version = [string]$manifest.latest
$release = @($manifest.versions | Where-Object { [string]$_.version -eq $version }) | Select-Object -First 1
$artifact = @($release.artifacts | Where-Object { [string]$_.variant -eq 'chrome-enterprise' }) | Select-Object -First 1
$url = [string]$artifact.url
$expectedHash = [string]$artifact.sha256
$expectedSize = 0L
if ($version -notmatch '^[0-9]+(?:\.[0-9]+)*$' -or
    $null -eq $release -or $null -eq $artifact -or
    -not $url.StartsWith("https://aliyun-oss.yaklang.com/chrome-extension/$version/", [StringComparison]::Ordinal) -or
    $expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or
    -not [long]::TryParse([string]$artifact.size, [ref]$expectedSize) -or $expectedSize -le 0) {
    throw "OSS manifest does not contain a valid latest chrome-enterprise artifact: $manifestUrl"
}
Write-Host "Resolved latest Yakit Browser Agent: $version" -ForegroundColor Cyan

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('ytray-extension-' + [Guid]::NewGuid().ToString('N'))
$temporaryArchive = Join-Path $temporaryDirectory 'yakit-browser-agent.zip'
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
    Invoke-WithRetry { Invoke-WebRequest -Uri $url -OutFile $temporaryArchive -UseBasicParsing } | Out-Null
    $actualSize = (Get-Item -LiteralPath $temporaryArchive).Length
    if ($actualSize -ne $expectedSize) {
        throw "Yakit Browser Agent size mismatch: expected $expectedSize, got $actualSize"
    }
    $actualHash = (Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash
    if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Yakit Browser Agent SHA-256 mismatch'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($temporaryArchive)
    try {
        $hasManifest = @($archive.Entries | Where-Object {
            $_.FullName -match '^(?:[^/]+/)?manifest[.]json$'
        }).Count -gt 0
        if (-not $hasManifest) { throw 'Archive does not contain a supported manifest.json root' }
        foreach ($entry in $archive.Entries) {
            $normalizedPath = $entry.FullName.Replace('\', '/')
            $segments = @($normalizedPath.Split('/') | Where-Object { $_ -ne '' })
            if ($normalizedPath.StartsWith('/') -or $entry.FullName.Contains('\') -or $segments -contains '..') {
                throw "Archive contains an unsafe path: $($entry.FullName)"
            }
        }
    }
    finally { $archive.Dispose() }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Copy-Item -LiteralPath $temporaryArchive -Destination (Join-Path $OutputDirectory 'yakit-browser-agent.zip') -Force
    [ordered]@{
        version = $version
        sha256 = $expectedHash.ToLowerInvariant()
        size = $expectedSize
        variant = 'chrome-enterprise'
        sourceManifest = $manifestUrl
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'bundled-extension.json') -Encoding utf8
    Write-Host "Prepared Yakit Browser Agent $version ($expectedSize bytes) in $OutputDirectory" -ForegroundColor Cyan
}
finally {
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
