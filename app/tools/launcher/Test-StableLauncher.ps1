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

$artifactRoot = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$launcherPath = Join-Path $artifactRoot 'WinShot.exe'
$checksumsPath = Join-Path $artifactRoot 'SHA256SUMS'
$expectedNames = @('WinShot.exe', 'launcher-build.json', 'SHA256SUMS')
$actualNames = @(
    Get-ChildItem -LiteralPath $artifactRoot -Force |
        Sort-Object -Property Name |
        ForEach-Object Name
)
if ($actualNames.Count -ne $expectedNames.Count) {
    throw "Stable-launcher artifact has an unexpected item count."
}
foreach ($name in $expectedNames) {
    if ($actualNames -cnotcontains $name) {
        throw "Stable-launcher artifact is missing '$name'."
    }
}
$evidence = Assert-StableLauncherBuildEvidence `
    -ArtifactRoot $artifactRoot `
    -RepositoryRoot $repositoryRoot `
    -Mode $Mode `
    -DotnetPath $DotnetPath
$actualHash = $evidence.LauncherSha256
$checksumText = [System.IO.File]::ReadAllText(
    $checksumsPath,
    [System.Text.Encoding]::ASCII)
if ($checksumText -cne "$actualHash  WinShot.exe`n") {
    throw "Stable-launcher checksum file is not exact canonical content."
}

$probe = Start-Process `
    -FilePath $launcherPath `
    -ArgumentList '--tray' `
    -WindowStyle Hidden `
    -PassThru `
    -Wait
if ($probe.ExitCode -eq 0) {
    throw "Out-of-layout launcher probe unexpectedly reported success."
}

Write-Host "Stable-launcher validation passed ($Mode): $artifactRoot"
