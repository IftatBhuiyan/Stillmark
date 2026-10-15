[CmdletBinding()]
param(
    [ValidateSet('UnsignedEngineering', 'SignedEngineering')]
    [string]$Mode = 'UnsignedEngineering',

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$DotnetPath = 'dotnet',

    [string]$VpkPath = 'vpk',

    [string]$OutputRoot,

    [string]$AzureTrustedSignFile,

    [string]$ExpectedPublisherSubject,

    [string]$ExpectedSubscriberIdentityEku
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ExecutablePath {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (Test-Path -LiteralPath $Value -PathType Leaf) {
        return [System.IO.Path]::GetFullPath($Value)
    }

    $command = Get-Command $Value -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "$Name '$Value' was not found. Pass an explicit path."
    }

    return [System.IO.Path]::GetFullPath($command.Source)
}

function Assert-SafeVersion {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
        throw "Version '$Value' must be a three-part semantic version with an optional prerelease label."
    }
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

. (Join-Path $PSScriptRoot 'Signing.Common.ps1')

Assert-SafeVersion -Value $Version
$signedEngineering = $Mode -ceq 'SignedEngineering'
if ($signedEngineering) {
    if ([string]::IsNullOrWhiteSpace($AzureTrustedSignFile) -or
        -not (Test-Path -LiteralPath $AzureTrustedSignFile -PathType Leaf)) {
        throw 'SignedEngineering requires an external Azure Artifact Signing metadata JSON file.'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedPublisherSubject)) {
        throw 'SignedEngineering requires the exact expected publisher subject.'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedSubscriberIdentityEku) -or
        $ExpectedSubscriberIdentityEku -notmatch
            '^1[.]3[.]6[.]1[.]4[.]1[.]311[.]97[.](?!1[.]0$)(?!1[.]3[.]1[.])(?!1[.]4[.]1[.])[0-9]+([.][0-9]+)+$') {
        throw 'SignedEngineering requires a unique Public Trust subscriber identity EKU.'
    }
    try {
        $signingMetadata = Get-Content -LiteralPath $AzureTrustedSignFile -Raw | ConvertFrom-Json
    }
    catch {
        throw 'Azure Artifact Signing metadata must be valid JSON.'
    }
    foreach ($propertyName in @('Endpoint', 'CodeSigningAccountName', 'CertificateProfileName')) {
        $property = $signingMetadata.PSObject.Properties[$propertyName]
        if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            throw "Azure Artifact Signing metadata is missing '$propertyName'."
        }
    }
    $endpoint = $null
    if (-not [Uri]::TryCreate(
            [string]$signingMetadata.Endpoint,
            [UriKind]::Absolute,
            [ref]$endpoint) -or
        $endpoint.Scheme -cne [Uri]::UriSchemeHttps -or
        $endpoint.Host -cne 'eus.codesigning.azure.net' -or
        -not [string]::IsNullOrEmpty($endpoint.UserInfo) -or
        ($endpoint.Port -ne -1 -and $endpoint.Port -ne 443) -or
        $endpoint.AbsolutePath -ne '/' -or
        -not [string]::IsNullOrEmpty($endpoint.Query) -or
        -not [string]::IsNullOrEmpty($endpoint.Fragment)) {
        throw 'Azure Artifact Signing metadata Endpoint must be exactly the East US HTTPS Artifact Signing origin.'
    }
    if ([string]$signingMetadata.CodeSigningAccountName -cne 'winshot-signing' -or
        [string]$signingMetadata.CertificateProfileName -cne 'stillmark') {
        throw 'Azure Artifact Signing metadata account/profile does not match the approved engineering resource.'
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$projectPath = Join-Path $repositoryRoot 'src\WinShot.App\WinShot.App.csproj'
$appInfoPath = Join-Path $repositoryRoot 'src\WinShot.App\AppInfo.cs'
$iconPath = Join-Path $repositoryRoot 'src\WinShot.App\Assets\winshot.ico'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "App project '$projectPath' was not found."
}
if (-not (Test-Path -LiteralPath $appInfoPath -PathType Leaf)) {
    throw "App identity source '$appInfoPath' was not found."
}
$appInfoText = [System.IO.File]::ReadAllText($appInfoPath)
$appInfoVersionMatch = [regex]::Match(
    $appInfoText,
    '(?m)^\s*public\s+const\s+string\s+Version\s*=\s*"(?<version>[0-9]+\.[0-9]+\.[0-9]+)"\s*;')
if (-not $appInfoVersionMatch.Success) {
    throw "App identity source '$appInfoPath' does not contain the expected bounded Version constant."
}
$appInfoVersion = $appInfoVersionMatch.Groups['version'].Value
if ($Version -cne $appInfoVersion) {
    throw "Requested package version '$Version' does not match AppInfo.Version '$appInfoVersion'."
}
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Application icon '$iconPath' was not found."
}

$resolvedDotnet = Resolve-ExecutablePath -Value $DotnetPath -Name 'dotnet'
$dotnetRoot = [System.IO.Path]::GetDirectoryName($resolvedDotnet)
$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$sdkVersion = [string](& $resolvedDotnet '--version')
$expectedSdkVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($LASTEXITCODE -ne 0 -or $sdkVersion.Trim() -cne $expectedSdkVersion) {
    throw "The engineering installer requires repository SDK $expectedSdkVersion; resolved '$($sdkVersion.Trim())'."
}

$resolvedVpk = Resolve-ExecutablePath -Value $VpkPath -Name 'vpk'
$vpkHelp = @(& $resolvedVpk '--vhelp')
if ($LASTEXITCODE -ne 0 -or
    -not ($vpkHelp -match 'Velopack CLI 1\.2\.0')) {
    throw 'The engineering installer requires the official vpk CLI 1.2.0.'
}
$signToolPath = $null
if ($signedEngineering) {
    $signToolCandidates = @(Get-ChildItem -LiteralPath ([System.IO.Path]::GetDirectoryName($resolvedVpk)) `
        -Filter 'signtool.exe' -File -Recurse -ErrorAction SilentlyContinue)
    if ($signToolCandidates.Count -eq 0) {
        throw 'SignedEngineering requires the signtool.exe bundled with vpk.'
    }
    $signToolPath = $signToolCandidates[0].FullName
    if (-not (Test-Path -LiteralPath $signToolPath -PathType Leaf)) {
        throw 'SignedEngineering requires an existing signtool.exe.'
    }
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = if ($signedEngineering) {
        Join-Path $repositoryRoot "artifacts\velopack\signed-engineering\$Version"
    }
    else {
        Join-Path $repositoryRoot "artifacts\velopack\unsigned\$Version"
    }
}
$outputPath = [System.IO.Path]::GetFullPath($OutputRoot)
$publishDirectory = Join-Path $outputPath 'payload'
$releaseDirectory = Join-Path $outputPath 'releases'
$markerName = if ($signedEngineering) { 'SIGNED-ENGINEERING.txt' } else { 'UNSIGNED-ENGINEERING.txt' }
$markerPath = Join-Path $outputPath $markerName
$releaseNotesPath = Join-Path $outputPath 'release-notes.md'

$allowedRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\velopack')).TrimEnd('\')
if (-not $outputPath.StartsWith("$allowedRoot\", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain below '$allowedRoot'."
}
if (Test-Path -LiteralPath $outputPath) {
    throw "Output '$outputPath' already exists. Choose a clean output directory; existing artifacts are never overwritten."
}

[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
[System.IO.Directory]::CreateDirectory($publishDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null

$runtimeIdentifier = 'win-x64'
$publishArguments = @(
    'publish',
    $projectPath,
    '--configuration', 'Release',
    '--runtime', $runtimeIdentifier,
    '--self-contained', 'true',
    '--output', $publishDirectory,
    '-p:Platform=x64',
    '-p:PublishTrimmed=false',
    '-p:PublishSingleFile=false',
    '-p:ContinuousIntegrationBuild=true',
    '-p:TreatWarningsAsErrors=true',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    "-p:Version=$Version",
    "-p:FileVersion=$Version.0",
    "-p:AssemblyVersion=$Version.0",
    "-p:InformationalVersion=$Version",
    '-p:WinShotLocalDeveloperUse=false'
)
Invoke-External -FilePath $resolvedDotnet -Arguments $publishArguments `
    -FailureMessage 'dotnet publish failed.'

$mainExecutable = Join-Path $publishDirectory 'WinShot.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "Published app '$mainExecutable' was not produced."
}

$preservedThirdPartySignatures = [ordered]@{}
if ($signedEngineering) {
    foreach ($file in @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.dll')) {
        if ($file.BaseName -match '^WinShot(?:[.].*)?$') {
            continue
        }
        $relative = $file.FullName.Substring($publishDirectory.Length).TrimStart('\').Replace('\', '/')
        $preservedThirdPartySignatures[$relative] = (Get-FileHash `
            -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}

$engineeringMarker = @"
Stillmark engineering installer
Package ID: RelicScreenshot
Version: $Version
Mode: $(if ($signedEngineering) { 'SIGNED_ENGINEERING' } else { 'UNSIGNED_ENGINEERING' })

$(if ($signedEngineering) {
    'This private preview is signed for local trust and packaging validation only. It is not approved for public distribution, and has no production update feed or entitlement authority.'
}
else {
    'This artifact is for local packaging validation only. It is unsigned, is not approved for distribution, and has no production update feed or entitlement authority.'
})
"@
[System.IO.File]::WriteAllText($markerPath, $engineeringMarker, [System.Text.UTF8Encoding]::new($false))
$payloadMarkerPath = Join-Path $publishDirectory $markerName
Copy-Item -LiteralPath $markerPath -Destination $payloadMarkerPath
$engineeringNotes = @"
# Stillmark $Version

Status: $(if ($signedEngineering) { 'signed private preview' } else { 'unsigned engineering build' }).

This package validates the per-user Velopack installer path only. It is not approved for customer
distribution, does not contain production update metadata, and does not enable automatic update apply.
"@
[System.IO.File]::WriteAllText($releaseNotesPath, $engineeringNotes, [System.Text.UTF8Encoding]::new($false))

$packArguments = @(
    'pack',
    '--packId', 'RelicScreenshot',
    '--packVersion', $Version,
    '--packDir', $publishDirectory,
    '--outputDir', $releaseDirectory,
    '--channel', 'stable',
    '--runtime', $runtimeIdentifier,
    '--packTitle', 'Stillmark',
    '--packAuthors', 'Iftat Bhuiyan',
    '--mainExe', 'WinShot.exe',
    '--icon', $iconPath,
    '--delta', 'None',
    '--shortcuts', 'StartMenuRoot',
    '--releaseNotes', $releaseNotesPath
)
if ($signedEngineering) {
    $packArguments += @(
        '--signExclude', '(?i)(^|[\\/])(?!WinShot(?:[.][^\\/]+)*[.]dll$)[^\\/]+[.]dll$',
        '--azureTrustedSignFile', ([System.IO.Path]::GetFullPath($AzureTrustedSignFile))
    )
}
Invoke-External -FilePath $resolvedVpk -Arguments $packArguments `
    -FailureMessage 'vpk pack failed.'

$setup = @(Get-ChildItem -LiteralPath $releaseDirectory -Filter '*Setup.exe' -File)
$packages = @(Get-ChildItem -LiteralPath $releaseDirectory -Filter '*.nupkg' -File)
if ($setup.Count -ne 1 -or $packages.Count -lt 1) {
    throw "vpk did not produce the expected Setup.exe and package outputs."
}

if ($signedEngineering) {
    $fullPackages = @($packages | Where-Object { $_.Name -match '-full[.]nupkg$' })
    if ($fullPackages.Count -ne 1) {
        throw 'SignedEngineering requires exactly one generated full Velopack package.'
    }

    $verificationDirectory = Join-Path $outputPath 'package-verification'
    Expand-SafeZipArchive `
        -ArchivePath $fullPackages[0].FullName `
        -DestinationPath $verificationDirectory
    $packageAppDirectory = Join-Path $verificationDirectory 'lib\app'
    if (-not (Test-Path -LiteralPath $packageAppDirectory -PathType Container)) {
        throw 'SignedEngineering package is missing its lib/app payload directory.'
    }

    $ownedPackageArtifacts = @(Get-ChildItem -LiteralPath $packageAppDirectory -Recurse -File | Where-Object {
        $_.Name -match '^WinShot(?:[.][^\\/]+)*[.](?:exe|dll)$'
    })
    if ($ownedPackageArtifacts.Count -lt 1) {
        throw 'SignedEngineering found no packaged WinShot-owned EXE/DLL files to verify.'
    }
    foreach ($artifact in $ownedPackageArtifacts) {
        Assert-SignedReleaseArtifact `
            -Path $artifact.FullName `
            -PublisherSubject $ExpectedPublisherSubject `
            -SubscriberIdentityEku $ExpectedSubscriberIdentityEku `
            -SignToolPath $signToolPath
    }
    foreach ($requiredGeneratedName in @('WinShot_ExecutionStub.exe', 'Squirrel.exe')) {
        $requiredGeneratedPath = Join-Path $packageAppDirectory $requiredGeneratedName
        if (-not (Test-Path -LiteralPath $requiredGeneratedPath -PathType Leaf)) {
            throw "SignedEngineering package is missing required generated '$requiredGeneratedName'."
        }
        Assert-SignedReleaseArtifact `
            -Path $requiredGeneratedPath `
            -PublisherSubject $ExpectedPublisherSubject `
            -SubscriberIdentityEku $ExpectedSubscriberIdentityEku `
            -SignToolPath $signToolPath
    }
    Assert-SignedReleaseArtifact `
        -Path $setup[0].FullName `
        -PublisherSubject $ExpectedPublisherSubject `
        -SubscriberIdentityEku $ExpectedSubscriberIdentityEku `
        -SignToolPath $signToolPath

    $packagedThirdPartySignatures = [ordered]@{}
    foreach ($file in @(Get-ChildItem -LiteralPath $packageAppDirectory -Recurse -File -Filter '*.dll')) {
        if ($file.BaseName -match '^WinShot(?:[.].*)?$') {
            continue
        }
        $relative = $file.FullName.Substring($packageAppDirectory.Length).TrimStart('\').Replace('\', '/')
        $packagedThirdPartySignatures[$relative] = (Get-FileHash `
            -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    $sourceThirdPartyNames = @($preservedThirdPartySignatures.Keys | Sort-Object)
    $packageThirdPartyNames = @($packagedThirdPartySignatures.Keys | Sort-Object)
    if (($sourceThirdPartyNames -join '|') -cne ($packageThirdPartyNames -join '|')) {
        throw 'Signed engineering changed the set of packaged third-party DLLs.'
    }
    foreach ($entry in $preservedThirdPartySignatures.GetEnumerator()) {
        if ([string]$packagedThirdPartySignatures[$entry.Key] -cne [string]$entry.Value) {
            throw "Signed engineering changed the bytes of third-party DLL '$($entry.Key)'."
        }
    }
}

$metadata = [ordered]@{
    schemaVersion = 1
    mode = $Mode.ToUpperInvariant()
    displayName = 'Stillmark'
    packageId = 'RelicScreenshot'
    version = $Version
    channel = 'stable'
    runtimeIdentifier = $runtimeIdentifier
    signed = $signedEngineering
    productionApproved = $false
    automaticApplyOnStartup = $false
    payloadDirectory = 'payload'
    setup = $setup[0].Name
    packages = @($packages | Sort-Object Name | ForEach-Object { $_.Name })
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'engineering-build.json') -Encoding utf8NoBOM

[pscustomobject]@{
    Mode = $Mode
    OutputRoot = $outputPath
    Setup = $setup[0].FullName
    Packages = @($packages | Sort-Object Name | ForEach-Object { $_.FullName })
    PackageId = 'RelicScreenshot'
    DisplayName = 'Stillmark'
    Version = $Version
    Signed = $signedEngineering
    ProductionApproved = $false
}
