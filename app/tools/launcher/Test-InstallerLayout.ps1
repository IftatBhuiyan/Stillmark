[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [ValidateSet('Authoring', 'Production')]
    [string]$Mode = 'Authoring',

    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LauncherValidation.Common.ps1')

$root = [System.IO.Path]::GetFullPath($ArtifactDirectory).TrimEnd('\')
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Installer-layout artifact directory is unavailable."
}
$rootItem = Get-Item -LiteralPath $root
if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Installer-layout root cannot be a reparse point."
}
$expectedRootItems = @(
    'WinShot.exe',
    'active-version',
    'launcher-build.json',
    'installer-layout.json',
    'SHA256SUMS',
    'versions'
)
$actualRootItems = @(Get-ChildItem -LiteralPath $root -Force | ForEach-Object Name)
foreach ($name in $expectedRootItems) {
    if ($actualRootItems -cnotcontains $name) {
        throw "Installer-layout root is missing '$name'."
    }
}
if ($actualRootItems.Count -ne $expectedRootItems.Count) {
    throw "Installer-layout root has an unexpected item."
}
if (Test-Path -LiteralPath (Join-Path $root 'current')) {
    throw "Installer-layout must not contain a replace-in-place current directory."
}

$allItems = @(Get-ChildItem -LiteralPath $root -Force -Recurse)
if ($allItems.Count -gt 8192) {
    throw "Installer-layout item count exceeds the bound."
}
foreach ($item in $allItems) {
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Installer-layout contains a reparse point."
    }
}

$pointerPath = Join-Path $root 'active-version'
$pointerBytes = [System.IO.File]::ReadAllBytes($pointerPath)
if ($pointerBytes.Length -le 0 -or $pointerBytes.Length -gt 128 -or
    @($pointerBytes | Where-Object { $_ -gt 0x7F }).Count -ne 0) {
    throw "Installer-layout active-version is not bounded ASCII."
}
$version = [System.Text.Encoding]::ASCII.GetString($pointerBytes)
if ($version -cnotmatch '^[A-Za-z0-9](?:[A-Za-z0-9._+-]{0,126}[A-Za-z0-9])?$' -or
    $version -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:[.]|$)') {
    throw "Installer-layout active-version is not canonical."
}

$metadataPath = Join-Path $root 'installer-layout.json'
$checkedMetadata = Read-LauncherStrictJson `
    -Path $metadataPath `
    -Context 'Installer-layout metadata' `
    -MaximumBytes 4194304
$metadata = $checkedMetadata.Value
$propertyNames = @($metadata.PSObject.Properties | ForEach-Object Name)
$expectedProperties = @(
    'schemaVersion',
    'artifactKind',
    'packageId',
    'version',
    'activeVersion',
    'launcherSha256',
    'launcherBuildMetadataSha256',
    'payloadManifestSha256',
    'installerContractSha256',
    'layoutToolsSha256',
    'files'
)
if ($propertyNames.Count -ne $expectedProperties.Count) {
    throw "Installer-layout metadata has an unexpected property count."
}
foreach ($property in $expectedProperties) {
    if ($propertyNames -cnotcontains $property) {
        throw "Installer-layout metadata is missing '$property'."
    }
}
if ([int]$metadata.schemaVersion -ne 1 -or
    [string]$metadata.artifactKind -cne 'UNSIGNED_INSTALLER_LAYOUT_ENGINEERING' -or
    [string]$metadata.packageId -cne 'PROVISIONAL.WinShot' -or
    [string]$metadata.version -cne $version -or
    [string]$metadata.activeVersion -cne $version) {
    throw "Installer-layout metadata identity is invalid."
}

$launcherPath = Join-Path $root 'WinShot.exe'
$payloadRoot = Join-Path $root ('versions\' + $version)
$payloadPath = Join-Path $payloadRoot 'WinShot.exe'
$manifestPath = Join-Path $payloadRoot 'release-manifest.json'
foreach ($required in @($launcherPath, $payloadPath, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Installer-layout required launcher or payload file is missing."
    }
}
$launcherHash = (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash
$payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
if ($launcherHash -ceq $payloadHash) {
    throw "Installer-layout root launcher was substituted with the payload apphost."
}
$launcherEvidence = Assert-StableLauncherBuildEvidence `
    -ArtifactRoot $root `
    -RepositoryRoot $repositoryRoot `
    -Mode $Mode `
    -DotnetPath $DotnetPath
& (Join-Path $repositoryRoot 'tools\release\Test-ReleaseArtifact.ps1') `
    -ArtifactDirectory $payloadRoot `
    -ExpectedVersion $version `
    -DotnetPath $launcherEvidence.DotnetPath
if ([string]$metadata.launcherSha256 -cne $launcherHash -or
    [string]$metadata.payloadManifestSha256 -cne
        (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash) {
    throw "Installer-layout launcher or payload-manifest binding is invalid."
}
$contractPath = Join-Path $repositoryRoot 'packaging\installer\installer-contract.json'
if ([string]$metadata.installerContractSha256 -cne
    (Get-FileHash -LiteralPath $contractPath -Algorithm SHA256).Hash) {
    throw "Installer-layout contract binding is invalid."
}
$toolFilesByName =
    [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::Ordinal)
foreach ($toolFile in Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.ps1') {
    if ($toolFilesByName.ContainsKey($toolFile.Name)) {
        throw "Launcher tooling contains a duplicate ordinal file name."
    }
    $toolFilesByName.Add($toolFile.Name, $toolFile)
}
$toolNames = Get-LauncherOrdinalStrings -Values ([string[]]@($toolFilesByName.Keys))
$toolEntries = @(
    foreach ($toolName in $toolNames) {
        $toolFile = $toolFilesByName[$toolName]
        "$($toolFile.Name)`0$((Get-FileHash -LiteralPath $toolFile.FullName -Algorithm SHA256).Hash)"
    }
)
$toolHasher = [System.Security.Cryptography.SHA256]::Create()
try {
    $actualToolsSha256 = [System.BitConverter]::ToString(
        $toolHasher.ComputeHash(
            [System.Text.Encoding]::UTF8.GetBytes(
                (($toolEntries -join "`n") + "`n")))).Replace('-', '')
}
finally {
    $toolHasher.Dispose()
}
if ([string]$metadata.layoutToolsSha256 -cne $actualToolsSha256) {
    throw "Installer-layout tooling binding is invalid."
}
if ([string]$metadata.launcherBuildMetadataSha256 -cne
    (Get-FileHash `
        -LiteralPath (Join-Path $root 'launcher-build.json') `
        -Algorithm SHA256).Hash) {
    throw "Installer-layout launcher-build metadata binding is invalid."
}

$inventory = @(
    Get-ChildItem -LiteralPath $root -File -Recurse |
        ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($relative -cin @('installer-layout.json', 'SHA256SUMS')) {
                return
            }
            [pscustomobject]@{
                path = $relative
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
)
$inventory = Get-LauncherOrdinalRecords `
    -Records $inventory `
    -PropertyName 'path'
$declared = @($metadata.files)
if ($metadata.files -isnot [System.Array] -or
    $declared.Count -ne $inventory.Count) {
    throw "Installer-layout file inventory count is invalid."
}
for ($index = 0; $index -lt $inventory.Count; $index++) {
    Assert-LauncherExactProperties $declared[$index] @(
        'path',
        'bytes',
        'sha256'
    ) "Installer-layout file inventory entry $index"
    if ($declared[$index].path -isnot [string] -or
        [string]$declared[$index].path -cne [string]$inventory[$index].path -or
        -not ($declared[$index].bytes -is [int64] -or
            $declared[$index].bytes -is [int]) -or
        [int64]$declared[$index].bytes -ne [int64]$inventory[$index].bytes -or
        $declared[$index].sha256 -isnot [string] -or
        [string]$declared[$index].sha256 -cne [string]$inventory[$index].sha256) {
        throw "Installer-layout file inventory is invalid."
    }
}

$expectedChecksumRecords = @(
    Get-ChildItem -LiteralPath $root -File -Recurse |
        ForEach-Object {
            $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($relative -ceq 'SHA256SUMS') {
                return
            }
            [pscustomobject]@{
                Path = $relative
                Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
)
$expectedChecksumRecords = Get-LauncherOrdinalRecords `
    -Records $expectedChecksumRecords `
    -PropertyName 'Path'
$expectedChecksums = @(
    $expectedChecksumRecords |
        ForEach-Object { "$($_.Hash)  $($_.Path)" }
)
$actualChecksumText = [System.IO.File]::ReadAllText(
    (Join-Path $root 'SHA256SUMS'),
    [System.Text.Encoding]::ASCII)
if ($actualChecksumText -cne (($expectedChecksums -join "`n") + "`n")) {
    throw "Installer-layout checksum inventory is invalid."
}

Write-Host "Installer-layout validation passed ($Mode): $root"
