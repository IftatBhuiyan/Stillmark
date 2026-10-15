[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LauncherValidation.Common.ps1')

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$allowedRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\installer-layout')).TrimEnd('\') + '\'
$root = [System.IO.Path]::GetFullPath($ArtifactDirectory).TrimEnd('\')
if (-not ($root + '\').StartsWith(
        $allowedRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Installer-layout mutation tests require an artifact below '$allowedRoot'."
}
$validator = Join-Path $PSScriptRoot 'Test-InstallerLayout.ps1'
& $validator `
    -ArtifactDirectory $root `
    -Mode Authoring `
    -DotnetPath $DotnetPath

$pointerPath = Join-Path $root 'active-version'
$metadataPath = Join-Path $root 'installer-layout.json'
$launcherPath = Join-Path $root 'WinShot.exe'
$launcherBuildPath = Join-Path $root 'launcher-build.json'
$version = [System.Text.Encoding]::ASCII.GetString(
    [System.IO.File]::ReadAllBytes($pointerPath))
$payloadPath = Join-Path $root ("versions\$version\WinShot.exe")
$payloadManifestPath = Join-Path $root (
    "versions\$version\release-manifest.json")
$originalPointer = [System.IO.File]::ReadAllBytes($pointerPath)
$originalMetadata = [System.IO.File]::ReadAllBytes($metadataPath)
$originalLauncher = [System.IO.File]::ReadAllBytes($launcherPath)
$originalLauncherBuild = [System.IO.File]::ReadAllBytes($launcherBuildPath)
$originalPayload = [System.IO.File]::ReadAllBytes($payloadPath)
$originalPayloadManifest = [System.IO.File]::ReadAllBytes($payloadManifestPath)
$currentPath = Join-Path $root 'current'

function Assert-ExpectedRefusal {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    $message = $null
    try {
        & $validator `
            -ArtifactDirectory $root `
            -Mode Authoring `
            -DotnetPath $DotnetPath
    }
    catch {
        $message = $_.Exception.Message
    }
    if ($null -eq $message -or $message -cnotmatch $Pattern) {
        throw "Installer-layout mutation '$Name' did not refuse at '$Pattern'. Actual '$message'."
    }
    Write-Host "Expected installer-layout refusal passed: $Name"
}

try {
    [System.IO.File]::WriteAllBytes(
        $pointerPath,
        [System.Text.Encoding]::ASCII.GetBytes('../1.0.0'))
    Assert-ExpectedRefusal -Name 'pointer-traversal' -Pattern 'active-version is not canonical'
    [System.IO.File]::WriteAllBytes($pointerPath, $originalPointer)

    [System.IO.File]::WriteAllBytes($launcherPath, $originalPayload)
    Assert-ExpectedRefusal -Name 'payload-as-launcher' -Pattern 'substituted with the payload'
    [System.IO.File]::WriteAllBytes($launcherPath, $originalLauncher)

    [System.IO.Directory]::CreateDirectory($currentPath) | Out-Null
    Assert-ExpectedRefusal -Name 'replace-in-place-current' `
        -Pattern 'unexpected item|must not contain'
    [System.IO.Directory]::Delete($currentPath, $false)

    [System.IO.File]::Delete($payloadPath)
    Assert-ExpectedRefusal -Name 'missing-active-payload' `
        -Pattern 'required launcher or payload file is missing'
    [System.IO.File]::WriteAllBytes($payloadPath, $originalPayload)

    $metadataText = [System.Text.Encoding]::UTF8.GetString($originalMetadata)
    $launcherHash = (
        Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256
    ).Hash
    [System.IO.File]::WriteAllText(
        $metadataPath,
        $metadataText.Replace($launcherHash, ('0' * 64)),
        [System.Text.UTF8Encoding]::new($false))
    Assert-ExpectedRefusal -Name 'launcher-binding-tamper' `
        -Pattern 'launcher or payload-manifest binding is invalid'
    [System.IO.File]::WriteAllBytes($metadataPath, $originalMetadata)

    $duplicateMetadata = $metadataText -replace (
        '(?s)^(\s*\{\s*"schemaVersion"\s*:\s*1)'),
        '$1, "schemaVersion": 1'
    [System.IO.File]::WriteAllText(
        $metadataPath,
        $duplicateMetadata,
        [System.Text.UTF8Encoding]::new($false))
    Assert-ExpectedRefusal -Name 'duplicate-layout-property' `
        -Pattern 'duplicate property name'
    [System.IO.File]::WriteAllBytes($metadataPath, $originalMetadata)

    $launcherBuildText = [System.Text.Encoding]::UTF8.GetString(
        $originalLauncherBuild)
    $launcherBuild = $launcherBuildText | ConvertFrom-Json
    $sourceHash = [string]@($launcherBuild.inputs)[0].sha256
    [System.IO.File]::WriteAllText(
        $launcherBuildPath,
        $launcherBuildText.Replace($sourceHash, ('0' * 64)),
        [System.Text.UTF8Encoding]::new($false))
    Assert-ExpectedRefusal -Name 'launcher-source-provenance' `
        -Pattern 'build input provenance mismatch'
    [System.IO.File]::WriteAllBytes(
        $launcherBuildPath,
        $originalLauncherBuild)

    $duplicateLauncherBuild = $launcherBuildText -replace (
        '(?s)^(\s*\{\s*"schemaVersion"\s*:\s*1)'),
        '$1, "schemaVersion": 1'
    [System.IO.File]::WriteAllText(
        $launcherBuildPath,
        $duplicateLauncherBuild,
        [System.Text.UTF8Encoding]::new($false))
    Assert-ExpectedRefusal -Name 'duplicate-launcher-property' `
        -Pattern 'duplicate property name'
    [System.IO.File]::WriteAllBytes(
        $launcherBuildPath,
        $originalLauncherBuild)

    $payloadManifestText = [System.Text.Encoding]::UTF8.GetString(
        $originalPayloadManifest)
    $payloadManifest = $payloadManifestText | ConvertFrom-Json
    $buildInputsHash = [string]$payloadManifest.sourceProvenance.buildInputsSha256
    [System.IO.File]::WriteAllText(
        $payloadManifestPath,
        $payloadManifestText.Replace($buildInputsHash, ('0' * 64)),
        [System.Text.UTF8Encoding]::new($false))
    Assert-ExpectedRefusal -Name 'stale-nested-source-provenance' `
        -Pattern 'Release source provenance changed|BuildInputsSha256'
}
finally {
    [System.IO.File]::WriteAllBytes($pointerPath, $originalPointer)
    [System.IO.File]::WriteAllBytes($metadataPath, $originalMetadata)
    [System.IO.File]::WriteAllBytes($launcherPath, $originalLauncher)
    [System.IO.File]::WriteAllBytes(
        $launcherBuildPath,
        $originalLauncherBuild)
    [System.IO.File]::WriteAllBytes($payloadPath, $originalPayload)
    [System.IO.File]::WriteAllBytes(
        $payloadManifestPath,
        $originalPayloadManifest)
    if (Test-Path -LiteralPath $currentPath -PathType Container) {
        [System.IO.Directory]::Delete($currentPath, $false)
    }
}

& $validator `
    -ArtifactDirectory $root `
    -Mode Authoring `
    -DotnetPath $DotnetPath

$launcherMetadata = (
    Read-LauncherStrictJson `
        -Path $launcherBuildPath `
        -Context 'Stable-launcher build metadata' `
        -MaximumBytes 131072
).Value
$dotnetInput = @($launcherMetadata.inputs |
    Where-Object { [string]$_.path -ceq 'dotnet.exe' })
if ($dotnetInput.Count -ne 1) {
    throw "Determinism test cannot resolve the launcher dotnet input."
}
$resolvedDotnet = Resolve-LauncherDotnetPath `
    -DotnetPath $DotnetPath `
    -ExpectedSdkVersion ([string]$launcherMetadata.sdkVersion) `
    -ExpectedHostSha256 ([string]$dotnetInput[0].sha256)
$launcherArtifactsRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\launcher')).TrimEnd('\') + '\'
$launcherFixture = Join-Path $launcherArtifactsRoot (
    '.layout-test-' + [Guid]::NewGuid().ToString('N'))
$comparisonRoot = Join-Path $allowedRoot (
    '.determinism-' + [Guid]::NewGuid().ToString('N'))
try {
    [System.IO.Directory]::CreateDirectory($launcherFixture) | Out-Null
    [System.IO.File]::Copy(
        $launcherPath,
        (Join-Path $launcherFixture 'WinShot.exe'),
        $false)
    [System.IO.File]::Copy(
        $launcherBuildPath,
        (Join-Path $launcherFixture 'launcher-build.json'),
        $false)
    $launcherHash = Get-LauncherSha256 -Path $launcherPath
    [System.IO.File]::WriteAllText(
        (Join-Path $launcherFixture 'SHA256SUMS'),
        "$launcherHash  WinShot.exe`n",
        [System.Text.UTF8Encoding]::new($false))

    & (Join-Path $PSScriptRoot 'Build-InstallerLayout.ps1') `
        -LauncherArtifactDirectory $launcherFixture `
        -PayloadDirectory (Join-Path $root "versions\$version") `
        -Version $version `
        -OutputDirectory $comparisonRoot `
        -DotnetPath $resolvedDotnet

    $expectedRecords = @(
        Get-ChildItem -LiteralPath $root -File -Recurse |
            ForEach-Object {
                [pscustomobject]@{
                    Path = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                    Bytes = $_.Length
                    Sha256 = Get-LauncherSha256 -Path $_.FullName
                }
            }
    )
    $actualRecords = @(
        Get-ChildItem -LiteralPath $comparisonRoot -File -Recurse |
            ForEach-Object {
                [pscustomobject]@{
                    Path = $_.FullName.Substring(
                        $comparisonRoot.Length + 1).Replace('\', '/')
                    Bytes = $_.Length
                    Sha256 = Get-LauncherSha256 -Path $_.FullName
                }
            }
    )
    $expectedRecords = Get-LauncherOrdinalRecords `
        -Records $expectedRecords `
        -PropertyName 'Path'
    $actualRecords = Get-LauncherOrdinalRecords `
        -Records $actualRecords `
        -PropertyName 'Path'
    if ($expectedRecords.Count -ne $actualRecords.Count) {
        throw "Installer-layout deterministic rebuild file count differs."
    }
    for ($index = 0; $index -lt $expectedRecords.Count; $index++) {
        if ($expectedRecords[$index].Path -cne $actualRecords[$index].Path -or
            $expectedRecords[$index].Bytes -ne $actualRecords[$index].Bytes -or
            $expectedRecords[$index].Sha256 -cne $actualRecords[$index].Sha256) {
            throw "Installer-layout deterministic rebuild differs at '$($expectedRecords[$index].Path)'."
        }
    }
}
finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($launcherFixture)
    $resolvedComparison = [System.IO.Path]::GetFullPath($comparisonRoot)
    if (-not ($resolvedFixture + '\').StartsWith(
            $launcherArtifactsRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ($resolvedComparison + '\').StartsWith(
            $allowedRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean deterministic layout fixtures outside artifact roots."
    }
    if (Test-Path -LiteralPath $resolvedFixture) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
    if (Test-Path -LiteralPath $resolvedComparison) {
        Remove-Item -LiteralPath $resolvedComparison -Recurse -Force
    }
}

Write-Host 'Installer-layout adversarial and deterministic rebuild tests passed.'
