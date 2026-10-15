[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath($ArtifactRoot)
$metadataPath = Join-Path $root 'engineering-build.json'
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
    throw "Engineering metadata '$metadataPath' is missing."
}
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$expectedProperties = @(
    'automaticApplyOnStartup',
    'channel',
    'displayName',
    'mode',
    'packageId',
    'packages',
    'payloadDirectory',
    'productionApproved',
    'runtimeIdentifier',
    'schemaVersion',
    'setup',
    'signed',
    'version')
$actualProperties = @($metadata.PSObject.Properties.Name | Sort-Object)
if (($actualProperties -join "`n") -cne (($expectedProperties | Sort-Object) -join "`n")) {
    throw 'Engineering metadata has missing or unknown properties.'
}
if ($metadata.schemaVersion -ne 1 -or
    $metadata.mode -cne 'UNSIGNEDENGINEERING' -or
    $metadata.displayName -cne 'Stillmark' -or
    $metadata.packageId -cne 'RelicScreenshot' -or
    $metadata.version -cne $ExpectedVersion -or
    $metadata.channel -cne 'stable' -or
    $metadata.runtimeIdentifier -cne 'win-x64' -or
    [bool]$metadata.signed -or
    [bool]$metadata.productionApproved -or
    [bool]$metadata.automaticApplyOnStartup -or
    $metadata.payloadDirectory -cne 'payload') {
    throw 'Engineering metadata does not describe the expected unsigned Stillmark package.'
}

$markerPath = Join-Path $root 'UNSIGNED-ENGINEERING.txt'
$payloadMarkerPath = Join-Path $root 'payload\UNSIGNED-ENGINEERING.txt'
if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $payloadMarkerPath -PathType Leaf)) {
    throw 'Unsigned engineering markers are missing.'
}

$setupPath = Join-Path $root "releases\$($metadata.setup)"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "Setup output '$setupPath' is missing."
}
$packageNames = @($metadata.packages)
if ($packageNames.Count -lt 1) {
    throw 'No Velopack package was recorded.'
}
foreach ($packageName in $packageNames) {
    $packagePath = Join-Path $root "releases\$packageName"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Recorded package '$packagePath' is missing."
    }
}

$payloadExecutable = Join-Path $root 'payload\WinShot.exe'
if (-not (Test-Path -LiteralPath $payloadExecutable -PathType Leaf)) {
    throw "Payload executable '$payloadExecutable' is missing."
}

[pscustomobject]@{
    Result = 'PASS_VELOPACK_UNSIGNED_ENGINEERING'
    ArtifactRoot = $root
    Setup = $setupPath
    PackageCount = $packageNames.Count
    PackageId = $metadata.packageId
    Version = $metadata.version
    Signed = [bool]$metadata.signed
    ProductionApproved = [bool]$metadata.productionApproved
}
