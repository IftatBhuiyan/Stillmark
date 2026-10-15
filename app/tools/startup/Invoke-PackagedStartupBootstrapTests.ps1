[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifactRoot = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$validator = Join-Path $PSScriptRoot 'Test-PackagedStartupBootstrap.ps1'
& $validator `
    -ArtifactDirectory $artifactRoot `
    -Mode Authoring `
    -DotnetPath $DotnetPath

$testRoot = Join-Path (
    Split-Path -Parent $artifactRoot) (
    '.startup-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Copy-Artifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $destination = Join-Path $testRoot $Name
    Copy-Item -LiteralPath $artifactRoot -Destination $destination -Recurse
    return $destination
}

function Assert-Refused {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Mutation,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedPattern
    )

    $caseRoot = Copy-Artifact -Name $Name
    & $Mutation $caseRoot
    $refused = $false
    try {
        & $validator `
            -ArtifactDirectory $caseRoot `
            -Mode Authoring `
            -DotnetPath $DotnetPath
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedPattern) {
            throw "Mutation '$Name' failed at an unexpected boundary: $($_.Exception.Message)"
        }
        $refused = $true
    }
    if (-not $refused) {
        throw "Mutation '$Name' was unexpectedly accepted."
    }
}

try {
    Assert-Refused `
        -Name 'missing-bootstrap' `
        -ExpectedPattern 'missing|item count' `
        -Mutation {
            param($root)
            Remove-Item -LiteralPath (
                Join-Path $root 'WinShot.Startup.exe') -Force
        }
    Assert-Refused `
        -Name 'tampered-bootstrap' `
        -ExpectedPattern 'hashes are not exact' `
        -Mutation {
            param($root)
            Add-Content -LiteralPath (
                Join-Path $root 'WinShot.Startup.exe') -Value 'tamper'
        }
    Assert-Refused `
        -Name 'stale-source-binding' `
        -ExpectedPattern 'stale or changed' `
        -Mutation {
            param($root)
            $metadataPath = Join-Path $root 'startup-build.json'
            $metadata = Get-Content -LiteralPath $metadataPath -Raw |
                ConvertFrom-Json
            $metadata.inputs[0].sha256 = '0' * 64
            [System.IO.File]::WriteAllText(
                $metadataPath,
                ($metadata | ConvertTo-Json -Depth 8) + "`n",
                [System.Text.UTF8Encoding]::new($false))
        }
    Assert-Refused `
        -Name 'extra-file' `
        -ExpectedPattern 'unexpected item count' `
        -Mutation {
            param($root)
            New-Item -ItemType File -Path (
                Join-Path $root 'WinShot.exe') | Out-Null
        }
    Assert-Refused `
        -Name 'noncanonical-checksum' `
        -ExpectedPattern 'checksum file is not exact' `
        -Mutation {
            param($root)
            Add-Content -LiteralPath (
                Join-Path $root 'SHA256SUMS') -Value ''
        }
}
finally {
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $expectedParent = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $artifactRoot)).TrimEnd('\') + '\'
    if (-not $resolvedTestRoot.StartsWith(
            $expectedParent,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean bootstrap tests outside '$expectedParent'."
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

Write-Host "Packaged startup bootstrap adversarial tests passed."
