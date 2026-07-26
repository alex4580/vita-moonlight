param(
    [string] $Destination = (Join-Path $PSScriptRoot "..\..\artifacts\vigembus")
)

$ErrorActionPreference = "Stop"
$version = "1.22.0"
$assetName = "ViGEmBus_1.22.0_x64_x86_arm64.exe"
$assetUri = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/$assetName"
$licenseUri = "https://raw.githubusercontent.com/nefarius/ViGEmBus/v1.22.0/LICENSE"
$expectedSha256 = "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A"

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$installer = Join-Path $Destination $assetName
Invoke-WebRequest -Uri $assetUri -OutFile $installer
$actualSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "ViGEmBus installer checksum mismatch. Expected $expectedSha256, got $actualSha256."
}
Invoke-WebRequest -Uri $licenseUri -OutFile (Join-Path $Destination "LICENSE-ViGEmBus.txt")

Write-Host "Prepared ViGEmBus $version in $Destination"
