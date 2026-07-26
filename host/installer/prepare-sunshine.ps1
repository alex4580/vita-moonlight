param(
    [string] $Destination = (Join-Path $PSScriptRoot "..\..\artifacts\sunshine")
)

$ErrorActionPreference = "Stop"
$version = "v2026.516.143833"
$assetName = "Sunshine-Windows-AMD64-installer.msi"
$assetUri = "https://github.com/LizardByte/Sunshine/releases/download/$version/$assetName"
$licenseUri = "https://raw.githubusercontent.com/LizardByte/Sunshine/$version/LICENSE"
$expectedSha256 = "E7208B11A4AB9DD89871133A054BBB8DC55DFBBA408227B0ECCAB22C60B273A2"

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$installer = Join-Path $Destination $assetName
Invoke-WebRequest -Uri $assetUri -OutFile $installer
$actualSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "Sunshine installer checksum mismatch. Expected $expectedSha256, got $actualSha256."
}
Invoke-WebRequest -Uri $licenseUri -OutFile (Join-Path $Destination "LICENSE-Sunshine.txt")

Write-Host "Prepared Sunshine $version in $Destination"
