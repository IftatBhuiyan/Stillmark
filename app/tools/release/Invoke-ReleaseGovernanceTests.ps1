[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$validator = Join-Path $PSScriptRoot "Test-ReleaseGovernance.ps1"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$sourceNotes = Join-Path $repositoryRoot "packaging\release-notes\v1.0.0.md"
$validNotes = (Get-Content -LiteralPath $sourceNotes -Raw -Encoding UTF8).Replace(
    "`r`n",
    "`n")
$utf8 = [System.Text.UTF8Encoding]::new($false)
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$tempRoot = Join-Path $tempBase "WinShotReleaseGovernanceTests-$([Guid]::NewGuid().ToString('N'))"
$notesDirectory = Join-Path $tempRoot "packaging\release-notes"
$notesPath = Join-Path $notesDirectory "v1.0.0.md"
$propsPath = Join-Path $tempRoot "Directory.Build.props"
$appInfoPath = Join-Path $tempRoot "src\WinShot.App\AppInfo.cs"
$created = $false
$refusalCases = 0

function Write-Utf8Fixture {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )
    [System.IO.File]::WriteAllText($Path, $Value, $utf8)
}

function Reset-Fixture {
    Write-Utf8Fixture -Path $propsPath -Value @'
<Project>
  <PropertyGroup>
    <Version>1.0.0</Version>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <FileVersion>1.0.0.0</FileVersion>
    <InformationalVersion>1.0.0</InformationalVersion>
  </PropertyGroup>
</Project>
'@
    Write-Utf8Fixture -Path $appInfoPath -Value @'
namespace WinShot.App;
public static class AppInfo
{
    public const string Version = "1.0.0";
    public const int Major = 1;
}
'@
    Write-Utf8Fixture -Path $notesPath -Value $validNotes
}

function Assert-Refused {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Expected
    )
    try {
        & $Action | Out-Null
    }
    catch {
        if ($_.Exception.Message -notmatch [regex]::Escape($Expected)) {
            throw "Expected refusal containing '$Expected', received '$($_.Exception.Message)'."
        }
        $script:refusalCases++
        return
    }
    throw "Expected release-governance refusal containing '$Expected'."
}

function Write-MutatedNotes {
    param(
        [Parameter(Mandatory = $true)][string]$Search,
        [Parameter(Mandatory = $true)][string]$Replacement
    )
    if (-not $validNotes.Contains($Search)) {
        throw "Release-governance fixture mutation source was not found."
    }
    Write-Utf8Fixture -Path $notesPath -Value $validNotes.Replace($Search, $Replacement)
}

try {
    [System.IO.Directory]::CreateDirectory($notesDirectory) | Out-Null
    [System.IO.Directory]::CreateDirectory(
        (Split-Path -Parent $appInfoPath)) | Out-Null
    $created = $true
    Reset-Fixture

    $positive = & $validator `
        -RepositoryRoot $tempRoot `
        -ReleaseTag "v1.0.0" `
        -ReleaseNotesPath $notesPath
    if ($positive.Result -cne "PASS_RELEASE_GOVERNANCE" -or
        $positive.Version -cne "1.0.0" -or
        $positive.ReleaseTag -cne "v1.0.0" -or
        $positive.Status -cne "ENGINEERING_DRAFT" -or
        $positive.PublicReleaseApproved -ne $false) {
        throw "Valid release-governance fixture returned an unexpected result."
    }

    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.1" `
            -ReleaseNotesPath $notesPath
    } "must exactly match"

    Write-Utf8Fixture -Path $propsPath -Value (
        (Get-Content -LiteralPath $propsPath -Raw).Replace(
            "<Version>1.0.0</Version>",
            "<Version>1.0.1</Version>"))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "AppInfo.Version must exactly match"
    Reset-Fixture

    Write-Utf8Fixture -Path $propsPath -Value (
        (Get-Content -LiteralPath $propsPath -Raw).Replace(
            "<FileVersion>1.0.0.0</FileVersion>",
            "<FileVersion>1.0.1.0</FileVersion>"))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "version fields must be unique"
    Reset-Fixture

    Write-Utf8Fixture -Path $appInfoPath -Value (
        (Get-Content -LiteralPath $appInfoPath -Raw).Replace(
            'Version = "1.0.0"',
            'Version = "1.0.1"'))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "AppInfo.Version must exactly match"
    Reset-Fixture

    Write-Utf8Fixture -Path $appInfoPath -Value (
        (Get-Content -LiteralPath $appInfoPath -Raw).Replace(
            "Major = 1",
            "Major = 2"))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "paid Major must be unique"
    Reset-Fixture

    Write-Utf8Fixture -Path $appInfoPath -Value (
        (Get-Content -LiteralPath $appInfoPath -Raw).Replace(
            'public const string Version = "1.0.0";',
            "public const string Version = `"1.0.0`";`n" +
            "    public const string Version = `"1.0.0`";"))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "Version and paid Major must be unique"
    Reset-Fixture

    $wrongFileName = Join-Path $notesDirectory "v1.0.1.md"
    Write-Utf8Fixture -Path $wrongFileName -Value $validNotes
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $wrongFileName
    } "file name must exactly match"

    Write-MutatedNotes -Search "# WinShot 1.0.0" -Replacement "# WinShot 1.0.1"
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "exact reviewed version"
    Reset-Fixture

    Write-MutatedNotes -Search "## Reliability and privacy" -Replacement "## Security"
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "exact reviewed sections"
    Reset-Fixture

    Write-MutatedNotes -Search "Status: Engineering draft" `
        -Replacement "Status: Production-ready"
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "readiness claims"
    Reset-Fixture

    Write-MutatedNotes -Search "local state." `
        -Replacement "local state and a signed package."
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "signing"
    Reset-Fixture

    Write-MutatedNotes -Search "local state." `
        -Replacement "local state and an unsigned package."
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "signing"
    Reset-Fixture

    Write-MutatedNotes -Search "local state." `
        -Replacement "local state at [this link](https://example.invalid/)."
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "unsafe Markdown"
    Reset-Fixture

    Write-MutatedNotes -Search "local state." `
        -Replacement "local state with TODO release copy."
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "placeholder"
    Reset-Fixture

    $firstBullet =
        "- Capture an area, window, display, or all connected displays through the resident tray workflow."
    Write-MutatedNotes -Search (
        "## Reliability and privacy`n" +
        "- Closing the dashboard keeps the resident tray process available until the user chooses Quit.") `
        -Replacement (
            "## Reliability and privacy`n" +
            $firstBullet)
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "unique"
    Reset-Fixture

    Write-Utf8Fixture -Path $notesPath -Value ($validNotes + ("A" * (33 * 1024)))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "128 through 32768 bytes"
    Reset-Fixture

    [System.IO.File]::WriteAllBytes(
        $notesPath,
        [byte[]]@(0xFF, 0xFE, 0xFD, 0xFC) + [System.Text.Encoding]::UTF8.GetBytes($validNotes))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "not valid bounded UTF-8"
    Reset-Fixture

    [System.IO.File]::WriteAllBytes(
        $notesPath,
        [System.Text.UTF8Encoding]::new($true).GetPreamble() +
        [System.Text.Encoding]::UTF8.GetBytes($validNotes))
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "without a byte-order mark"
    Reset-Fixture

    Write-MutatedNotes -Search "local state." `
        -Replacement ("local" + [char]1 + " state.")
    Assert-Refused {
        & $validator -RepositoryRoot $tempRoot -ReleaseTag "v1.0.0" `
            -ReleaseNotesPath $notesPath
    } "control or invisible character"
    Reset-Fixture

    [pscustomobject]@{
        Result = "PASS_RELEASE_GOVERNANCE_TESTS"
        PositiveCases = 1
        RefusalCases = $refusalCases
        TemporaryDirectoryRemoved = $true
    }
}
finally {
    if ($created) {
        $resolved = [System.IO.Path]::GetFullPath($tempRoot)
        if ([System.IO.Path]::GetDirectoryName($resolved) -cne $tempBase -or
            [System.IO.Path]::GetFileName($resolved) -notmatch
                '^WinShotReleaseGovernanceTests-[0-9a-f]{32}$') {
            throw "Refusing to remove a release-governance test directory outside the GUID-owned scope."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
