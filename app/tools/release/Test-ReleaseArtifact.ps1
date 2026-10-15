[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [string]$DotnetPath = (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Release.Common.ps1")
. (Join-Path $PSScriptRoot "Release.SourceBinding.ps1")

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $actual = Get-OrdinalSortedStrings -Values @($Object.PSObject.Properties.Name)
    $expectedSorted = Get-OrdinalSortedStrings -Values $Expected
    if (($actual -join "`n") -cne ($expectedSorted -join "`n")) {
        throw "$Context contains missing or unknown properties."
    }
}

function Assert-SafeMetadataRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$MaximumCharacters
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        $Path.Length -gt $MaximumCharacters -or
        $Path.Contains('\') -or
        $Path.StartsWith('/') -or
        $Path.Contains(':') -or
        $Path.Contains("`r") -or
        $Path.Contains("`n")) {
        throw "Release metadata contains an unsafe or noncanonical path."
    }

    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or
            $segment -in @('.', '..') -or
            $segment.EndsWith(' ') -or
            $segment.EndsWith('.') -or
            $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "Release metadata contains an unsafe path segment."
        }
        $deviceStem = [System.IO.Path]::GetFileNameWithoutExtension($segment)
        if ($deviceStem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "Release metadata contains a reserved Windows device path."
        }
    }
}

Assert-SafeReleaseVersion -Version $ExpectedVersion
$repositoryRoot = Get-WinShotRepositoryRoot
$configuration = Get-ReleaseConfiguration -RepositoryRoot $repositoryRoot
$artifactPath = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (-not (Test-Path -LiteralPath $artifactPath -PathType Container)) {
    throw "Artifact directory '$artifactPath' does not exist."
}

foreach ($required in @($configuration.requiredPayloadFiles)) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifactPath $required) -PathType Leaf)) {
        throw "Required release payload '$required' is missing."
    }
}

foreach ($requiredMetadata in @(
    "component-evidence.json",
    "DOTNET-LICENSE.txt",
    "DOTNET-THIRD-PARTY-NOTICES.txt",
    "payload-attribution.json",
    "release-manifest.json",
    "SBOM.cdx.json",
    "SHA256SUMS")) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifactPath $requiredMetadata) -PathType Leaf)) {
        throw "Required release metadata '$requiredMetadata' is missing."
    }
}
$componentEvidence = Get-ComponentEvidence -RepositoryRoot $repositoryRoot
if ((Get-Sha256Hex -Path (Join-Path $artifactPath 'component-evidence.json')) -cne
    (Get-Sha256Hex -Path (Join-Path $repositoryRoot 'packaging\component-evidence.json'))) {
    throw "Artifact component evidence differs from the source-reviewed contract."
}

Assert-NoForbiddenReleaseContent -ArtifactDirectory $artifactPath -RepositoryRoot $repositoryRoot
$payloadProvenance = Assert-ReleasePayloadProvenance `
    -ArtifactDirectory $artifactPath `
    -Configuration $configuration
$sourceBinding = Assert-ReleasePayloadSourceBinding `
    -ArtifactDirectory $artifactPath `
    -RepositoryRoot $repositoryRoot `
    -DotnetPath $DotnetPath `
    -ComponentEvidence $componentEvidence
foreach ($x64Payload in @(
    "WinShot.exe",
    "WinShot.Startup.exe",
    "ScreenRecorderLib.dll",
    "coreclr.dll",
    "clrjit.dll",
    "hostfxr.dll",
    "hostpolicy.dll")) {
    Assert-X64Pe -Path (Join-Path $artifactPath $x64Payload)
}

$startupValidationRoot = Join-Path `
    ([System.IO.Path]::GetTempPath()) `
    ('winshot-startup-payload-validation-' + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Directory]::CreateDirectory($startupValidationRoot) | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $artifactPath 'WinShot.Startup.exe') `
        -Destination (Join-Path $startupValidationRoot 'WinShot.Startup.exe')
    Copy-Item `
        -LiteralPath (Join-Path $artifactPath 'startup-build.json') `
        -Destination (Join-Path $startupValidationRoot 'startup-build.json')
    $startupHash = Get-Sha256Hex `
        -Path (Join-Path $startupValidationRoot 'WinShot.Startup.exe')
    [System.IO.File]::WriteAllText(
        (Join-Path $startupValidationRoot 'SHA256SUMS'),
        "$startupHash  WinShot.Startup.exe`n",
        [System.Text.UTF8Encoding]::new($false))
    & (Join-Path $repositoryRoot `
        'tools\startup\Test-PackagedStartupBootstrap.ps1') `
        -ArtifactDirectory $startupValidationRoot `
        -Mode Authoring `
        -DotnetPath $DotnetPath
}
finally {
    $temporaryRoot = [System.IO.Path]::GetFullPath(
        ([System.IO.Path]::GetTempPath())).TrimEnd('\') + '\'
    $resolvedStartupValidationRoot = [System.IO.Path]::GetFullPath(
        $startupValidationRoot)
    if (-not $resolvedStartupValidationRoot.StartsWith(
            $temporaryRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean startup validation outside the temporary root."
    }
    if (Test-Path -LiteralPath $resolvedStartupValidationRoot) {
        Remove-Item -LiteralPath $resolvedStartupValidationRoot -Recurse -Force
    }
}

$runtimeConfig = Get-Content -LiteralPath (Join-Path $artifactPath "WinShot.runtimeconfig.json") -Raw |
    ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.includedFrameworks) {
    throw "WinShot.runtimeconfig.json does not describe an included self-contained framework."
}
$includedFrameworkNames = @($runtimeConfig.runtimeOptions.includedFrameworks |
    ForEach-Object { [string]$_.name })
foreach ($frameworkName in @("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")) {
    if ($frameworkName -notin $includedFrameworkNames) {
        throw "Self-contained runtime configuration is missing '$frameworkName'."
    }
}

$deps = Get-Content -LiteralPath (Join-Path $artifactPath "WinShot.deps.json") -Raw | ConvertFrom-Json
if ([string]$deps.runtimeTarget.name -notmatch 'win-x64') {
    throw "WinShot.deps.json runtime target '$($deps.runtimeTarget.name)' is not win-x64."
}

$fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $artifactPath "WinShot.exe"))
if (-not ([string]$fileInfo.ProductVersion).StartsWith(
    $ExpectedVersion,
    [System.StringComparison]::Ordinal)) {
    throw "WinShot.exe product version '$($fileInfo.ProductVersion)' does not start with '$ExpectedVersion'."
}
foreach ($ownedAssembly in @(
    "WinShot.Startup.exe",
    "WinShot.dll",
    "WinShot.Core.dll",
    "WinShot.Capture.dll",
    "WinShot.Interop.dll",
    "WinShot.Licensing.dll")) {
    $ownedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
        (Join-Path $artifactPath $ownedAssembly))
    if (-not ([string]$ownedVersion.ProductVersion).StartsWith(
        $ExpectedVersion,
        [System.StringComparison]::Ordinal)) {
        throw "$ownedAssembly product version '$($ownedVersion.ProductVersion)' does not start with '$ExpectedVersion'."
    }
}

$manifest = Get-Content -LiteralPath (Join-Path $artifactPath "release-manifest.json") -Raw |
    ConvertFrom-Json
Assert-ExactProperties -Object $manifest -Context "Release manifest" -Expected @(
    'schemaVersion',
    'application',
    'version',
    'runtimeIdentifier',
    'packageKind',
    'selfContained',
    'trimmed',
    'singleFile',
    'sourceProvenance',
    'commercialConfiguration',
    'files')
if ($manifest.schemaVersion -ne 1 -or
    $manifest.application -cne "WinShot" -or
    $manifest.version -cne $ExpectedVersion -or
    $manifest.runtimeIdentifier -cne [string]$configuration.runtimeIdentifier -or
    $manifest.packageKind -cne "UNSIGNED_TEST" -or
    -not $manifest.selfContained -or $manifest.trimmed -or $manifest.singleFile) {
    throw "Release manifest does not match the unsigned, self-contained, untrimmed, multi-file contract."
}
$commercialValidationScript = Join-Path $PSScriptRoot "Test-CommercialConfiguration.ps1"
$commercialValidation = & $commercialValidationScript `
    -Mode Authoring `
    -ConfigurationPath (Join-Path $artifactPath "commercial-config.json")
$manifestCommercial = $manifest.PSObject.Properties['commercialConfiguration']
if (-not $manifestCommercial) {
    throw "Release manifest commercial configuration evidence is missing."
}
$manifestCommercial = $manifestCommercial.Value
if ([string]$manifestCommercial.sha256 -cne
        [string]$commercialValidation.ConfigurationSha256 -or
    [int]$manifestCommercial.contractRevision -ne
        [int]$commercialValidation.ContractRevision -or
    [int]$manifestCommercial.applicationMajor -ne
        [int]$commercialValidation.ApplicationMajor -or
    [string]$manifestCommercial.licenseMode -cne
        [string]$commercialValidation.LicenseMode -or
    [bool]$manifestCommercial.checkoutApproved -ne
        [bool]$commercialValidation.CheckoutApproved -or
    [bool]$manifestCommercial.buyerTermsApproved -ne
        [bool]$commercialValidation.BuyerTermsApproved -or
    [string]$manifestCommercial.primaryVariantId -cne
        [string]$commercialValidation.PrimaryVariantId -or
    (($manifestCommercial.provider | ConvertTo-Json -Depth 6 -Compress) -cne
        ($commercialValidation.Provider | ConvertTo-Json -Depth 6 -Compress))) {
    throw "Release manifest commercial configuration evidence does not match the payload."
}
$manifestProvenanceProperty = $manifest.PSObject.Properties['sourceProvenance']
if (-not $manifestProvenanceProperty) {
    throw "Release manifest source provenance is missing."
}
$manifestProvenance = $manifestProvenanceProperty.Value
foreach ($requiredProvenanceProperty in @(
    'commitSha',
    'workingTreeDirty',
    'buildInputsDirty',
    'buildInputsSha256',
    'buildInputFileCount',
    'packagingToolsSha256',
    'packagingToolFileCount',
    'pinnedSdkVersion')) {
    if (-not $manifestProvenance -or
        -not $manifestProvenance.PSObject.Properties[$requiredProvenanceProperty]) {
        throw "Release manifest source provenance is missing '$requiredProvenanceProperty'."
    }
}
if (
    [string]$manifestProvenance.commitSha -notmatch '^[0-9a-f]{40}$' -or
    [string]$manifestProvenance.buildInputsSha256 -notmatch '^[A-F0-9]{64}$' -or
    [string]$manifestProvenance.packagingToolsSha256 -notmatch '^[A-F0-9]{64}$' -or
    [int]$manifestProvenance.buildInputFileCount -lt 1 -or
    [int]$manifestProvenance.packagingToolFileCount -lt 1 -or
    [string]$manifestProvenance.pinnedSdkVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
    $manifestProvenance.workingTreeDirty -isnot [bool] -or
    $manifestProvenance.buildInputsDirty -isnot [bool]) {
    throw "Release manifest source provenance is missing or malformed."
}
$expectedProvenance = [pscustomobject]@{
    CommitSha = [string]$manifestProvenance.commitSha
    WorkingTreeDirty = [bool]$manifestProvenance.workingTreeDirty
    BuildInputsDirty = [bool]$manifestProvenance.buildInputsDirty
    BuildInputsSha256 = [string]$manifestProvenance.buildInputsSha256
    BuildInputFileCount = [int]$manifestProvenance.buildInputFileCount
    PackagingToolsSha256 = [string]$manifestProvenance.packagingToolsSha256
    PackagingToolFileCount = [int]$manifestProvenance.packagingToolFileCount
    PinnedSdkVersion = [string]$manifestProvenance.pinnedSdkVersion
}
$currentProvenance = Get-ReleaseSourceProvenance -RepositoryRoot $repositoryRoot
Assert-ReleaseSourceProvenance -Expected $expectedProvenance -Actual $currentProvenance

if ($manifest.files -isnot [System.Array] -or @($manifest.files).Count -lt 1) {
    throw "Release manifest files must be a nonempty array."
}
$manifestPathSet = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in @($manifest.files)) {
    Assert-ExactProperties -Object $entry -Context "Release manifest file entry" -Expected @(
        'path',
        'bytes',
        'sha256')
    $entryPath = [string]$entry.path
    Assert-SafeMetadataRelativePath `
        -Path $entryPath `
        -MaximumCharacters ([int]$configuration.validationLimits.maximumPathCharacters)
    if (-not $manifestPathSet.Add($entryPath)) {
        throw "Release manifest contains a duplicate or case-colliding path."
    }
    if ([string]$entry.sha256 -cnotmatch '^[A-F0-9]{64}$' -or
        [long]$entry.bytes -lt 0 -or
        [long]$entry.bytes -gt [long]$configuration.validationLimits.maximumFileBytes) {
        throw "Release manifest file entry has an invalid hash or byte length."
    }
    $path = Join-Path $artifactPath $entryPath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Manifest file '$($entry.path)' is missing."
    }
    $actualHash = Get-Sha256Hex -Path $path
    if ($actualHash -ne [string]$entry.sha256) {
        throw "Manifest hash mismatch for '$($entry.path)'."
    }
    if ((Get-Item -LiteralPath $path).Length -ne [long]$entry.bytes) {
        throw "Manifest size mismatch for '$($entry.path)'."
    }
}

$manifestPaths = @($manifest.files | ForEach-Object { [string]$_.path })
$actualManifestPaths = Get-OrdinalSortedStrings -Values @(
    Get-ReleaseFiles -ArtifactDirectory $artifactPath |
    Where-Object { $_.Name -notin @('release-manifest.json', 'SHA256SUMS') } |
    ForEach-Object { Get-RelativeReleasePath -Root $artifactPath -Path $_.FullName })
if (($manifestPaths -join "`n") -cne ($actualManifestPaths -join "`n")) {
    throw "Release manifest inventory does not exactly match the artifact directory."
}

$checksumLines = Get-Content -LiteralPath (Join-Path $artifactPath "SHA256SUMS")
$checksumPaths = @()
$checksumPathSet = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($line in $checksumLines) {
    if ($line -notmatch '^([A-F0-9]{64})  (.+)$') {
        throw "Malformed SHA256SUMS line."
    }
    $checksumPath = [string]$Matches[2]
    Assert-SafeMetadataRelativePath `
        -Path $checksumPath `
        -MaximumCharacters ([int]$configuration.validationLimits.maximumPathCharacters)
    if (-not $checksumPathSet.Add($checksumPath)) {
        throw "SHA256SUMS contains a duplicate or case-colliding path."
    }
    $path = Join-Path $artifactPath $checksumPath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Checksum file '$($Matches[2])' is missing."
    }
    if ((Get-Sha256Hex -Path $path) -ne $Matches[1]) {
        throw "Checksum mismatch for '$($Matches[2])'."
    }
    $checksumPaths += $checksumPath
}
$actualChecksumPaths = Get-OrdinalSortedStrings -Values @(
    Get-ReleaseFiles -ArtifactDirectory $artifactPath |
    Where-Object { $_.Name -ne 'SHA256SUMS' } |
    ForEach-Object { Get-RelativeReleasePath -Root $artifactPath -Path $_.FullName })
if (($checksumPaths -join "`n") -cne ($actualChecksumPaths -join "`n")) {
    throw "SHA256SUMS inventory does not exactly match the artifact directory."
}

$notice = Get-Content -LiteralPath (Join-Path $artifactPath "UNSIGNED-TEST-ARTIFACT.txt") -Raw
if ($notice -notmatch 'not signed' -or $notice -notmatch 'not approved for sale') {
    throw "Unsigned test artifact warning is missing required release-blocking language."
}

$unsignedOwnedPayload = @(
    "WinShot.exe",
    "WinShot.Startup.exe",
    "WinShot.dll",
    "WinShot.Core.dll",
    "WinShot.Capture.dll",
    "WinShot.Interop.dll",
    "WinShot.Licensing.dll",
    "ScreenRecorderLib.dll")
Assert-UnsignedReleasePayloadSignatures `
    -ArtifactDirectory $artifactPath `
    -RelativePaths $unsignedOwnedPayload
$authenticode = Get-AuthenticodeSignature -LiteralPath (Join-Path $artifactPath "WinShot.exe")

$attribution = Get-Content `
    -LiteralPath (Join-Path $artifactPath 'payload-attribution.json') `
    -Raw |
    ConvertFrom-Json
Assert-ExactProperties -Object $attribution -Context "Payload attribution" -Expected @(
    'schemaVersion',
    'application',
    'version',
    'legalReviewApproved',
    'reviewNotice',
    'files')
if ($attribution.schemaVersion -ne 1 -or
    $attribution.application -cne 'WinShot' -or
    $attribution.version -cne $ExpectedVersion -or
    [bool]$attribution.legalReviewApproved -ne [bool]$componentEvidence.legalReviewApproved -or
    [string]$attribution.reviewNotice -cne [string]$componentEvidence.reviewNotice) {
    throw "Payload attribution identity or review boundary is invalid."
}
$expectedAttribution = @(Get-PayloadAttributionRecords `
    -ArtifactDirectory $artifactPath `
    -Version $ExpectedVersion `
    -Configuration $configuration `
    -ComponentEvidence $componentEvidence)
$actualAttribution = @($attribution.files)
if ($actualAttribution.Count -ne $expectedAttribution.Count) {
    throw "Payload attribution file count is not exact."
}
for ($index = 0; $index -lt $expectedAttribution.Count; $index++) {
    $actual = $actualAttribution[$index]
    $expected = $expectedAttribution[$index]
    Assert-ExactProperties -Object $actual -Context "Payload attribution file entry" -Expected @(
        'path',
        'bytes',
        'sha256',
        'bomRef',
        'sourceBomRef',
        'sourceKind')
    if ([string]$actual.path -cne [string]$expected.path -or
        [long]$actual.bytes -ne [long]$expected.bytes -or
        [string]$actual.sha256 -cne [string]$expected.sha256 -or
        [string]$actual.bomRef -cne [string]$expected.bomRef -or
        [string]$actual.sourceBomRef -cne [string]$expected.sourceBomRef -or
        [string]$actual.sourceKind -cne [string]$expected.sourceKind) {
        throw "Payload attribution does not match exact path, hash, size, or origin evidence."
    }
}
$expectedAttributionDocument = Get-PayloadAttributionDocument `
    -ArtifactDirectory $artifactPath `
    -Version $ExpectedVersion `
    -Configuration $configuration `
    -ComponentEvidence $componentEvidence
if (($attribution | ConvertTo-Json -Depth 10 -Compress) -cne
    ($expectedAttributionDocument | ConvertTo-Json -Depth 10 -Compress)) {
    throw "Payload attribution is not the exact canonical typed document."
}

$sbom = Get-Content -LiteralPath (Join-Path $artifactPath "SBOM.cdx.json") -Raw | ConvertFrom-Json
$expectedSbom = Get-DependencyInventoryDocument `
    -ArtifactDirectory $artifactPath `
    -Version $ExpectedVersion `
    -ComponentEvidence $componentEvidence
$actualSbomJson = $sbom | ConvertTo-Json -Depth 12 -Compress
$expectedSbomJson = $expectedSbom | ConvertTo-Json -Depth 12 -Compress
if ($actualSbomJson -cne $expectedSbomJson) {
    throw "Dependency inventory is not the exact canonical CycloneDX 1.7 payload-derived document."
}

$files = Get-ReleaseFiles -ArtifactDirectory $artifactPath
[pscustomobject]@{
    ArtifactDirectory = $artifactPath
    Version = $ExpectedVersion
    FileCount = $files.Count
    TotalBytes = ($files | Measure-Object -Property Length -Sum).Sum
    ExecutableSignature = [string]$authenticode.Status
    DependencyAttributedFiles = $payloadProvenance.DependencyAttributedFiles
    SatelliteFiles = $payloadProvenance.SatelliteFiles
    SourceBoundFiles = $sourceBinding.TotalBoundFiles
    Result = "PASS_UNSIGNED_TEST"
}
