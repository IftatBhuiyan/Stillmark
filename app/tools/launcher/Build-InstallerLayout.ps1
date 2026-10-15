[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LauncherArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LauncherValidation.Common.ps1')

if ($Version -cnotmatch '^[A-Za-z0-9](?:[A-Za-z0-9._+-]{0,126}[A-Za-z0-9])?$' -or
    $Version -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:[.]|$)') {
    throw "Installer-layout version is not a canonical safe version segment."
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$launcherRoot = [System.IO.Path]::GetFullPath($LauncherArtifactDirectory)
$payloadRoot = [System.IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$allowedOutputRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\installer-layout')).TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith(
        $allowedOutputRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Installer-layout output must be below '$allowedOutputRoot'."
}
if (Test-Path -LiteralPath $outputRoot) {
    if ((Get-ChildItem -LiteralPath $outputRoot -Force | Measure-Object).Count -ne 0) {
        throw "Installer-layout output must be empty."
    }
}
else {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

& (Join-Path $PSScriptRoot 'Test-StableLauncher.ps1') `
    -ArtifactDirectory $launcherRoot `
    -Mode Authoring `
    -DotnetPath $DotnetPath
& (Join-Path $repositoryRoot 'tools\release\Test-ReleaseArtifact.ps1') `
    -ArtifactDirectory $payloadRoot `
    -ExpectedVersion $Version `
    -DotnetPath $DotnetPath

$launcherPath = Join-Path $launcherRoot 'WinShot.exe'
$versionRoot = Join-Path $outputRoot ('versions\' + $Version)
New-Item -ItemType Directory -Path $versionRoot | Out-Null
[System.IO.File]::Copy(
    $launcherPath,
    (Join-Path $outputRoot 'WinShot.exe'),
    $false)
[System.IO.File]::Copy(
    (Join-Path $launcherRoot 'launcher-build.json'),
    (Join-Path $outputRoot 'launcher-build.json'),
    $false)
[System.IO.File]::WriteAllBytes(
    (Join-Path $outputRoot 'active-version'),
    [System.Text.Encoding]::ASCII.GetBytes($Version))

$payloadFiles = @(Get-ChildItem -LiteralPath $payloadRoot -File -Recurse)
foreach ($file in $payloadFiles) {
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Installer-layout input payload contains a reparse point."
    }
    $relative = $file.FullName.Substring($payloadRoot.Length + 1)
    $destination = Join-Path $versionRoot $relative
    $destinationDirectory = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationDirectory | Out-Null
    }
    [System.IO.File]::Copy($file.FullName, $destination, $false)
}
& (Join-Path $repositoryRoot 'tools\release\Test-ReleaseArtifact.ps1') `
    -ArtifactDirectory $versionRoot `
    -ExpectedVersion $Version `
    -DotnetPath $DotnetPath

$contractPath = Join-Path $repositoryRoot 'packaging\installer\installer-contract.json'
$payloadManifest = Join-Path $payloadRoot 'release-manifest.json'
$launcherBuildMetadata = Join-Path $launcherRoot 'launcher-build.json'
$launcherHash = (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash
$payloadManifestHash = (
    Get-FileHash -LiteralPath $payloadManifest -Algorithm SHA256
).Hash
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
$toolBytes = [System.Text.Encoding]::UTF8.GetBytes(
    (($toolEntries -join "`n") + "`n"))
$toolHasher = [System.Security.Cryptography.SHA256]::Create()
try {
    $layoutToolsSha256 = [System.BitConverter]::ToString(
        $toolHasher.ComputeHash($toolBytes)).Replace('-', '')
}
finally {
    $toolHasher.Dispose()
}
$inventoryFiles = @(
    Get-ChildItem -LiteralPath $outputRoot -File -Recurse |
        ForEach-Object {
            $relative = $_.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
            [pscustomobject][ordered]@{
                path = $relative
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
)
$inventoryFiles = Get-LauncherOrdinalRecords `
    -Records $inventoryFiles `
    -PropertyName 'path'
$metadata = [ordered]@{
    schemaVersion = 1
    artifactKind = 'UNSIGNED_INSTALLER_LAYOUT_ENGINEERING'
    packageId = 'PROVISIONAL.WinShot'
    version = $Version
    activeVersion = $Version
    launcherSha256 = $launcherHash
    launcherBuildMetadataSha256 = (
        Get-FileHash -LiteralPath $launcherBuildMetadata -Algorithm SHA256
    ).Hash
    payloadManifestSha256 = $payloadManifestHash
    installerContractSha256 = (
        Get-FileHash -LiteralPath $contractPath -Algorithm SHA256
    ).Hash
    layoutToolsSha256 = $layoutToolsSha256
    files = $inventoryFiles
}
[System.IO.File]::WriteAllText(
    (Join-Path $outputRoot 'installer-layout.json'),
    (($metadata | ConvertTo-Json -Depth 8) + "`n"),
    [System.Text.UTF8Encoding]::new($false))

$checksumEntries = @(
    Get-ChildItem -LiteralPath $outputRoot -File -Recurse |
        ForEach-Object {
            $relative = $_.FullName.Substring($outputRoot.Length + 1).Replace('\', '/')
            if ($relative -ceq 'SHA256SUMS') {
                return
            }
            [pscustomobject]@{
                Path = $relative
                Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
)
$checksumEntries = Get-LauncherOrdinalRecords `
    -Records $checksumEntries `
    -PropertyName 'Path'
$checksumText = ($checksumEntries | ForEach-Object {
    "$($_.Hash)  $($_.Path)"
}) -join "`n"
[System.IO.File]::WriteAllText(
    (Join-Path $outputRoot 'SHA256SUMS'),
    $checksumText + "`n",
    [System.Text.UTF8Encoding]::new($false))

& (Join-Path $PSScriptRoot 'Test-InstallerLayout.ps1') `
    -ArtifactDirectory $outputRoot `
    -Mode Authoring `
    -DotnetPath $DotnetPath

Write-Host "Installer-layout engineering artifact built: $outputRoot"
