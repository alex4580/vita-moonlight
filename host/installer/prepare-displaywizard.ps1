param(
    [string] $Destination = (Join-Path $PSScriptRoot "..\..\artifacts\displaywizard")
)

$ErrorActionPreference = "Stop"
$releaseTag = "beta_v0.614"
$assetName = "PRPlanIT.com-VirtualDisplayDrv_Wiz.zip"
$assetUri = "https://github.com/PrPlanIT/DisplayWizard/releases/download/$releaseTag/$assetName"
$licenseUri = "https://raw.githubusercontent.com/PrPlanIT/DisplayWizard/main/LICENSE"
$expectedSha256 = "7A4F3032D4A30E42BE89F878A0D98710C764F0734DF4F7EE04FF32EA3F6F6B75"
$archive = Join-Path ([System.IO.Path]::GetTempPath()) "vita-moonlight-displaywizard-$releaseTag.zip"
$driverReleaseTag = "25.5.2"
$driverVersion = "24.12.24"
$driverAssetName = "Signed-Driver-v$driverVersion-x64.zip"
$driverUri = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/$driverReleaseTag/$driverAssetName"
$driverLicenseUri = "https://raw.githubusercontent.com/VirtualDrivers/Virtual-Display-Driver/master/LICENSE"
$driverExpectedSha256 = "F93E7CE3D640C83C419B1B72D2C83B2D8E34D83E8B7BA11F3328A40D2DA82FA4"
$driverArchive = Join-Path ([System.IO.Path]::GetTempPath()) "vita-moonlight-vdd-$driverVersion.zip"
$driverExtract = Join-Path ([System.IO.Path]::GetTempPath()) "vita-moonlight-vdd-$driverVersion"
$vcRuntimeVersion = "14.44.35211.0"
$vcRuntimeUri = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
$vcRuntimeExpectedSha256 = "CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B"

if (Test-Path -LiteralPath $Destination) {
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

Invoke-WebRequest -Uri $assetUri -OutFile $archive
$actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "DisplayWizard archive checksum mismatch. Expected $expectedSha256, got $actualSha256."
}

Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
Invoke-WebRequest -Uri $licenseUri -OutFile (Join-Path $Destination "LICENSE-DisplayWizard.txt")
Remove-Item -LiteralPath $archive -Force

# The companion implements mode editing and driver reload itself. Keep the
# audited AutoHotkey source and signed nefcon helper, but do not ship the legacy
# GUI executable because it offers to download an obsolete, expired-certificate
# driver when launched interactively.
@(
    "PRPlanIT.com-VirtualDisplayDrv_Wiz.exe",
    "SunshineIntegration.bat",
    "PrPlanIT.com.ico"
) | ForEach-Object {
    $legacyRuntimePath = Join-Path $Destination $_
    if (Test-Path -LiteralPath $legacyRuntimePath) {
        Remove-Item -LiteralPath $legacyRuntimePath -Force
    }
}

Invoke-WebRequest -Uri $driverUri -OutFile $driverArchive
$driverActualSha256 = (Get-FileHash -LiteralPath $driverArchive -Algorithm SHA256).Hash
if ($driverActualSha256 -ne $driverExpectedSha256) {
    throw "Virtual Display Driver archive checksum mismatch. Expected $driverExpectedSha256, got $driverActualSha256."
}
if (Test-Path -LiteralPath $driverExtract) {
    Remove-Item -LiteralPath $driverExtract -Recurse -Force
}
Expand-Archive -LiteralPath $driverArchive -DestinationPath $driverExtract -Force
Copy-Item -LiteralPath (Join-Path $driverExtract "mttvdd.cat") -Destination $Destination
Copy-Item -LiteralPath (Join-Path $driverExtract "MttVDD.dll") -Destination $Destination
Copy-Item -LiteralPath (Join-Path $driverExtract "MttVDD.inf") -Destination $Destination
Copy-Item -LiteralPath (Join-Path $driverExtract "vdd_settings.xml") -Destination $Destination
Invoke-WebRequest -Uri $driverLicenseUri -OutFile (Join-Path $Destination "LICENSE-VirtualDisplayDriver.txt")
$driverCatalogSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Destination "mttvdd.cat")
if ($driverCatalogSignature.Status -ne "Valid") {
    throw "Virtual Display Driver catalog signature validation failed."
}
$nefconSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Destination "nefconw.exe")
if ($nefconSignature.Status -ne "Valid") {
    throw "nefcon publisher signature validation failed."
}
Remove-Item -LiteralPath $driverArchive -Force
Remove-Item -LiteralPath $driverExtract -Recurse -Force

$vcRuntimePath = Join-Path $Destination "VC_redist.x64.exe"
Invoke-WebRequest -Uri $vcRuntimeUri -OutFile $vcRuntimePath
$vcRuntimeActualSha256 = (Get-FileHash -LiteralPath $vcRuntimePath -Algorithm SHA256).Hash
if ($vcRuntimeActualSha256 -ne $vcRuntimeExpectedSha256) {
    throw "Microsoft Visual C++ runtime checksum mismatch. Expected $vcRuntimeExpectedSha256, got $vcRuntimeActualSha256."
}
$vcRuntimeSignature = Get-AuthenticodeSignature -LiteralPath $vcRuntimePath
if ($vcRuntimeSignature.Status -ne "Valid" -or $vcRuntimeSignature.SignerCertificate.Subject -notlike "CN=Microsoft Corporation,*") {
    throw "Microsoft Visual C++ runtime publisher signature validation failed."
}

$requiredFiles = @(
    "PRPlanIT.com-VirtualDisplayDrv_Wiz.ahk",
    "nefconw.exe",
    "README.md",
    "LICENSE-DisplayWizard.txt",
    "MttVDD.inf",
    "MttVDD.dll",
    "mttvdd.cat",
    "vdd_settings.xml",
    "VC_redist.x64.exe",
    "LICENSE-VirtualDisplayDriver.txt"
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $Destination $file))) {
        throw "The pinned DisplayWizard bundle is missing $file."
    }
}

Write-Host "Prepared DisplayWizard $releaseTag in $Destination"
Write-Host "Included Microsoft Visual C++ runtime $vcRuntimeVersion"
