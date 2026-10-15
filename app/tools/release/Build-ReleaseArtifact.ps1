[CmdletBinding()]
param(
    [ValidateSet("UnsignedTest", "Production")]
    [string]$Mode = "UnsignedTest",

    [string]$Version,

    [string]$ReleaseTag,

    [string]$DotnetPath = (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"),

    [string]$OutputRoot,

    [string]$Owner,

    [string]$Publisher,

    [string]$PackageId,

    [string]$SigningCertificate,

    [string]$TimestampUrl,

    [string]$AzureTrustedSignFile,

    [string]$CatalogSigningKeyPath,

    [string]$ExpectedSubscriberIdentityEku,

    [DateTimeOffset]$ReleasedAtUtc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Release.Common.ps1")

$repositoryRoot = Get-WinShotRepositoryRoot
$configuration = Get-ReleaseConfiguration -RepositoryRoot $repositoryRoot
$componentEvidence = Get-ComponentEvidence -RepositoryRoot $repositoryRoot
$projectVersion = Get-ProjectReleaseVersion -RepositoryRoot $repositoryRoot
$commercialValidationScript = Join-Path $PSScriptRoot "Test-CommercialConfiguration.ps1"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectVersion
}
Assert-SafeReleaseVersion -Version $Version
if ($Version -ne $projectVersion) {
    throw "Requested version '$Version' does not match the product version '$projectVersion'. Update AppInfo.cs and Directory.Build.props together before packaging."
}
$releaseTagWasSupplied = -not [string]::IsNullOrWhiteSpace($ReleaseTag)
if (-not $releaseTagWasSupplied) {
    $ReleaseTag = "v$Version"
}
$releaseGovernanceScript = Join-Path $PSScriptRoot "Test-ReleaseGovernance.ps1"
$releaseGovernance = & $releaseGovernanceScript -ReleaseTag $ReleaseTag

if ($Mode -eq "Production") {
    $missing = @()
    foreach ($input in @{
        Owner = $Owner
        Publisher = $Publisher
        PackageId = $PackageId
        SigningCertificate = $SigningCertificate
        TimestampUrl = $TimestampUrl
        ReleaseTag = if ($releaseTagWasSupplied) { $ReleaseTag } else { $null }
    }.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$input.Value)) {
            $missing += $input.Key
        }
    }

    if ($missing.Count -gt 0) {
        $sortedMissing = Get-OrdinalSortedStrings -Values $missing
        throw "Production release refused: explicit inputs are missing: $($sortedMissing -join ', ')."
    }
    if ($PackageId -match '(?i)provisional|winshot' -or $Publisher -eq "UNSET") {
        throw "Production release refused: package identity/publisher must be final and name-cleared."
    }
    $null = & $commercialValidationScript -Mode Production
    $timestamp = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestamp) -or
        ($timestamp.Scheme -ne [Uri]::UriSchemeHttps -and
            $TimestampUrl -cne "http://timestamp.acs.microsoft.com/")) {
        throw "Production release refused: TimestampUrl must be an absolute HTTPS RFC 3161 endpoint or the exact Microsoft Artifact Signing TSA."
    }

    if ([string]::IsNullOrWhiteSpace($AzureTrustedSignFile) -or
        [string]::IsNullOrWhiteSpace($CatalogSigningKeyPath) -or
        [string]::IsNullOrWhiteSpace($ExpectedSubscriberIdentityEku) -or
        $ReleasedAtUtc.ToUnixTimeSeconds() -le 0) {
        throw 'Production packaging requires AzureTrustedSignFile, CatalogSigningKeyPath, ExpectedSubscriberIdentityEku, and ReleasedAtUtc.'
    }
    $appInfo = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src/WinShot.App/AppInfo.cs'))
    $date = $ReleasedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    if (-not $appInfo.Contains('ReleaseDateUtc = "' + $date + '"') -or
        -not $appInfo.Contains('PackageId = "' + $PackageId + '"') -or
        -not $appInfo.Contains('ExpectedPublisherSubject = "' + $Publisher + '"')) {
        throw 'Production identity and date must match the compiled application.'
    }
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        throw 'Pass the pinned dotnet executable path.'
    }
    $sdk = (& $DotnetPath --version).Trim()
    if ($sdk -cne (Get-Content (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version) {
        throw 'Production packaging requires the pinned SDK.'
    }
    $payload = Join-Path $repositoryRoot "artifacts/velopack/payload-$Version"
    if (Test-Path $payload) { throw 'Production payload output already exists.' }
    & $DotnetPath publish (Join-Path $repositoryRoot 'src/WinShot.App/WinShot.App.csproj') `
        -c Release -r win-x64 --self-contained true -o $payload `
        -p:Platform=x64 -p:PublishTrimmed=false -p:PublishSingleFile=false `
        -p:ContinuousIntegrationBuild=true -p:TreatWarningsAsErrors=true `
        -p:DebugSymbols=false -p:DebugType=None -p:WinShotLocalDeveloperUse=false `
        -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Production payload build failed.' }
    Copy-Item (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') $payload
    $runtimeRoot = Split-Path -Parent $DotnetPath
    Copy-Item (Join-Path $runtimeRoot 'LICENSE.txt') (Join-Path $payload 'DOTNET-LICENSE.txt')
    Copy-Item (Join-Path $runtimeRoot 'ThirdPartyNotices.txt') (Join-Path $payload 'DOTNET-THIRD-PARTY-NOTICES.txt')
    # This creates and verifies a candidate. Hosting/promotion remains a separate action
    # after clean-machine installation and customer delivery checks succeed.
    & (Join-Path $repositoryRoot 'tools/installer/Publish-VelopackProduction.ps1') `
        -PayloadDirectory $payload -Version $Version -ReleasedAtUtc $ReleasedAtUtc `
        -AzureTrustedSignFile $AzureTrustedSignFile -CatalogSigningKeyPath $CatalogSigningKeyPath `
        -ExpectedPublisherSubject $Publisher -ExpectedSubscriberIdentityEku $ExpectedSubscriberIdentityEku `
        -OutputRoot $OutputRoot
    return
}

$commercialValidation = & $commercialValidationScript -Mode Authoring
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "Pinned dotnet executable '$DotnetPath' was not found."
}
$dotnetRoot = [System.IO.Path]::GetDirectoryName(
    [System.IO.Path]::GetFullPath($DotnetPath))
foreach ($legalSource in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $dotnetRoot $legalSource) -PathType Leaf)) {
        throw "Pinned .NET legal source '$legalSource' is missing."
    }
}
$sourceProvenanceBefore = Get-ReleaseSourceProvenance -RepositoryRoot $repositoryRoot
$resolvedSdk = @(& $DotnetPath --version)
if ($LASTEXITCODE -ne 0 -or $resolvedSdk.Count -ne 1 -or
    [string]$resolvedSdk[0] -cne [string]$sourceProvenanceBefore.PinnedSdkVersion) {
    throw "Release dotnet host did not resolve the pinned SDK '$($sourceProvenanceBefore.PinnedSdkVersion)'."
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\release"
}
$outputPath = [System.IO.Path]::GetFullPath($OutputRoot)
$repositoryPath = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\')
$allowedRepositoryOutput = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts\release")).TrimEnd('\')
if ($outputPath.StartsWith(($repositoryPath + '\'), [System.StringComparison]::OrdinalIgnoreCase) -and
    -not ($outputPath -eq $allowedRepositoryOutput -or
        $outputPath.StartsWith(
            ($allowedRepositoryOutput + '\'),
            [System.StringComparison]::OrdinalIgnoreCase))) {
    throw "Repository-local release output is restricted to artifacts\release."
}

$artifactName = "WinShot-$Version-$($configuration.runtimeIdentifier)-UNSIGNED-TEST"
$artifactDirectory = Join-Path $outputPath $artifactName
$zipPath = Join-Path $outputPath "$artifactName.zip"
if ((Test-Path -LiteralPath $artifactDirectory) -or (Test-Path -LiteralPath $zipPath)) {
    throw "Release output already exists for '$artifactName'. Choose a clean OutputRoot; existing artifacts are never overwritten."
}

$projectPath = Join-Path $repositoryRoot "src\WinShot.App\WinShot.App.csproj"
$startupBuildScript = Join-Path $repositoryRoot `
    "tools\startup\Build-PackagedStartupBootstrap.ps1"
$runtimeIdentifier = [string]$configuration.runtimeIdentifier
& $DotnetPath restore $projectPath --locked-mode --runtime $runtimeIdentifier
if ($LASTEXITCODE -ne 0) {
    throw "Runtime-specific locked restore failed with exit code $LASTEXITCODE. Do not fall back to an unlocked release restore."
}

[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
$versionParts = $Version.Split('-', 2)[0].Split('.')
$fourPartVersion = "$($versionParts[0]).$($versionParts[1]).$($versionParts[2]).0"
$cleanArguments = @(
    "clean",
    $projectPath,
    "--configuration", "Release",
    "--runtime", $runtimeIdentifier,
    "-p:Platform=x64",
    "-p:TreatWarningsAsErrors=true"
)
& $DotnetPath @cleanArguments
if ($LASTEXITCODE -ne 0) {
    throw "Runtime-specific Release clean failed with exit code $LASTEXITCODE."
}

$publishArguments = @(
    "publish",
    $projectPath,
    "--configuration", "Release",
    "--runtime", $runtimeIdentifier,
    "--self-contained", "true",
    "--no-restore",
    "--output", $artifactDirectory,
    "-p:Platform=x64",
    "-p:PublishTrimmed=false",
    "-p:PublishSingleFile=false",
    "-p:ContinuousIntegrationBuild=true",
    "-p:TreatWarningsAsErrors=true",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-p:Version=$Version",
    "-p:FileVersion=$fourPartVersion",
    "-p:AssemblyVersion=$fourPartVersion",
    "-p:InformationalVersion=$Version"
)

& $DotnetPath @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$startupArtifactDirectory = Join-Path `
    (Join-Path $repositoryRoot 'artifacts\startup') `
    ('release-' + [Guid]::NewGuid().ToString('N'))
try {
    & $startupBuildScript `
        -OutputDirectory $startupArtifactDirectory `
        -DotnetPath $DotnetPath
    foreach ($startupFile in @(
        'WinShot.Startup.exe',
        'startup-build.json')) {
        $source = Join-Path $startupArtifactDirectory $startupFile
        $destination = Join-Path $artifactDirectory $startupFile
        if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
            (Test-Path -LiteralPath $destination)) {
            throw "Packaged startup payload '$startupFile' is missing or conflicts with publish output."
        }
        Copy-Item -LiteralPath $source -Destination $destination
    }
}
finally {
    $startupArtifactsRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot 'artifacts\startup')).TrimEnd('\')
    $resolvedStartupArtifact = [System.IO.Path]::GetFullPath(
        $startupArtifactDirectory)
    if (-not $resolvedStartupArtifact.StartsWith(
            ($startupArtifactsRoot + '\'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean packaged-startup output outside '$startupArtifactsRoot'."
    }
    if (Test-Path -LiteralPath $resolvedStartupArtifact) {
        Remove-Item -LiteralPath $resolvedStartupArtifact -Recurse -Force
    }
}
$sourceProvenanceAfter = Get-ReleaseSourceProvenance -RepositoryRoot $repositoryRoot
Assert-ReleaseSourceProvenance `
    -Expected $sourceProvenanceBefore `
    -Actual $sourceProvenanceAfter

Remove-NonCustomerPublishFiles -ArtifactDirectory $artifactDirectory
foreach ($reservedMetadata in @(
    "THIRD-PARTY-NOTICES.md",
    "commercial-config.json",
    "component-evidence.json",
    "DOTNET-LICENSE.txt",
    "DOTNET-THIRD-PARTY-NOTICES.txt",
    "UNSIGNED-TEST-ARTIFACT.txt",
    "payload-attribution.json",
    "SBOM.cdx.json",
    "release-manifest.json",
    "SHA256SUMS")) {
    if (Test-Path -LiteralPath (Join-Path $artifactDirectory $reservedMetadata)) {
        throw "Publish output unexpectedly contains reserved release file '$reservedMetadata'; it will not be overwritten."
    }
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot "THIRD-PARTY-NOTICES.md") `
    -Destination (Join-Path $artifactDirectory "THIRD-PARTY-NOTICES.md")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "packaging\UNSIGNED-TEST-ARTIFACT.txt") `
    -Destination (Join-Path $artifactDirectory "UNSIGNED-TEST-ARTIFACT.txt")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "packaging\commercial-config.json") `
    -Destination (Join-Path $artifactDirectory "commercial-config.json")
Copy-Item -LiteralPath (Join-Path $repositoryRoot "packaging\component-evidence.json") `
    -Destination (Join-Path $artifactDirectory "component-evidence.json")
Copy-Item -LiteralPath (Join-Path $dotnetRoot "LICENSE.txt") `
    -Destination (Join-Path $artifactDirectory "DOTNET-LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $dotnetRoot "ThirdPartyNotices.txt") `
    -Destination (Join-Path $artifactDirectory "DOTNET-THIRD-PARTY-NOTICES.txt")

Assert-NoForbiddenReleaseContent -ArtifactDirectory $artifactDirectory -RepositoryRoot $repositoryRoot
Write-PayloadAttribution `
    -ArtifactDirectory $artifactDirectory `
    -Version $Version `
    -Configuration $configuration `
    -ComponentEvidence $componentEvidence
Write-DependencyInventory `
    -ArtifactDirectory $artifactDirectory `
    -Version $Version `
    -ComponentEvidence $componentEvidence
Write-ReleaseManifest -ArtifactDirectory $artifactDirectory -Version $Version `
    -RuntimeIdentifier ([string]$configuration.runtimeIdentifier) `
    -SourceProvenance $sourceProvenanceAfter `
    -CommercialConfiguration $commercialValidation
Write-ChecksumFile -ArtifactDirectory $artifactDirectory

$validationScript = Join-Path $PSScriptRoot "Test-ReleaseArtifact.ps1"
$validation = & $validationScript `
    -ArtifactDirectory $artifactDirectory `
    -ExpectedVersion $Version `
    -DotnetPath $DotnetPath
New-DeterministicZip -ArtifactDirectory $artifactDirectory -DestinationPath $zipPath
Assert-ReleaseZipMatchesDirectory -ArtifactDirectory $artifactDirectory -ArchivePath $zipPath
$archiveValidationScript = Join-Path $PSScriptRoot "Test-ReleaseArchive.ps1"
$archiveValidation = & $archiveValidationScript `
    -ArchivePath $zipPath `
    -ExpectedVersion $Version `
    -DotnetPath $DotnetPath

[pscustomobject]@{
    Mode = "UNSIGNED_TEST"
    ArtifactDirectory = $artifactDirectory
    Archive = $zipPath
    ArchiveSha256 = Get-Sha256Hex -Path $zipPath
    Version = $Version
    ReleaseTag = $ReleaseTag
    ReleaseNotesSha256 = $releaseGovernance.ReleaseNotesSha256
    Validation = $validation.Result
    ArchiveValidation = $archiveValidation.Result
    FileCount = $validation.FileCount
    ProductionApproved = $false
}
