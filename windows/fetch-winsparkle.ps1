param([ValidateSet('amd64', '386')][string]$Architecture = 'amd64')
$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$File) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($File)
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $hash.Dispose() }
}
$root = Join-Path $PSScriptRoot '.dependencies\WinSparkle-0.9.4'
$engineArch = if ($Architecture -eq '386') { 'Win32' } else { 'x64' }
$dll = Join-Path $root ($engineArch + '\Release\WinSparkle.dll')
$expected = if ($Architecture -eq '386') { '6837653B02E2C3ACF83AE5C76867C370C9BD83D14782D1F1E8A8C093C6C0FDF7' } else { '9B43B1C16EE39FB9A91B5BD75138767898779510E0836BE2919250607CDBE8AB' }
if (Test-Path -LiteralPath $dll) {
    if ((Get-Sha256 $dll) -eq $expected) { return }
    throw 'Cached WinSparkle DLL checksum mismatch; remove windows/.dependencies and rebuild'
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ytray-winsparkle-' + [Guid]::NewGuid())
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $zip = Join-Path $temporary 'WinSparkle.zip'
    Invoke-WebRequest 'https://github.com/vslavik/winsparkle/releases/download/v0.9.4/WinSparkle-0.9.4.zip' -OutFile $zip
    if ((Get-Sha256 $zip) -ne '6037DF37FC263BD1650A1C4949681A9D40FFE991D01F35892A406CB5D103C976') { throw 'WinSparkle distribution checksum mismatch' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $temporary)
    [void][IO.Directory]::CreateDirectory((Split-Path $root))
    Move-Item -LiteralPath (Join-Path $temporary 'WinSparkle-0.9.4') -Destination $root
    if (-not (Test-Path -LiteralPath $dll) -or (Get-Sha256 $dll) -ne $expected) { throw 'WinSparkle binary checksum mismatch' }
} finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
