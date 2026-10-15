[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][DateTimeOffset]$ReleasedAtUtc,
    [Parameter(Mandatory = $true)][string]$AzureTrustedSignFile,
    [Parameter(Mandatory = $true)][string]$CatalogSigningKeyPath,
    [Parameter(Mandatory = $true)][string]$ExpectedPublisherSubject,
    [Parameter(Mandatory = $true)][string]$ExpectedSubscriberIdentityEku,
    [string]$OutputRoot,
    [string]$VpkPath = 'vpk'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Signing.Common.ps1')

function Resolve-ExecutablePath {
    param([Parameter(Mandatory = $true)][string]$Value)
    if (Test-Path -LiteralPath $Value -PathType Leaf) {
        return [System.IO.Path]::GetFullPath($Value)
    }
    $command = Get-Command $Value -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "vpk executable '$Value' was not found."
    }
    return [System.IO.Path]::GetFullPath($command.Source)
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or [Version]::Parse($Version).Major -le 0) {
    throw "Version '$Version' must be a three-part production version with a positive major."
}
if ($ReleasedAtUtc.ToUnixTimeSeconds() -le 0) {
    throw 'ReleasedAtUtc is mandatory and must be a positive immutable release timestamp.'
}
if (-not (Test-Path -LiteralPath $PayloadDirectory -PathType Container)) {
    throw "Payload directory '$PayloadDirectory' was not found."
}
if (-not (Test-Path -LiteralPath $AzureTrustedSignFile -PathType Leaf)) {
    throw 'AzureTrustedSignFile must point to the externally-created Azure Artifact Signing profile metadata file.'
}
if (-not (Test-Path -LiteralPath $CatalogSigningKeyPath -PathType Leaf)) {
    throw 'CatalogSigningKeyPath must point to externally-held release signing material; the script never creates keys.'
}
$nonProductionMarkers = Get-ChildItem -LiteralPath $PayloadDirectory -File -Recurse |
    Where-Object {
        $_.Name -like '*-ENGINEERING.txt' -or
        $_.Name -ceq 'UNSIGNED-TEST-ARTIFACT.txt'
    }
if ($nonProductionMarkers) {
    throw 'Production Velopack packaging refused: an engineering or unsigned-test marker is in the payload.'
}
if (-not (Test-Path -LiteralPath (Join-Path $PayloadDirectory 'WinShot.exe') -PathType Leaf)) {
    throw "Payload '$PayloadDirectory' does not contain WinShot.exe."
}
foreach ($notice in @('THIRD-PARTY-NOTICES.md', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path (Join-Path $PayloadDirectory $notice) -PathType Leaf)) {
        throw "Production payload is missing $notice."
    }
}

$profileText = [System.IO.File]::ReadAllText($AzureTrustedSignFile)
try {
    $null = $profileText | ConvertFrom-Json
}
catch {
    throw 'AzureTrustedSignFile must contain valid JSON profile metadata.'
}

$resolvedVpk = Resolve-ExecutablePath -Value $VpkPath
$vpkHelp = @(& $resolvedVpk '--vhelp')
if ($LASTEXITCODE -ne 0 -or -not ($vpkHelp -match 'Velopack CLI 1\.2\.0')) {
    throw 'Production packaging requires the pinned official vpk CLI 1.2.0.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\velopack\production\$Version"
}
$outputPath = [System.IO.Path]::GetFullPath($OutputRoot)
$repositoryArtifacts = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\velopack')).TrimEnd('\')
if (-not $outputPath.StartsWith("$repositoryArtifacts\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain below '$repositoryArtifacts'."
}
if (Test-Path -LiteralPath $outputPath) {
    throw "Output '$outputPath' already exists; production release outputs are immutable."
}

$releaseDirectory = Join-Path $outputPath 'releases'
$signTool = @(Get-ChildItem ([IO.Path]::GetDirectoryName($resolvedVpk)) -Recurse -File -Filter signtool.exe)[0].FullName
$thirdPartyHashes = @{}
foreach ($file in Get-ChildItem $PayloadDirectory -Recurse -File -Filter '*.dll') {
    if ($file.BaseName -notmatch '^WinShot(?:[.].*)?$') {
        $relative = [IO.Path]::GetRelativePath($PayloadDirectory, $file.FullName)
        $thirdPartyHashes[$relative] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    }
}
$iconPath = Join-Path $repositoryRoot 'src\WinShot.App\Assets\winshot.ico'
[System.IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
$releaseNotesPath = Join-Path $outputPath 'release-notes.md'
$releaseDateIso = $ReleasedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
$releaseNotes = @"
# Stillmark $Version

Release date: $releaseDateIso

This production release is distributed through the stable Velopack channel.
"@
[System.IO.File]::WriteAllText(
    $releaseNotesPath,
    $releaseNotes.Replace("`r`n", "`n"),
    [System.Text.UTF8Encoding]::new($false))
$arguments = @(
    'pack',
    '--packId', 'RelicScreenshot',
    '--packVersion', $Version,
    '--packDir', ([System.IO.Path]::GetFullPath($PayloadDirectory)),
    '--outputDir', $releaseDirectory,
    '--channel', 'stable',
    '--runtime', 'win-x64',
    '--framework', 'vcredist143-x64',
    '--packTitle', 'Stillmark',
    '--packAuthors', 'Iftat Bhuiyan',
    '--mainExe', 'WinShot.exe',
    '--icon', $iconPath,
    '--delta', 'None',
    '--shortcuts', 'StartMenuRoot',
    '--releaseNotes', $releaseNotesPath,
    '--signExclude', '(?i)(^|[\\/])(?!WinShot(?:[.][^\\/]+)*[.]dll$)[^\\/]+[.]dll$',
    '--azureTrustedSignFile', ([System.IO.Path]::GetFullPath($AzureTrustedSignFile))
)
& $resolvedVpk @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Azure Artifact Signing Velopack pack failed with exit code $LASTEXITCODE."
}

$fullPackages = @(Get-ChildItem $releaseDirectory -File -Filter '*-full.nupkg')
$setups = @(Get-ChildItem $releaseDirectory -File -Filter '*Setup.exe')
if ($fullPackages.Count -ne 1 -or $setups.Count -ne 1) {
    throw 'Production packaging must produce exactly one full package and installer.'
}
$verificationPath = Join-Path $outputPath 'package-verification'
Expand-SafeZipArchive -ArchivePath $fullPackages[0].FullName -DestinationPath $verificationPath
$appPath = Join-Path $verificationPath 'lib/app'
foreach ($required in @('WinShot.exe', 'WinShot_ExecutionStub.exe', 'Squirrel.exe')) {
    if (-not (Test-Path (Join-Path $appPath $required) -PathType Leaf)) {
        throw "Production package is missing $required."
    }
}
$owned = @(Get-ChildItem $appPath -Recurse -File | Where-Object {
    $_.Name -match '^WinShot(?:[.][^\\/]+)*[.](?:exe|dll)$' -or
        $_.Name -in @('Squirrel.exe', 'WinShot_ExecutionStub.exe')
}) + $setups
foreach ($file in $owned) {
    Assert-SignedReleaseArtifact -Path $file.FullName -PublisherSubject $ExpectedPublisherSubject `
        -SubscriberIdentityEku $ExpectedSubscriberIdentityEku -SignToolPath $signTool
}
foreach ($relative in $thirdPartyHashes.Keys) {
    $path = Join-Path $appPath $relative
    if (-not (Test-Path $path -PathType Leaf) -or
        (Get-FileHash $path -Algorithm SHA256).Hash -cne $thirdPartyHashes[$relative]) {
        throw "Production packaging changed third-party DLL $relative."
    }
}

$catalogScript = Join-Path $PSScriptRoot 'New-VelopackReleaseCatalog.ps1'
& $catalogScript `
    -ArtifactRoot $outputPath `
    -Version $Version `
    -ReleasedAtUtc $ReleasedAtUtc `
    -SigningKeyPath $CatalogSigningKeyPath `
    -OutputDirectory $outputPath
if ($LASTEXITCODE -ne 0) {
    throw 'Signed release catalog generation failed.'
}

[pscustomobject]@{
    OutputRoot = $outputPath
    Setup = (Get-ChildItem -LiteralPath $releaseDirectory -Filter '*Setup.exe' -File).FullName
    Package = (Get-ChildItem -LiteralPath $releaseDirectory -Filter '*.nupkg' -File).FullName
    Catalog = Join-Path $outputPath "RelicScreenshot-$Version-release-catalog.json"
    CatalogSignature = Join-Path $outputPath "RelicScreenshot-$Version-release-catalog.sig"
    PackageId = 'RelicScreenshot'
    Channel = 'stable'
    RuntimeIdentifier = 'win-x64'
    SignedByAzureArtifactSigning = $true
    AutomaticApplyOnStartup = $false
}
