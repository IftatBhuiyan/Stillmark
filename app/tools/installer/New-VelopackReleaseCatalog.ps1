[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ArtifactRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][DateTimeOffset]$ReleasedAtUtc,
    [Parameter(Mandatory = $true)][string]$SigningKeyPath,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SafeVersion {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Version '$Value' must be a three-part production semantic version."
    }
    $parsed = [Version]::Parse($Value)
    if ($parsed.Major -le 0 -or $parsed.Build -lt 0 -or $parsed.Revision -ge 0) {
        throw "Version '$Value' is not a valid production release version."
    }
}

function Get-JsonString {
    param([Parameter(Mandatory = $true)][string]$Value)
    return ConvertTo-Json -InputObject $Value -Compress
}

Assert-SafeVersion -Value $Version
if ($ReleasedAtUtc.ToUnixTimeSeconds() -le 0) {
    throw 'ReleasedAtUtc must be an explicit positive release timestamp; production dates are never invented.'
}
if (-not (Test-Path -LiteralPath $ArtifactRoot -PathType Container)) {
    throw "ArtifactRoot '$ArtifactRoot' was not found."
}
$artifactPath = [System.IO.Path]::GetFullPath($ArtifactRoot)
$packageId = 'RelicScreenshot'
$packageFileName = "$packageId-$Version-stable-full.nupkg"
$package = Join-Path (Join-Path $artifactPath 'releases') $packageFileName
$setupFiles = @(Get-ChildItem -LiteralPath (Join-Path $artifactPath 'releases') -Filter '*Setup.exe' -File)
if (-not (Test-Path -LiteralPath $package -PathType Leaf) -or $setupFiles.Count -ne 1) {
    throw "A completed Velopack release must contain exactly '$packageFileName' and one Setup.exe."
}

foreach ($unsignedMarker in @(
        (Join-Path $artifactPath 'UNSIGNED-ENGINEERING.txt'),
        (Join-Path $artifactPath 'engineering-build.json'),
        (Join-Path (Join-Path $artifactPath 'payload') 'UNSIGNED-ENGINEERING.txt'))) {
    if (Test-Path -LiteralPath $unsignedMarker -PathType Leaf) {
        throw "Production catalog refused: unsigned engineering marker '$unsignedMarker' is present."
    }
}
if (-not (Test-Path -LiteralPath $SigningKeyPath -PathType Leaf)) {
    throw "Catalog signing key '$SigningKeyPath' was not found."
}

$setupSignature = Get-AuthenticodeSignature -LiteralPath $setupFiles[0].FullName
if ($setupSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Production catalog refused: Setup.exe must have a valid Azure Artifact Signing signature; status was '$($setupSignature.Status)'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageZip = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $nuspecEntries = @($packageZip.Entries | Where-Object {
        $_.FullName -match '^[^/\\]+\.nuspec$'
    })
    if ($nuspecEntries.Count -ne 1 -or $nuspecEntries[0].Length -gt 64KB) {
        throw 'Production catalog refused: package must contain exactly one bounded root nuspec manifest.'
    }
    $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
    try {
        $nuspecText = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
    if ($nuspecText.Contains('<!DOCTYPE', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production catalog refused: nuspec DTD declarations are not allowed.'
    }
    $nuspec = [System.Xml.Linq.XDocument]::Parse(
        $nuspecText,
        [System.Xml.Linq.LoadOptions]::PreserveWhitespace)
    $metadataNodes = @($nuspec.Descendants() | Where-Object { $_.Name.LocalName -eq 'metadata' })
    if ($metadataNodes.Count -ne 1) {
        throw 'Production catalog refused: nuspec metadata is missing or ambiguous.'
    }
    $metadata = $metadataNodes[0]
    function Get-MetadataText {
        param([Parameter(Mandatory = $true)][System.Xml.Linq.XElement]$Parent, [Parameter(Mandatory = $true)][string]$Name)
        $nodes = @($Parent.Elements() | Where-Object { $_.Name.LocalName -eq $Name })
        if ($nodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$nodes[0].Value)) {
            throw "Production catalog refused: nuspec '$Name' is missing or ambiguous."
        }
        return [string]$nodes[0].Value
    }
    $manifestMatches = (Get-MetadataText $metadata 'id') -ceq $packageId
    $manifestMatches = $manifestMatches -and ((Get-MetadataText $metadata 'version') -ceq $Version)
    $manifestMatches = $manifestMatches -and ((Get-MetadataText $metadata 'channel') -ceq 'stable')
    $manifestMatches = $manifestMatches -and ((Get-MetadataText $metadata 'rid') -ceq 'win-x64')
    if (-not $manifestMatches) {
        throw 'Production catalog refused: nuspec identity, version, channel, or runtime does not match the requested release.'
    }
    $releaseDateIso = $ReleasedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    $releaseNotes = Get-MetadataText $metadata 'releaseNotes'
    if ($releaseNotes.Replace("`r`n", "`n") -notmatch "(?m)^Release date: $([regex]::Escape($releaseDateIso))$") {
        throw 'Production catalog refused: nuspec release notes do not carry the exact immutable release timestamp.'
    }
}
finally {
    $packageZip.Dispose()
}

$packageInfo = Get-Item -LiteralPath $package
if ($packageInfo.Length -le 0 -or $packageInfo.Length -gt 512MB) {
    throw "Package '$packageFileName' exceeds the catalog package size budget."
}
$packageBytes = [System.IO.File]::ReadAllBytes($packageInfo.FullName)
$packageSha256 = [Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData($packageBytes))

$protectedKey = [IO.Path]::GetExtension($SigningKeyPath) -eq '.dpapi'
$keyText = if ($protectedKey) { '' } else { [System.IO.File]::ReadAllText($SigningKeyPath) }
$containsPublicKeyMarker = $keyText.Contains('PUBLIC KEY')
$containsPrivateKeyMarker = $keyText.Contains('PRIVATE KEY')
if (($keyText.Length -gt 16KB) -or ($containsPublicKeyMarker -and -not $containsPrivateKeyMarker)) {
    throw 'Catalog signing key must be a bounded RSA private PEM key.'
}
$signer = [System.Security.Cryptography.RSA]::Create()
try {
    if ($protectedKey) {
        Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
        $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect(
            [IO.File]::ReadAllBytes($SigningKeyPath), $null,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
        try {
            $consumed = 0
            $signer.ImportPkcs8PrivateKey($privateBytes, [ref]$consumed)
            if ($consumed -ne $privateBytes.Length) { throw 'Unexpected trailing private-key bytes.' }
        }
        finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($privateBytes) }
    }
    else { $signer.ImportFromPem($keyText) }
    if ($signer.KeySize -lt 3072) {
        throw 'Catalog signing key must be at least 3072 bits.'
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = $artifactPath
    }
    $outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
    [System.IO.Directory]::CreateDirectory($outputPath) | Out-Null
    $catalogName = "$packageId-$Version-release-catalog.json"
    $catalogPath = Join-Path $outputPath $catalogName
    $signaturePath = Join-Path $outputPath "$packageId-$Version-release-catalog.sig"
    if ((Test-Path -LiteralPath $catalogPath) -or (Test-Path -LiteralPath $signaturePath)) {
        throw "Catalog output already exists for '$Version'; immutable release metadata is never overwritten."
    }

    $releasedSeconds = $ReleasedAtUtc.ToUnixTimeSeconds().ToString(
        [Globalization.CultureInfo]::InvariantCulture)
    $catalog = @"
{
  "schemaVersion": 1,
  "application": $(Get-JsonString 'Stillmark'),
  "packageId": $(Get-JsonString $packageId),
  "channel": "stable",
  "runtimeIdentifier": "win-x64",
  "version": $(Get-JsonString $Version),
  "releasedAtUtc": $releasedSeconds,
  "packageFileName": $(Get-JsonString $packageFileName),
  "packageBytes": $($packageInfo.Length),
  "packageSha256": "$packageSha256"
}
"@
    $catalogBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        $catalog.Replace("`r`n", "`n"))
    $signature = $signer.SignData(
        $catalogBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pss)
    [System.IO.File]::WriteAllBytes($catalogPath, $catalogBytes)
    [System.IO.File]::WriteAllBytes($signaturePath, $signature)

    [pscustomobject]@{
        Catalog = $catalogPath
        Signature = $signaturePath
        Package = $packageInfo.FullName
        PackageBytes = $packageInfo.Length
        PackageSha256 = $packageSha256
        ReleasedAtUtc = $ReleasedAtUtc.ToUniversalTime().ToString('O')
        Signed = $true
        AzureArtifactSigningSetup = $setupFiles[0].FullName
    }
}
finally {
    $signer.Dispose()
}
