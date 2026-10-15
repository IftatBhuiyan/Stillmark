[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'Test-StableLauncher.ps1') `
    -ArtifactDirectory $ArtifactDirectory `
    -Mode Authoring `
    -DotnetPath $DotnetPath

$sourceRoot = [System.IO.Path]::GetFullPath($ArtifactDirectory)
$mutationRoot = Join-Path (
    Split-Path -Parent $sourceRoot
) ('.mutations-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $mutationRoot | Out-Null

function Invoke-ExpectedRefusal {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Mutate,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedPattern
    )

    $caseRoot = Join-Path $mutationRoot $Name
    [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Force) {
        [System.IO.File]::Copy(
            $item.FullName,
            (Join-Path $caseRoot $item.Name),
            $false)
    }
    & $Mutate $caseRoot

    $message = $null
    try {
        & (Join-Path $PSScriptRoot 'Test-StableLauncher.ps1') `
            -ArtifactDirectory $caseRoot `
            -Mode Authoring `
            -DotnetPath $DotnetPath
    }
    catch {
        $message = $_.Exception.Message
    }
    if ($null -eq $message -or $message -cnotmatch $ExpectedPattern) {
        throw "Mutation '$Name' did not refuse at '$ExpectedPattern'. Actual: '$message'"
    }
    Write-Host "Expected refusal passed: $Name"
}

try {
    Invoke-ExpectedRefusal `
        -Name 'hash-mismatch' `
        -ExpectedPattern 'hash does not match' `
        -Mutate {
            param($root)
            $path = Join-Path $root 'WinShot.exe'
            $stream = [System.IO.File]::Open(
                $path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            try {
                $stream.Position = $stream.Length - 1
                $value = $stream.ReadByte()
                $stream.Position = $stream.Length - 1
                $stream.WriteByte($value -bxor 1)
                $stream.Flush()
            }
            finally {
                $stream.Dispose()
            }
        }

    Invoke-ExpectedRefusal `
        -Name 'extra-file' `
        -ExpectedPattern 'unexpected item count' `
        -Mutate {
            param($root)
            [System.IO.File]::WriteAllText(
                (Join-Path $root 'unexpected.dll'),
                'forbidden')
        }

    Invoke-ExpectedRefusal `
        -Name 'metadata-kind' `
        -ExpectedPattern 'metadata is invalid' `
        -Mutate {
            param($root)
            $path = Join-Path $root 'launcher-build.json'
            $json = [System.IO.File]::ReadAllText($path) |
                ConvertFrom-Json
            $json.artifactKind = 'SIGNED_PRODUCTION'
            [System.IO.File]::WriteAllText(
                $path,
                (($json | ConvertTo-Json -Depth 8) + "`n"),
                [System.Text.UTF8Encoding]::new($false))
        }

    Invoke-ExpectedRefusal `
        -Name 'non-integer-schema-version' `
        -ExpectedPattern 'metadata is invalid' `
        -Mutate {
            param($root)
            $path = Join-Path $root 'launcher-build.json'
            $json = [System.IO.File]::ReadAllText($path) |
                ConvertFrom-Json
            $json.schemaVersion = 1.5
            [System.IO.File]::WriteAllText(
                $path,
                (($json | ConvertTo-Json -Depth 8) + "`n"),
                [System.Text.UTF8Encoding]::new($false))
        }
}
finally {
    $resolvedMutationRoot = [System.IO.Path]::GetFullPath($mutationRoot)
    $expectedParent = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $sourceRoot)).TrimEnd('\') + '\'
    if (-not $resolvedMutationRoot.StartsWith(
            $expectedParent,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean mutation root outside the launcher artifact parent."
    }
    if (Test-Path -LiteralPath $resolvedMutationRoot) {
        Remove-Item -LiteralPath $resolvedMutationRoot -Recurse -Force
    }
}

Write-Host 'Stable-launcher adversarial tests passed.'
