[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'script/prepare-yakit-browser-agent.mjs'
& node $scriptPath $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Browser extension preparation failed.' }
