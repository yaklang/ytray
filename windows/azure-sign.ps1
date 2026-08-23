param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$File
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $File -PathType Leaf)) { throw "Signing target not found: $File" }

$required = @(
    $env:WINDOWS_CODE_SIGN_KEY_VAULT_URI,
    $env:WINDOWS_CODE_SIGN_CLIENT_ID,
    $env:WINDOWS_CODE_SIGN_CLIENT_SECRET,
    $env:WINDOWS_CODE_SIGN_CERT_NAME,
    $env:WINDOWS_CODE_SIGN_TENANT_ID
)
if ($required | Where-Object { [string]::IsNullOrWhiteSpace($_) }) {
    Write-Host "WINDOWS_CODE_SIGN_* secrets are incomplete; leaving unsigned: $File" -ForegroundColor Yellow
    exit 0
}

$tool = Get-Command AzureSignTool -ErrorAction SilentlyContinue
if (-not $tool) {
    dotnet tool install --global AzureSignTool
    $tool = Get-Command AzureSignTool -ErrorAction Stop
}

& $tool.Source sign `
    -kvu $env:WINDOWS_CODE_SIGN_KEY_VAULT_URI `
    -kvi $env:WINDOWS_CODE_SIGN_CLIENT_ID `
    -kvt $env:WINDOWS_CODE_SIGN_TENANT_ID `
    -kvs $env:WINDOWS_CODE_SIGN_CLIENT_SECRET `
    -kvc $env:WINDOWS_CODE_SIGN_CERT_NAME `
    -tr http://timestamp.digicert.com `
    -v `
    $File
if ($LASTEXITCODE -ne 0) { throw "AzureSignTool failed with exit code $LASTEXITCODE" }
