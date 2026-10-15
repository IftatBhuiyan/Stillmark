[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion,

    [string]$DotnetPath = (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Release.Common.ps1")

Assert-SafeReleaseVersion -Version $ExpectedVersion
$repositoryRoot = Get-WinShotRepositoryRoot
$configuration = Get-ReleaseConfiguration -RepositoryRoot $repositoryRoot
$artifactPath = [System.IO.Path]::GetFullPath($ArtifactDirectory).TrimEnd('\')
if (-not (Test-Path -LiteralPath $artifactPath -PathType Container)) {
    throw "Known-good release artifact '$artifactPath' does not exist."
}

$validatorPath = Join-Path $PSScriptRoot "Test-ReleaseArtifact.ps1"
if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf)) {
    throw "Release artifact validator is missing."
}
$childPowerShell = (Get-Command powershell.exe -CommandType Application -ErrorAction Stop).Source

$tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$tempRootName = "WinShotReleaseArtifactTests-$([Guid]::NewGuid().ToString('N'))"
$tempRoot = [System.IO.Path]::GetFullPath((Join-Path $tempParent $tempRootName))
$tempParentPrefix = $tempParent + '\'
if (-not $tempRoot.StartsWith(
        $tempParentPrefix,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($tempRoot) -cne $tempRootName) {
    throw "Refusing to create an adversarial-test root outside the system temp directory."
}
[void][System.IO.Directory]::CreateDirectory($tempRoot)
$tempRootInfo = Get-Item -LiteralPath $tempRoot -Force
if (($tempRootInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Adversarial-test root must not be a reparse point."
}

function Write-Utf8Text {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Value,
        [System.Text.UTF8Encoding]::new($false))
}

function Read-BoundedJson {
    param([Parameter(Mandatory = $true)][string]$Path)

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -gt [long]$configuration.validationLimits.maximumTextFileBytes) {
        throw "Adversarial fixture JSON exceeds the release text-file limit."
    }
    return [System.IO.File]::ReadAllText(
        $file.FullName,
        [System.Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
}

function Write-BoundedJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 100
    if ([System.Text.Encoding]::UTF8.GetByteCount($json) -gt
        [long]$configuration.validationLimits.maximumTextFileBytes) {
        throw "Adversarial fixture JSON exceeds the release text-file limit."
    }
    Write-Utf8Text -Path $Path -Value ($json + [Environment]::NewLine)
}

function Write-CurrentChecksumInventory {
    param([Parameter(Mandatory = $true)][string]$FixtureRoot)

    $lines = @(
        Get-ReleaseFiles -ArtifactDirectory $FixtureRoot |
            Where-Object { $_.Name -cne "SHA256SUMS" } |
            ForEach-Object {
                $relative = Get-RelativeReleasePath -Root $FixtureRoot -Path $_.FullName
                "$(Get-Sha256Hex -Path $_.FullName)  $relative"
            })
    Write-Utf8Text `
        -Path (Join-Path $FixtureRoot "SHA256SUMS") `
        -Value (($lines -join [Environment]::NewLine) + [Environment]::NewLine)
}

function Rebuild-ManifestFileInventory {
    param([Parameter(Mandatory = $true)][string]$FixtureRoot)

    $manifestPath = Join-Path $FixtureRoot "release-manifest.json"
    $manifest = Read-BoundedJson -Path $manifestPath
    $manifest.files = @(
        Get-ReleaseFiles -ArtifactDirectory $FixtureRoot |
            Where-Object { $_.Name -cnotin @("release-manifest.json", "SHA256SUMS") } |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    path = Get-RelativeReleasePath -Root $FixtureRoot -Path $_.FullName
                    bytes = [long]$_.Length
                    sha256 = Get-Sha256Hex -Path $_.FullName
                }
            })
    Write-BoundedJson -Path $manifestPath -Value $manifest
}

function Invoke-ChildArtifactValidator {
    param([Parameter(Mandatory = $true)][string]$FixtureRoot)

    $arguments = @(
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy", "Bypass",
        "-File", $validatorPath,
        "-ArtifactDirectory", $FixtureRoot,
        "-ExpectedVersion", $ExpectedVersion,
        "-DotnetPath", $DotnetPath)
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = @(& $childPowerShell @arguments 2>&1)
        $exitCode = [int]$LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
    }
}

function Assert-OwnedFixturePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $rootPrefix = $tempRoot.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($fullPath) -cnotmatch
            '^case-[0-9]{2}-[a-z0-9-]+-[0-9a-f]{32}$') {
        throw "Refusing to operate on an unowned adversarial fixture directory."
    }
    return $fullPath
}

function Remove-OwnedFixture {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = Assert-OwnedFixturePath -Path $Path
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function New-FixtureCopy {
    param(
        [Parameter(Mandatory = $true)][int]$Index,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $safeName = ($Name.ToLowerInvariant() -replace '[^a-z0-9-]', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($safeName) -or $safeName.Length -gt 80) {
        throw "Adversarial case name is invalid."
    }
    $leaf = "case-$('{0:D2}' -f $Index)-$safeName-$([Guid]::NewGuid().ToString('N'))"
    $fixtureRoot = Assert-OwnedFixturePath -Path (Join-Path $tempRoot $leaf)
    [void][System.IO.Directory]::CreateDirectory($fixtureRoot)

    foreach ($item in @(Get-ChildItem -LiteralPath $artifactPath -Force)) {
        Copy-Item -LiteralPath $item.FullName -Destination $fixtureRoot -Recurse -Force
    }
    [void](Get-ReleaseFiles -ArtifactDirectory $fixtureRoot)
    return $fixtureRoot
}

function Invoke-NegativeCase {
    param(
        [Parameter(Mandatory = $true)][int]$Index,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Mutation,
        [Parameter(Mandatory = $true)][string]$ExpectedRefusalPattern
    )

    $fixtureRoot = $null
    try {
        $fixtureRoot = New-FixtureCopy -Index $Index -Name $Name
        & $Mutation $fixtureRoot
        $validation = Invoke-ChildArtifactValidator -FixtureRoot $fixtureRoot
        if ($validation.ExitCode -eq 0) {
            return [pscustomobject]@{
                Case = $Name
                Passed = $false
                Detail = "Validator accepted the adversarial artifact."
            }
        }
        if ($validation.Output -notmatch $ExpectedRefusalPattern) {
            $boundedOutput = $validation.Output
            if ($boundedOutput.Length -gt 2048) {
                $boundedOutput = $boundedOutput.Substring(0, 2048)
            }
            return [pscustomobject]@{
                Case = $Name
                Passed = $false
                Detail = "Validator refused for the wrong reason: $boundedOutput"
            }
        }
        return [pscustomobject]@{
            Case = $Name
            Passed = $true
            Detail = "Refused at the intended semantic boundary."
        }
    }
    catch {
        return [pscustomobject]@{
            Case = $Name
            Passed = $false
            Detail = "Harness mutation failed: $($_.Exception.Message)"
        }
    }
    finally {
        if ($null -ne $fixtureRoot) {
            Remove-OwnedFixture -Path $fixtureRoot
        }
    }
}

try {
    # Validate one byte-for-byte fixture copy first. Negative results are not meaningful unless
    # the caller supplied an artifact accepted by the current repository and source provenance.
    $positiveRoot = $null
    try {
        $positiveRoot = New-FixtureCopy -Index 0 -Name "unchanged-positive"
        $positiveValidation = Invoke-ChildArtifactValidator -FixtureRoot $positiveRoot
        if ($positiveValidation.ExitCode -ne 0 -or
            $positiveValidation.Output -notmatch 'PASS_UNSIGNED_TEST') {
            throw "The unchanged artifact fixture was not accepted: $($positiveValidation.Output)"
        }
        Write-Output "PASS: unchanged-positive"
    }
    finally {
        if ($null -ne $positiveRoot) {
            Remove-OwnedFixture -Path $positiveRoot
        }
    }

    $results = [System.Collections.Generic.List[object]]::new()

    [void]$results.Add((Invoke-NegativeCase `
        -Index 1 `
        -Name "reordered-manifest-files" `
        -ExpectedRefusalPattern 'Release manifest inventory does not exactly match' `
        -Mutation {
            param($root)
            $path = Join-Path $root "release-manifest.json"
            $manifest = Read-BoundedJson -Path $path
            [object[]]$entries = @($manifest.files)
            [Array]::Reverse($entries)
            $manifest.files = $entries
            Write-BoundedJson -Path $path -Value $manifest
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 2 `
        -Name "reordered-sha256sums" `
        -ExpectedRefusalPattern 'SHA256SUMS inventory does not exactly match' `
        -Mutation {
            param($root)
            $path = Join-Path $root "SHA256SUMS"
            [string[]]$lines = @(Get-Content -LiteralPath $path)
            [Array]::Reverse($lines)
            Write-Utf8Text `
                -Path $path `
                -Value (($lines -join [Environment]::NewLine) + [Environment]::NewLine)
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 3 `
        -Name "case-colliding-manifest-path" `
        -ExpectedRefusalPattern 'duplicate or case-colliding path' `
        -Mutation {
            param($root)
            $path = Join-Path $root "release-manifest.json"
            $manifest = Read-BoundedJson -Path $path
            $source = @($manifest.files | Where-Object { $_.path -ceq "WinShot.dll" })
            if ($source.Count -ne 1) {
                throw "Known-good manifest does not contain exactly one WinShot.dll entry."
            }
            $duplicate = [pscustomobject][ordered]@{
                path = "winshot.dll"
                bytes = [long]$source[0].bytes
                sha256 = [string]$source[0].sha256
            }
            $manifest.files = @($manifest.files) + $duplicate
            Write-BoundedJson -Path $path -Value $manifest
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 4 `
        -Name "unsafe-traversal-manifest-path" `
        -ExpectedRefusalPattern 'unsafe path segment' `
        -Mutation {
            param($root)
            $path = Join-Path $root "release-manifest.json"
            $manifest = Read-BoundedJson -Path $path
            $source = @($manifest.files | Where-Object { $_.path -ceq "WinShot.dll" })
            if ($source.Count -ne 1) {
                throw "Known-good manifest does not contain exactly one WinShot.dll entry."
            }
            $unsafe = [pscustomobject][ordered]@{
                path = "../WinShot.dll"
                bytes = [long]$source[0].bytes
                sha256 = [string]$source[0].sha256
            }
            $manifest.files = @($manifest.files) + $unsafe
            Write-BoundedJson -Path $path -Value $manifest
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 5 `
        -Name "substituted-screen-recorder" `
        -ExpectedRefusalPattern 'Shipped ScreenRecorderLib\.dll does not match its reviewed source (?:length|SHA-256)' `
        -Mutation {
            param($root)
            Copy-Item `
                -LiteralPath (Join-Path $root "WinShot.Core.dll") `
                -Destination (Join-Path $root "ScreenRecorderLib.dll") `
                -Force
            Rebuild-ManifestFileInventory -FixtureRoot $root
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 6 `
        -Name "deps-injected-arbitrary-culture-satellite" `
        -ExpectedRefusalPattern '(?i)runtime asset must be a top-level payload path|satellite.+(?:unapproved|missing|source)|dependency manifest' `
        -Mutation {
            param($root)
            $sourceSatellite = @(
                Get-ChildItem -LiteralPath $root -Filter "*.resources.dll" -File -Recurse |
                    Where-Object { $_.Directory.FullName -cne $root } |
                    Sort-Object FullName |
                    Select-Object -First 1)
            if ($sourceSatellite.Count -ne 1) {
                throw "Known-good artifact does not contain a satellite resource fixture."
            }

            $culture = "zz"
            $cultureDirectory = Join-Path $root $culture
            [void][System.IO.Directory]::CreateDirectory($cultureDirectory)
            $destination = Join-Path $cultureDirectory $sourceSatellite[0].Name
            Copy-Item -LiteralPath $sourceSatellite[0].FullName -Destination $destination
            $relative = "$culture/$($sourceSatellite[0].Name)"

            $depsPath = Join-Path $root "WinShot.deps.json"
            $deps = Read-BoundedJson -Path $depsPath
            $selectedTargets = @($deps.targets.PSObject.Properties | Where-Object {
                $_.Name -ceq [string]$deps.runtimeTarget.name
            })
            if ($selectedTargets.Count -ne 1) {
                throw "Dependency manifest has no unique selected runtime target."
            }
            $desktopComponents = @($selectedTargets[0].Value.PSObject.Properties |
                Where-Object {
                    $_.Name -like
                        "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/*"
                })
            if ($desktopComponents.Count -ne 1) {
                throw "Dependency manifest has no unique Windows Desktop runtime component."
            }
            $runtimeAssets = $desktopComponents[0].Value.PSObject.Properties["runtime"]
            if (-not $runtimeAssets -or
                $runtimeAssets.Value.PSObject.Properties[$relative]) {
                throw "Dependency manifest runtime assets cannot accept the adversarial fixture."
            }
            $runtimeAssets.Value | Add-Member `
                -MemberType NoteProperty `
                -Name $relative `
                -Value ([pscustomobject]@{})
            Write-BoundedJson -Path $depsPath -Value $deps

            Rebuild-ManifestFileInventory -FixtureRoot $root
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 7 `
        -Name "missing-packaged-startup-bootstrap" `
        -ExpectedRefusalPattern "Required release payload 'WinShot\.Startup\.exe' is missing" `
        -Mutation {
            param($root)
            Remove-Item -LiteralPath (
                Join-Path $root "WinShot.Startup.exe") -Force
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 8 `
        -Name "substituted-packaged-startup-bootstrap" `
        -ExpectedRefusalPattern 'Startup-bootstrap hashes are not exact deterministic evidence' `
        -Mutation {
            param($root)
            Copy-Item `
                -LiteralPath (Join-Path $root "WinShot.exe") `
                -Destination (Join-Path $root "WinShot.Startup.exe") `
                -Force
            Rebuild-ManifestFileInventory -FixtureRoot $root
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 9 `
        -Name "stale-packaged-startup-source-binding" `
        -ExpectedRefusalPattern "Startup-bootstrap input '.+' is stale or changed" `
        -Mutation {
            param($root)
            $path = Join-Path $root "startup-build.json"
            $metadata = Read-BoundedJson -Path $path
            $metadata.inputs[0].sha256 = '0' * 64
            Write-BoundedJson -Path $path -Value $metadata
            Rebuild-ManifestFileInventory -FixtureRoot $root
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 10 `
        -Name "screen-recorder-reference-identity" `
        -ExpectedRefusalPattern "Release dependency libraries must contain exactly one case-exact 'ScreenRecorderLib\.Reference/.+' property" `
        -Mutation {
            param($root)
            $depsPath = Join-Path $root "WinShot.deps.json"
            $deps = Read-BoundedJson -Path $depsPath
            $selectedTargets = @($deps.targets.PSObject.Properties | Where-Object {
                $_.Name -ceq [string]$deps.runtimeTarget.name
            })
            $screenLibraries = @($deps.libraries.PSObject.Properties | Where-Object {
                $_.Name -like "ScreenRecorderLib.Reference/*"
            })
            if ($selectedTargets.Count -ne 1 -or $screenLibraries.Count -ne 1) {
                throw "Dependency manifest has no unique ScreenRecorderLib reference fixture."
            }

            $originalIdentity = $screenLibraries[0].Name
            $replacementIdentity = "ScreenRecorderLib.Reference/0.0.0.0"
            foreach ($container in @($deps.libraries, $selectedTargets[0].Value)) {
                $property = @($container.PSObject.Properties | Where-Object {
                    $_.Name -ceq $originalIdentity
                })
                if ($property.Count -ne 1) {
                    throw "Dependency manifest reference fixture is inconsistent."
                }
                $value = $property[0].Value
                $container.PSObject.Properties.Remove($originalIdentity)
                $container | Add-Member `
                    -MemberType NoteProperty `
                    -Name $replacementIdentity `
                    -Value $value
            }

            Write-BoundedJson -Path $depsPath -Value $deps
            Rebuild-ManifestFileInventory -FixtureRoot $root
            Write-CurrentChecksumInventory -FixtureRoot $root
        }))

    [void]$results.Add((Invoke-NegativeCase `
        -Index 11 `
        -Name "local-developer-entitlement-marker" `
        -ExpectedRefusalPattern 'local-developer entitlement marker' `
        -Mutation {
            param($root)
            $path = Join-Path $root "WinShot.dll"
            $marker = [System.Text.Encoding]::ASCII.GetBytes(
                "WINSHOT_LOCAL_DEVELOPER_USE_ENTITLEMENT_V1")
            $stream = [System.IO.File]::Open(
                $path,
                [System.IO.FileMode]::Append,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            try {
                $stream.Write($marker, 0, $marker.Length)
                $stream.Flush($true)
            }
            finally {
                $stream.Dispose()
            }
        }))

    foreach ($result in $results) {
        $status = if ($result.Passed) { "PASS" } else { "FAIL" }
        Write-Output "$status`: $($result.Case) - $($result.Detail)"
    }

    $failures = @($results | Where-Object { -not $_.Passed })
    if ($failures.Count -ne 0) {
        throw "Release artifact adversarial harness found $($failures.Count) failed case(s): $(
            ($failures.Case -join ', '))"
    }

    Write-Output "PASS_RELEASE_ARTIFACT_ADVERSARIAL_TESTS"
}
finally {
    $resolvedTempRoot = [System.IO.Path]::GetFullPath($tempRoot).TrimEnd('\')
    if (-not $resolvedTempRoot.StartsWith(
            $tempParentPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($resolvedTempRoot) -cne $tempRootName) {
        throw "Refusing to remove an unowned adversarial-test root."
    }
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
