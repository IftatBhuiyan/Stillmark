Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-OrdinalSortedStrings {
    param([AllowEmptyCollection()][string[]]$Values)

    $copy = [string[]]@($Values)
    [Array]::Sort($copy, [System.StringComparer]::Ordinal)
    return @($copy)
}

function Get-OrdinalSortedProperties {
    param([Parameter(Mandatory = $true)][object]$Object)

    $names = Get-OrdinalSortedStrings -Values @($Object.PSObject.Properties.Name)
    return @($names | ForEach-Object { $Object.PSObject.Properties[$_] })
}

function Assert-ExactObjectProperties {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $actual = Get-OrdinalSortedStrings -Values @($Object.PSObject.Properties.Name)
    $expectedSorted = Get-OrdinalSortedStrings -Values $Expected
    if (($actual -join "`n") -cne ($expectedSorted -join "`n")) {
        throw "$Context contains missing or unknown properties."
    }
}

function Get-WinShotRepositoryRoot {
    $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
    if (-not (Test-Path -LiteralPath (Join-Path $root "WinShot.sln") -PathType Leaf)) {
        throw "Release tooling must run from a WinShot repository containing WinShot.sln."
    }

    return $root
}

function Assert-SafeReleaseVersion {
    param([Parameter(Mandatory = $true)][string]$Version)

    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
        throw "Release version '$Version' must be a three-part semantic version with an optional prerelease label."
    }
}

function Get-ReleaseConfiguration {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $configPath = Join-Path $RepositoryRoot "packaging\release.json"
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    Assert-ExactObjectProperties -Object $config -Context "Release configuration" -Expected @(
        'schemaVersion',
        'applicationName',
        'packageId',
        'publisher',
        'targetFramework',
        'runtimeIdentifier',
        'validationLimits',
        'appHostFiles',
        'packagedStartupFiles',
        'buildInjectedFiles',
        'metadataFiles',
        'allowedSatelliteCultures',
        'requiredPayloadFiles')
    Assert-ExactObjectProperties -Object $config.validationLimits `
        -Context "Release validation limits" `
        -Expected @(
            'maximumItemCount',
            'maximumFileCount',
            'maximumFileBytes',
            'maximumTextFileBytes',
            'maximumTotalBytes',
            'maximumArchiveBytes',
            'maximumPathCharacters')
    if ($config.schemaVersion -ne 1) {
        throw "Unsupported packaging/release.json schema version '$($config.schemaVersion)'."
    }
    if ($config.applicationName -ne "WinShot" -or
        $config.targetFramework -ne "net10.0-windows10.0.19041.0" -or
        $config.runtimeIdentifier -ne "win-x64") {
        throw "Release configuration must match the reviewed WinShot Windows x64 target."
    }
    $requiredSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($required in @($config.requiredPayloadFiles)) {
        if ([string]::IsNullOrWhiteSpace([string]$required) -or
            [System.IO.Path]::GetFileName([string]$required) -ne [string]$required -or
            -not $requiredSet.Add([string]$required)) {
            throw "Required payload entries must be plain top-level, case-unique file names."
        }
    }
    foreach ($limitName in @(
        'maximumItemCount',
        'maximumFileCount',
        'maximumFileBytes',
        'maximumTextFileBytes',
        'maximumTotalBytes',
        'maximumArchiveBytes',
        'maximumPathCharacters')) {
        $limitProperty = $config.validationLimits.PSObject.Properties[$limitName]
        if (-not $limitProperty -or [long]$limitProperty.Value -lt 1) {
            throw "Release validation limit '$limitName' must be a positive integer."
        }
    }
    if ([long]$config.validationLimits.maximumFileCount -gt
        [long]$config.validationLimits.maximumItemCount) {
        throw "maximumFileCount cannot exceed maximumItemCount."
    }
    if ([long]$config.validationLimits.maximumFileBytes -gt
        [long]$config.validationLimits.maximumTotalBytes) {
        throw "maximumFileBytes cannot exceed maximumTotalBytes."
    }
    if ([long]$config.validationLimits.maximumTextFileBytes -gt
        [long]$config.validationLimits.maximumFileBytes) {
        throw "maximumTextFileBytes cannot exceed maximumFileBytes."
    }
    $classifiedFiles = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($fileListName in @(
        'appHostFiles',
        'packagedStartupFiles',
        'buildInjectedFiles',
        'metadataFiles')) {
        foreach ($entry in @($config.$fileListName)) {
            if ([string]::IsNullOrWhiteSpace([string]$entry) -or
                [System.IO.Path]::GetFileName([string]$entry) -ne [string]$entry -or
                -not $classifiedFiles.Add([string]$entry)) {
                throw "Release configuration file classifications must be disjoint, case-unique top-level names."
            }
        }
    }
    $expectedFileClassifications = [ordered]@{
        appHostFiles = @('WinShot.exe')
        packagedStartupFiles = @('WinShot.Startup.exe')
        buildInjectedFiles = @('ScreenRecorderLib.dll')
        metadataFiles = @(
            'commercial-config.json',
            'component-evidence.json',
            'DOTNET-LICENSE.txt',
            'DOTNET-THIRD-PARTY-NOTICES.txt',
            'WinShot.deps.json',
            'WinShot.runtimeconfig.json',
            'startup-build.json',
            'THIRD-PARTY-NOTICES.md',
            'UNSIGNED-TEST-ARTIFACT.txt',
            'payload-attribution.json',
            'SBOM.cdx.json',
            'release-manifest.json',
            'SHA256SUMS')
    }
    foreach ($classification in $expectedFileClassifications.Keys) {
        $actualValues = Get-OrdinalSortedStrings -Values @($config.$classification)
        $expectedValues = Get-OrdinalSortedStrings `
            -Values @($expectedFileClassifications[$classification])
        if (($actualValues -join "`n") -cne ($expectedValues -join "`n")) {
            throw "Release configuration '$classification' differs from the reviewed exact set."
        }
    }
    $cultureSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($culture in @($config.allowedSatelliteCultures)) {
        if ([string]$culture -notmatch '^[A-Za-z]{2}(?:-[A-Za-z]{2,4})?$' -or
            -not $cultureSet.Add([string]$culture)) {
            throw "Invalid satellite-culture directory '$culture'."
        }
    }

    return $config
}

function Get-ComponentEvidence {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $path = Join-Path $RepositoryRoot "packaging\component-evidence.json"
    $evidence = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Assert-ExactObjectProperties -Object $evidence -Context "Component evidence" -Expected @(
        'schemaVersion',
        'legalReviewApproved',
        'reviewNotice',
        'components')
    if ($evidence.schemaVersion -ne 1 -or
        $evidence.legalReviewApproved -isnot [bool] -or
        [bool]$evidence.legalReviewApproved -or
        [string]$evidence.reviewNotice -cnotmatch
            'This is not legal advice or approval to distribute\.$') {
        throw "Component evidence configuration is missing its reviewed schema boundary."
    }

    $expectedComponents = [ordered]@{
        'Microsoft.NETCore.App.Runtime.win-x64' = '10.0.12'
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' = '10.0.12'
        'Microsoft.Windows.SDK.NET.Ref' = '10.0.19041.57'
        'ScreenRecorderLib' = '6.3.0'
        'Microsoft.NETCore.App.Host.win-x64' = '10.0.12'
        'Velopack' = '1.2.0'
    }
    $expectedSourceKinds = [ordered]@{
        'Microsoft.NETCore.App.Runtime.win-x64' = 'nuget-runtime-pack'
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' = 'nuget-runtime-pack'
        'Microsoft.Windows.SDK.NET.Ref' = 'nuget-runtime-pack'
        'ScreenRecorderLib' = 'nuget-build-injected-native'
        'Microsoft.NETCore.App.Host.win-x64' = 'dotnet-apphost-template'
        'Velopack' = 'nuget-package'
    }
    $bomRefs = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $identities = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($component in @($evidence.components)) {
        $componentProperties = @(
            'bomRef',
            'name',
            'version',
            'sourceKind',
            'purl',
            'supplier',
            'sourceSha256',
            'sourceHashSubject',
            'declaredLicense',
            'licenseUrl',
            'projectUrl')
        if ([string]$component.name -ceq 'ScreenRecorderLib') {
            $componentProperties += @('payloadSha256', 'payloadAuthenticodeStatus')
        }
        Assert-ExactObjectProperties -Object $component `
            -Context "Component evidence entry" `
            -Expected $componentProperties
        Assert-ExactObjectProperties -Object $component.declaredLicense `
            -Context "Component declared-license evidence" `
            -Expected @('kind', 'value', 'evidence')
        $identity = "$([string]$component.name)/$([string]$component.version)"
        $expectedBomRef =
            "pkg:nuget/$([string]$component.name)@$([string]$component.version)"
        if ([string]::IsNullOrWhiteSpace([string]$component.bomRef) -or
            [string]::IsNullOrWhiteSpace([string]$component.name) -or
            [string]$component.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$' -or
            -not $expectedComponents.Contains([string]$component.name) -or
            [string]$expectedComponents[[string]$component.name] -cne
                [string]$component.version -or
            [string]$component.bomRef -cne $expectedBomRef -or
            [string]$component.purl -cne $expectedBomRef -or
            [string]::IsNullOrWhiteSpace([string]$component.supplier) -or
            [string]$component.sourceKind -cne
                [string]$expectedSourceKinds[[string]$component.name] -or
            [string]::IsNullOrWhiteSpace([string]$component.sourceHashSubject) -or
            [string]$component.sourceSha256 -cnotmatch '^[A-F0-9]{64}$' -or
            -not $bomRefs.Add([string]$component.bomRef) -or
            -not $identities.Add($identity)) {
            throw "Component evidence contains an invalid or duplicate identity."
        }
        $projectUri = $null
        if (-not [Uri]::TryCreate(
                [string]$component.projectUrl,
                [UriKind]::Absolute,
                [ref]$projectUri) -or
            $projectUri.Scheme -ne [Uri]::UriSchemeHttps) {
            throw "Component evidence project URLs must use HTTPS."
        }
        $licenseUri = $null
        if (-not [Uri]::TryCreate(
                [string]$component.licenseUrl,
                [UriKind]::Absolute,
                [ref]$licenseUri) -or
            $licenseUri.Scheme -ne [Uri]::UriSchemeHttps) {
            throw "Component evidence license URLs must use HTTPS."
        }
        if ([string]$component.declaredLicense.kind -cnotin @('spdx', 'named') -or
            [string]::IsNullOrWhiteSpace([string]$component.declaredLicense.value) -or
            [string]::IsNullOrWhiteSpace([string]$component.declaredLicense.evidence)) {
            throw "Component evidence must record source-derived declared-license evidence."
        }
        if ([string]$component.name -ceq 'ScreenRecorderLib' -and
            ([string]$component.payloadSha256 -cnotmatch '^[A-F0-9]{64}$' -or
                [string]$component.payloadAuthenticodeStatus -cne 'NotSigned')) {
            throw "ScreenRecorderLib evidence must bind its reviewed payload hash and signature status."
        }
    }
    if ($bomRefs.Count -ne $expectedComponents.Count) {
        throw "Component evidence must cover the six reviewed external source families."
    }

    return $evidence
}

function Get-ProjectReleaseVersion {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $RepositoryRoot "Directory.Build.props") -Raw
    $versionNodes = @($buildProperties.SelectNodes('/Project/PropertyGroup/Version'))
    if ($versionNodes.Count -ne 1) {
        throw "Directory.Build.props must contain exactly one product Version."
    }
    $version = [string]$versionNodes[0].InnerText
    Assert-SafeReleaseVersion -Version $version

    $appInfoPath = Join-Path $RepositoryRoot "src\WinShot.App\AppInfo.cs"
    $appInfo = Get-Content -LiteralPath $appInfoPath -Raw
    $match = [regex]::Match(
        $appInfo,
        'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"\s*;',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or $match.Groups[1].Value -ne $version) {
        throw "AppInfo.Version must exactly match Directory.Build.props Version '$version'."
    }

    return $version
}

function Invoke-ReleaseGit {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = @(& git -C $RepositoryRoot @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Git provenance command failed. Release source identity cannot be established."
    }
    return @($output)
}

function Get-SourceSetDigest {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]]$PathSpecs
    )

    $arguments = @('ls-files', '--cached', '--others', '--exclude-standard', '--') + $PathSpecs
    $relativePaths = Get-OrdinalSortedStrings -Values @(
        Invoke-ReleaseGit -RepositoryRoot $RepositoryRoot -Arguments $arguments)
    $uniquePaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $records = [System.Text.StringBuilder]::new()
    [long]$totalBytes = 0
    $fileCount = 0

    foreach ($relativePath in $relativePaths) {
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains("`r") -or $relativePath.Contains("`n") -or
            $relativePath -match '(^|[\\/])\.\.([\\/]|$)') {
            throw "Git returned an unsafe build-input path."
        }
        $normalized = $relativePath.Replace('\', '/')
        if (-not $uniquePaths.Add($normalized)) {
            continue
        }

        $fullPath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativePath))
        $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\') + '\'
        if (-not $fullPath.StartsWith(
            $repositoryPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Git build input resolves outside the repository."
        }

        $fileCount++
        if ($fileCount -gt 10000) {
            throw "Build-input provenance exceeds the 10,000-file limit."
        }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            [void]$records.Append($normalized).Append("`0MISSING`n")
            continue
        }

        $file = Get-Item -LiteralPath $fullPath
        if ($file.Length -gt 268435456) {
            throw "A build input exceeds the 256 MiB provenance limit."
        }
        if ($totalBytes -gt (1073741824 - [long]$file.Length)) {
            throw "Build-input provenance exceeds the 1 GiB total limit."
        }
        $totalBytes += [long]$file.Length
        $hash = Get-Sha256Hex -Path $fullPath
        [void]$records.Append($normalized)
        [void]$records.Append("`0")
        [void]$records.Append(
            $file.Length.ToString([System.Globalization.CultureInfo]::InvariantCulture))
        [void]$records.Append("`0")
        [void]$records.Append($hash)
        [void]$records.Append("`n")
    }

    $recordBytes = [System.Text.Encoding]::UTF8.GetBytes($records.ToString())
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = [BitConverter]::ToString($sha.ComputeHash($recordBytes)).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }

    return [pscustomobject]@{
        Sha256 = $digest
        FileCount = $fileCount
        TotalBytes = $totalBytes
    }
}

function Get-ReleaseSourceProvenance {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $commitLines = @(
        Invoke-ReleaseGit -RepositoryRoot $RepositoryRoot -Arguments @('rev-parse', 'HEAD'))
    if ($commitLines.Count -ne 1 -or $commitLines[0] -notmatch '^[0-9a-f]{40}$') {
        throw "Git HEAD is not a full commit SHA."
    }

    $buildPathSpecs = @(
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'global.json',
        'NuGet.config',
        'THIRD-PARTY-NOTICES.md',
        'src',
        'packaging',
        'tools/startup',
        'tools/release')
    $packagingPathSpecs = @('packaging', 'tools/startup', 'tools/release')
    $buildDigest = Get-SourceSetDigest -RepositoryRoot $RepositoryRoot -PathSpecs $buildPathSpecs
    $packagingDigest = Get-SourceSetDigest `
        -RepositoryRoot $RepositoryRoot `
        -PathSpecs $packagingPathSpecs

    $workingStatus = @(
        Invoke-ReleaseGit `
            -RepositoryRoot $RepositoryRoot `
            -Arguments @('status', '--porcelain=v1', '--untracked-files=all'))
    $buildStatusArguments = @(
        'status',
        '--porcelain=v1',
        '--untracked-files=all',
        '--') + $buildPathSpecs
    $buildStatus = @(
        Invoke-ReleaseGit `
            -RepositoryRoot $RepositoryRoot `
            -Arguments $buildStatusArguments)

    $globalJson = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw |
        ConvertFrom-Json
    $sdkVersion = [string]$globalJson.sdk.version
    if ($sdkVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Pinned SDK version is missing or malformed."
    }

    return [pscustomobject]@{
        CommitSha = [string]$commitLines[0]
        WorkingTreeDirty = $workingStatus.Count -gt 0
        BuildInputsDirty = $buildStatus.Count -gt 0
        BuildInputsSha256 = $buildDigest.Sha256
        BuildInputFileCount = $buildDigest.FileCount
        PackagingToolsSha256 = $packagingDigest.Sha256
        PackagingToolFileCount = $packagingDigest.FileCount
        PinnedSdkVersion = $sdkVersion
    }
}

function Assert-ReleaseSourceProvenance {
    param(
        [Parameter(Mandatory = $true)][object]$Expected,
        [Parameter(Mandatory = $true)][object]$Actual
    )

    foreach ($propertyName in @(
        'CommitSha',
        'WorkingTreeDirty',
        'BuildInputsDirty',
        'BuildInputsSha256',
        'BuildInputFileCount',
        'PackagingToolsSha256',
        'PackagingToolFileCount',
        'PinnedSdkVersion')) {
        if ([string]$Expected.$propertyName -cne [string]$Actual.$propertyName) {
            throw "Release source provenance changed or does not match at '$propertyName'."
        }
    }
}

function Get-RelativeReleasePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rootUri = [Uri](([System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'))
    $pathUri = [Uri][System.IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('\', '/')
}

function Get-ReleaseFiles {
    param([Parameter(Mandatory = $true)][string]$ArtifactDirectory)

    $artifactPath = [System.IO.Path]::GetFullPath($ArtifactDirectory)
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Container)) {
        throw "Release directory '$artifactPath' does not exist."
    }

    $repositoryRoot = Get-WinShotRepositoryRoot
    $configuration = Get-ReleaseConfiguration -RepositoryRoot $repositoryRoot
    $limits = $configuration.validationLimits
    $itemCount = 0
    $fileCount = 0
    [long]$totalBytes = 0
    $filesByPath = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::Ordinal)

    foreach ($item in (Get-ChildItem -LiteralPath $artifactPath -Force -Recurse)) {
        $itemCount++
        if ($itemCount -gt [int]$limits.maximumItemCount) {
            throw "Release payload exceeds the $($limits.maximumItemCount)-item validation limit."
        }

        $relative = Get-RelativeReleasePath -Root $artifactPath -Path $item.FullName
        if ($relative.Length -gt [int]$limits.maximumPathCharacters) {
            throw "Release path exceeds the $($limits.maximumPathCharacters)-character validation limit."
        }

        $linkTypeProperty = $item.PSObject.Properties['LinkType']
        if ($linkTypeProperty -and
            -not [string]::IsNullOrWhiteSpace([string]$linkTypeProperty.Value)) {
            throw "Release payload '$relative' is a symbolic link or reparse point."
        }
        if ($item.PSIsContainer) {
            continue
        }

        $fileCount++
        if ($fileCount -gt [int]$limits.maximumFileCount) {
            throw "Release payload exceeds the $($limits.maximumFileCount)-file validation limit."
        }
        if ([long]$item.Length -gt [long]$limits.maximumFileBytes) {
            throw "Release file '$relative' exceeds the $($limits.maximumFileBytes)-byte limit."
        }
        if ($totalBytes -gt ([long]$limits.maximumTotalBytes - [long]$item.Length)) {
            throw "Release payload exceeds the $($limits.maximumTotalBytes)-byte total limit."
        }
        $totalBytes += [long]$item.Length
        if ($filesByPath.ContainsKey($relative)) {
            throw "Release payload contains duplicate path '$relative'."
        }
        $filesByPath.Add($relative, [System.IO.FileInfo]$item)
    }

    $sortedPaths = Get-OrdinalSortedStrings -Values @($filesByPath.Keys)
    return @($sortedPaths | ForEach-Object { $filesByPath[$_] })
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    $maximumAttempts = 8
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        $stream = $null
        $sha256 = $null
        try {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            $sha256 = [System.Security.Cryptography.SHA256]::Create()
            $hash = $sha256.ComputeHash($stream)
            return [System.BitConverter]::ToString($hash).Replace("-", "")
        }
        catch [System.IO.IOException] {
            if ($attempt -eq $maximumAttempts) {
                throw
            }

            # OneDrive, antivirus, and indexing services can briefly hold a newly published file
            # without read sharing. Retry for a bounded total of 2.8 seconds; persistent access
            # failures still fail the release rather than silently omitting or changing a hash.
            Start-Sleep -Milliseconds (100 * $attempt)
        }
        finally {
            if ($null -ne $sha256) {
                $sha256.Dispose()
            }
            if ($null -ne $stream) {
                $stream.Dispose()
            }
        }
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 64) {
            throw "'$Path' is too small to be a PE file."
        }

        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' has no DOS/PE header."
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or ($peOffset + 6) -gt $stream.Length) {
            throw "'$Path' has an invalid PE header offset."
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' has no PE signature."
        }

        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-X64Pe {
    param([Parameter(Mandatory = $true)][string]$Path)

    $machine = Get-PeMachine -Path $Path
    if ($machine -ne 0x8664) {
        throw "'$Path' is not an x64 PE file (machine 0x$('{0:X4}' -f $machine))."
    }
}

function Assert-UnsignedReleasePayloadSignatures {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string[]]$RelativePaths
    )

    foreach ($relativePath in $RelativePaths) {
        $path = Join-Path $ArtifactDirectory $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Unsigned signature target '$relativePath' is missing."
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
            throw "Unsigned-test payload '$relativePath' must have exact NotSigned status; actual status is '$($signature.Status)'."
        }
    }
}

function Assert-NoForbiddenReleaseContent {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $configuration = Get-ReleaseConfiguration -RepositoryRoot $RepositoryRoot
    $forbiddenNames = @(
        '(?i)(^|/)WinShot\.KeyGen(?:\.|/|$)',
        '(?i)(^|/)(tests?|TestResults|obj|Debug)(/|$)',
        '(?i)\.(?:pdb|xml|cs|csproj|sln|user|suo|pem|pfx|p12|snk)$',
        '(?i)(?:^|/)packages\.lock\.json$',
        '(?i)(?:^|/)winshot-keys[^/]*\.csv$',
        '(?i)(?:^|/)(?:settings|license|trial)\.(?:json|dat)$',
        '(?i)(?:^|/)(?:History|Clipboard|tmp)(?:/|$)',
        '(?i)(?:^|/)error(?:\.[0-9]+)?\.log$'
    )

    foreach ($file in (Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory)) {
        $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
        if ($file.Name -ne "SHA256SUMS" -and
            $file.Extension -notin @('.exe', '.dll', '.json', '.config', '.txt', '.md')) {
            throw "Release payload '$relative' has an extension outside the customer allowlist."
        }
        foreach ($pattern in $forbiddenNames) {
            if ($relative -match $pattern) {
                throw "Forbidden release payload '$relative' matched '$pattern'."
            }
        }
    }

    $repositoryText = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
    $textFiles = Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory |
        Where-Object { $_.Extension -in @('.json', '.config', '.txt', '.md', '.xml') }
    foreach ($file in $textFiles) {
        if ($file.Length -gt [long]$configuration.validationLimits.maximumTextFileBytes) {
            $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
            throw "Release text file '$relative' exceeds the bounded text-inspection limit."
        }
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text.IndexOf($repositoryText, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
            throw "Release text file '$relative' leaks the repository path."
        }
        if ($text -match '(?i)-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:sk_live|sk-proj|ghp_|github_pat_)[0-9A-Za-z_-]{8,}') {
            $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
            throw "Release text file '$relative' contains a high-signal credential or private-key marker."
        }
    }

    $profileText = [System.IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)).TrimEnd('\')
    $ownedBinaries = Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory |
        Where-Object {
            $_.Name -match '(?i)^WinShot(?:\..+)?\.(?:exe|dll)$' -or
            $_.Name -eq 'ScreenRecorderLib.dll'
        }
    $localDeveloperMarker = 'WINSHOT_LOCAL_DEVELOPER_USE_ENTITLEMENT_V1'
    foreach ($file in $ownedBinaries) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
        $unicode = [System.Text.Encoding]::Unicode.GetString($bytes)
        $unicodeShifted = if ($bytes.Length -gt 1) {
            [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
        }
        else {
            ''
        }
        if ($ascii.Contains($localDeveloperMarker) -or
            $unicode.Contains($localDeveloperMarker) -or
            $unicodeShifted.Contains($localDeveloperMarker)) {
            $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
            throw "Release binary '$relative' contains the local-developer entitlement marker."
        }
        foreach ($sensitivePath in @($repositoryText, $profileText, '\bin\Debug\')) {
            if ($ascii.IndexOf($sensitivePath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $unicode.IndexOf($sensitivePath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
                throw "Release binary '$relative' contains a development or user-profile path."
            }
        }
    }
}

function Assert-ReleasePayloadProvenance {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][object]$Configuration
    )

    $depsPath = Join-Path $ArtifactDirectory 'WinShot.deps.json'
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $targetProperty = @($deps.targets.PSObject.Properties |
        Where-Object {
            [string]::Equals(
                $_.Name,
                [string]$deps.runtimeTarget.name,
                [System.StringComparison]::Ordinal)
        })
    if ($targetProperty.Count -ne 1) {
        throw "Dependency manifest must contain exactly one selected runtime target."
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($library in (Get-OrdinalSortedProperties -Object $targetProperty[0].Value)) {
        foreach ($assetKind in @('runtime', 'native')) {
            $assetProperty = $library.Value.PSObject.Properties[$assetKind]
            if (-not $assetProperty) {
                continue
            }
            foreach ($asset in (Get-OrdinalSortedProperties -Object $assetProperty.Value)) {
                $relative = $asset.Name.Replace('\', '/')
                if ($library.Name.StartsWith('Velopack/', [System.StringComparison]::Ordinal)) {
                    $relative = [System.IO.Path]::GetFileName($relative)
                }
                if ([string]::IsNullOrWhiteSpace($relative) -or
                    $relative.StartsWith('/') -or $relative.Contains(':') -or
                    $relative -match '(^|/)\.\.(/|$)') {
                    throw "Dependency manifest contains an unsafe payload path."
                }
                [void]$expected.Add($relative)
            }
        }
    }

    foreach ($configuredFile in @(
        @($Configuration.appHostFiles) +
        @($Configuration.packagedStartupFiles) +
        @($Configuration.buildInjectedFiles) +
        @($Configuration.metadataFiles))) {
        [void]$expected.Add([string]$configuredFile)
    }

    $actualFiles = Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory
    $actualPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($file in $actualFiles) {
        $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
        [void]$actualPaths.Add($relative)
        if ($expected.Contains($relative)) {
            continue
        }

        $segments = $relative.Split('/')
        if ($segments.Count -ne 2 -or
            $segments[0] -cnotin @($Configuration.allowedSatelliteCultures) -or
            $segments[1] -notmatch '^(.+)\.resources\.dll$') {
            throw "Release payload '$relative' is not attributed to the selected dependency graph or an explicit exception."
        }
        $neutralAssembly = "$($Matches[1]).dll"
        if (-not $actualPaths.Contains($neutralAssembly) -and
            -not (Test-Path -LiteralPath (Join-Path $ArtifactDirectory $neutralAssembly) -PathType Leaf)) {
            throw "Satellite payload '$relative' has no neutral assembly in the package."
        }
    }

    foreach ($relative in $expected) {
        if (-not $actualPaths.Contains($relative)) {
            throw "Dependency-attributed or explicitly required payload '$relative' is missing."
        }
    }

    $actualExecutables = Get-OrdinalSortedStrings -Values @(
        $actualPaths | Where-Object { [System.IO.Path]::GetExtension($_) -eq '.exe' })
    $expectedExecutables = Get-OrdinalSortedStrings -Values @(
        $expected | Where-Object { [System.IO.Path]::GetExtension($_) -eq '.exe' })
    if (($actualExecutables -join "`n") -cne ($expectedExecutables -join "`n")) {
        throw "Executable payload inventory differs from the dependency graph and explicit app-host list."
    }

    return [pscustomobject]@{
        DependencyAttributedFiles = $expected.Count
        SatelliteFiles = $actualFiles.Count - $expected.Count
        Executables = $actualExecutables
    }
}

function Remove-NonCustomerPublishFiles {
    param([Parameter(Mandatory = $true)][string]$ArtifactDirectory)

    $artifactPath = [System.IO.Path]::GetFullPath($ArtifactDirectory).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Container)) {
        throw "Publish output '$artifactPath' does not exist."
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $artifactPath -File -Recurse |
        Where-Object { $_.Extension -in @('.pdb', '.xml') })) {
        $resolved = [System.IO.Path]::GetFullPath($file.FullName)
        if (-not $resolved.StartsWith(
            ($artifactPath + '\'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove publish file outside the new artifact directory."
        }
        Remove-Item -LiteralPath $resolved -Force
    }
}

function Get-PayloadAttributionRecords {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][object]$Configuration,
        [Parameter(Mandatory = $true)][object]$ComponentEvidence
    )

    $deps = Get-Content -LiteralPath (Join-Path $ArtifactDirectory 'WinShot.deps.json') -Raw |
        ConvertFrom-Json
    $target = @($deps.targets.PSObject.Properties |
        Where-Object {
            [string]::Equals(
                $_.Name,
                [string]$deps.runtimeTarget.name,
                [System.StringComparison]::Ordinal)
        })
    if ($target.Count -ne 1) {
        throw "Payload attribution requires exactly one selected dependency target."
    }

    $evidenceByName = @{}
    foreach ($component in @($ComponentEvidence.components)) {
        $evidenceByName[[string]$component.name] = $component
    }
    $appBomRef = "pkg:generic/WinShot@$Version"
    $origins = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)

    foreach ($library in (Get-OrdinalSortedProperties -Object $target[0].Value)) {
        $separator = $library.Name.LastIndexOf('/')
        if ($separator -lt 1) {
            throw "Dependency target contains an invalid library identity."
        }
        $libraryName = $library.Name.Substring(0, $separator)
        $bomRef = $null
        if ($libraryName -eq 'WinShot' -or $libraryName.StartsWith('WinShot.')) {
            $bomRef = $appBomRef
        }
        elseif ($libraryName.StartsWith('runtimepack.')) {
            $componentName = $libraryName.Substring('runtimepack.'.Length)
            if (-not $evidenceByName.ContainsKey($componentName)) {
                throw "Runtime payload library '$libraryName' has no component evidence."
            }
            $bomRef = [string]$evidenceByName[$componentName].bomRef
        }
        elseif ($libraryName -in @('ScreenRecorderLib', 'ScreenRecorderLib.Reference')) {
            $bomRef = [string]$evidenceByName['ScreenRecorderLib'].bomRef
        }
        elseif ($libraryName -eq 'Velopack') {
            $bomRef = [string]$evidenceByName['Velopack'].bomRef
        }
        else {
            throw "Payload-contributing library '$libraryName' has no reviewed origin mapping."
        }

        foreach ($assetKind in @('runtime', 'native')) {
            $assets = $library.Value.PSObject.Properties[$assetKind]
            if (-not $assets) {
                continue
            }
            foreach ($asset in (Get-OrdinalSortedProperties -Object $assets.Value)) {
                $relative = $asset.Name.Replace('\', '/')
                if ($libraryName -eq 'Velopack') {
                    $relative = [System.IO.Path]::GetFileName($relative)
                }
                if ($origins.ContainsKey($relative) -and
                    [string]$origins[$relative].BomRef -cne $bomRef) {
                    throw "Payload path '$relative' has conflicting dependency origins."
                }
                $origins[$relative] = [pscustomobject]@{
                    BomRef = $bomRef
                    SourceBomRef = $bomRef
                    SourceKind = "deps-$assetKind"
                }
            }
        }
    }

    if ($origins.ContainsKey('WinShot.exe')) {
        throw "WinShot.exe dependency origin conflicts with the reviewed apphost origin."
    }
    $origins['WinShot.exe'] = [pscustomobject]@{
        BomRef = $appBomRef
        SourceBomRef = [string]$evidenceByName['Microsoft.NETCore.App.Host.win-x64'].bomRef
        SourceKind = 'sdk-patched-apphost'
    }
    if ($origins.ContainsKey('WinShot.Startup.exe')) {
        throw "WinShot.Startup.exe dependency origin conflicts with the reviewed bootstrap origin."
    }
    $origins['WinShot.Startup.exe'] = [pscustomobject]@{
        BomRef = $appBomRef
        SourceBomRef = $appBomRef
        SourceKind = 'repository-source-inbox-framework-csc'
    }
    if ($origins.ContainsKey('ScreenRecorderLib.dll') -and
        [string]$origins['ScreenRecorderLib.dll'].BomRef -cne
            [string]$evidenceByName['ScreenRecorderLib'].bomRef) {
        throw "ScreenRecorderLib.dll dependency origin conflicts with the reviewed build-injected origin."
    }
    $origins['ScreenRecorderLib.dll'] = [pscustomobject]@{
        BomRef = [string]$evidenceByName['ScreenRecorderLib'].bomRef
        SourceBomRef = [string]$evidenceByName['ScreenRecorderLib'].bomRef
        SourceKind = 'nuget-build-injected-native'
    }

    $actualFiles = Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory
    $actualByPath = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($file in $actualFiles) {
        $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
        $actualByPath[$relative] = $file
        if ($origins.ContainsKey($relative) -or
            $relative -cin @($Configuration.metadataFiles)) {
            continue
        }

        $segments = $relative.Split('/')
        if ($segments.Count -eq 2 -and
            $segments[0] -cin @($Configuration.allowedSatelliteCultures) -and
            $segments[1] -match '^(.+)\.resources\.dll$') {
            $neutral = "$($Matches[1]).dll"
            if (-not $origins.ContainsKey($neutral) -or
                [string]$origins[$neutral].BomRef -cne
                    [string]$evidenceByName['Microsoft.WindowsDesktop.App.Runtime.win-x64'].bomRef) {
                throw "Satellite payload '$relative' has no WindowsDesktop neutral origin."
            }
            $origins[$relative] = [pscustomobject]@{
                BomRef = [string]$evidenceByName['Microsoft.WindowsDesktop.App.Runtime.win-x64'].bomRef
                SourceBomRef =
                    [string]$evidenceByName['Microsoft.WindowsDesktop.App.Runtime.win-x64'].bomRef
                SourceKind = 'runtime-pack-satellite'
            }
            continue
        }

        throw "Payload file '$relative' has no exact component attribution."
    }

    $records = @()
    foreach ($relative in (Get-OrdinalSortedStrings -Values @($origins.Keys))) {
        if (-not $actualByPath.ContainsKey($relative)) {
            throw "Attributed payload file '$relative' is missing."
        }
        $file = $actualByPath[$relative]
        $records += [ordered]@{
            path = $relative
            bytes = [long]$file.Length
            sha256 = Get-Sha256Hex -Path $file.FullName
            bomRef = [string]$origins[$relative].BomRef
            sourceBomRef = [string]$origins[$relative].SourceBomRef
            sourceKind = [string]$origins[$relative].SourceKind
        }
    }
    return @($records)
}

function Get-PayloadAttributionDocument {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][object]$Configuration,
        [Parameter(Mandatory = $true)][object]$ComponentEvidence
    )

    $document = [ordered]@{
        schemaVersion = 1
        application = 'WinShot'
        version = $Version
        legalReviewApproved = [bool]$ComponentEvidence.legalReviewApproved
        reviewNotice = [string]$ComponentEvidence.reviewNotice
        files = @(Get-PayloadAttributionRecords `
            -ArtifactDirectory $ArtifactDirectory `
            -Version $Version `
            -Configuration $Configuration `
            -ComponentEvidence $ComponentEvidence)
    }
    return $document
}

function Write-PayloadAttribution {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][object]$Configuration,
        [Parameter(Mandatory = $true)][object]$ComponentEvidence
    )

    $document = Get-PayloadAttributionDocument `
        -ArtifactDirectory $ArtifactDirectory `
        -Version $Version `
        -Configuration $Configuration `
        -ComponentEvidence $ComponentEvidence
    $path = Join-Path $ArtifactDirectory 'payload-attribution.json'
    [System.IO.File]::WriteAllText(
        $path,
        (($document | ConvertTo-Json -Depth 8) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-ReleaseManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$RuntimeIdentifier,
        [Parameter(Mandatory = $true)][object]$SourceProvenance,
        [Parameter(Mandatory = $true)][object]$CommercialConfiguration
    )

    $manifestPath = Join-Path $ArtifactDirectory "release-manifest.json"
    $files = @()
    foreach ($file in (Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory |
        Where-Object { $_.Name -notin @('release-manifest.json', 'SHA256SUMS') })) {
        $files += [ordered]@{
            path = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
            bytes = $file.Length
            sha256 = Get-Sha256Hex -Path $file.FullName
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        application = "WinShot"
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        packageKind = "UNSIGNED_TEST"
        selfContained = $true
        trimmed = $false
        singleFile = $false
        sourceProvenance = [ordered]@{
            commitSha = $SourceProvenance.CommitSha
            workingTreeDirty = $SourceProvenance.WorkingTreeDirty
            buildInputsDirty = $SourceProvenance.BuildInputsDirty
            buildInputsSha256 = $SourceProvenance.BuildInputsSha256
            buildInputFileCount = $SourceProvenance.BuildInputFileCount
            packagingToolsSha256 = $SourceProvenance.PackagingToolsSha256
            packagingToolFileCount = $SourceProvenance.PackagingToolFileCount
            pinnedSdkVersion = $SourceProvenance.PinnedSdkVersion
        }
        commercialConfiguration = [ordered]@{
            sha256 = [string]$CommercialConfiguration.ConfigurationSha256
            contractRevision = [int]$CommercialConfiguration.ContractRevision
            applicationMajor = [int]$CommercialConfiguration.ApplicationMajor
            licenseMode = [string]$CommercialConfiguration.LicenseMode
            provider = $CommercialConfiguration.Provider
            checkoutApproved = [bool]$CommercialConfiguration.CheckoutApproved
            buyerTermsApproved = [bool]$CommercialConfiguration.BuyerTermsApproved
            primaryVariantId = $CommercialConfiguration.PrimaryVariantId
        }
        files = $files
    }
    $json = $manifest | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText(
        $manifestPath,
        ($json + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
}

function Get-DependencyInventoryDocument {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][object]$ComponentEvidence
    )

    $attribution = Get-Content `
        -LiteralPath (Join-Path $ArtifactDirectory 'payload-attribution.json') `
        -Raw |
        ConvertFrom-Json
    $components = @()
    $dependencies = @()
    $componentRefs = @()
    $appHostAncestor = $null
    $componentsByRef = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($component in @($ComponentEvidence.components)) {
        $componentsByRef.Add([string]$component.bomRef, $component)
    }
    foreach ($componentRef in (Get-OrdinalSortedStrings -Values @($componentsByRef.Keys))) {
        $component = $componentsByRef[$componentRef]
        $bomRef = [string]$component.bomRef
        $occurrences = @($attribution.files |
            Where-Object { [string]$_.bomRef -ceq $bomRef } |
            ForEach-Object {
                [ordered]@{ location = [string]$_.path }
            })
        $isAppHost =
            [string]$component.name -ceq 'Microsoft.NETCore.App.Host.win-x64'
        $derivedOccurrences = @($attribution.files |
            Where-Object { [string]$_.sourceBomRef -ceq $bomRef })
        if (-not $isAppHost -and $occurrences.Count -lt 1) {
            throw "External component '$bomRef' has no attributed payload occurrence."
        }
        if ($isAppHost -and
            ($occurrences.Count -ne 0 -or
                $derivedOccurrences.Count -ne 1 -or
                [string]$derivedOccurrences[0].path -cne 'WinShot.exe')) {
            throw "Apphost evidence must describe one SDK-patched WinShot.exe derivative, not a direct occurrence."
        }

        $license = if ([string]$component.declaredLicense.kind -ceq 'spdx') {
            [ordered]@{
                license = [ordered]@{
                    id = [string]$component.declaredLicense.value
                    url = [string]$component.licenseUrl
                    acknowledgement = 'declared'
                }
            }
        }
        else {
            [ordered]@{
                license = [ordered]@{
                    name = [string]$component.declaredLicense.value
                    url = [string]$component.licenseUrl
                    acknowledgement = 'declared'
                }
            }
        }
        $properties = @(
            [ordered]@{
                name = 'winshot:sourceKind'
                value = [string]$component.sourceKind
            },
            [ordered]@{
                name = 'winshot:sourceHashSubject'
                value = [string]$component.sourceHashSubject
            },
            [ordered]@{
                name = 'winshot:declaredLicenseEvidence'
                value = [string]$component.declaredLicense.evidence
            },
            [ordered]@{
                name = 'winshot:legalReviewApproved'
                value = ([bool]$ComponentEvidence.legalReviewApproved).ToString().ToLowerInvariant()
            }
        )
        if ([string]$component.name -ceq 'ScreenRecorderLib') {
            $properties += @(
                [ordered]@{
                    name = 'winshot:payloadSha256'
                    value = [string]$component.payloadSha256
                },
                [ordered]@{
                    name = 'winshot:payloadAuthenticodeStatus'
                    value = [string]$component.payloadAuthenticodeStatus
                }
            )
        }
        $componentDocument = [ordered]@{
            type = if ([string]$component.name -eq 'ScreenRecorderLib') {
                'library'
            }
            else {
                'framework'
            }
            'bom-ref' = $bomRef
            supplier = [ordered]@{
                name = [string]$component.supplier
            }
            name = [string]$component.name
            version = [string]$component.version
            hashes = @(
                [ordered]@{
                    alg = 'SHA-256'
                    content = [string]$component.sourceSha256
                }
            )
            licenses = @($license)
            purl = [string]$component.purl
            externalReferences = @(
                [ordered]@{
                    type = 'website'
                    url = [string]$component.projectUrl
                },
                [ordered]@{
                    type = 'license'
                    url = [string]$component.licenseUrl
                }
            )
            evidence = [ordered]@{
                occurrences = $occurrences
            }
            properties = $properties
        }
        if ($isAppHost) {
            $componentDocument.Remove('evidence')
            $appHostAncestor = $componentDocument
        }
        else {
            $components += $componentDocument
            $componentRefs += $bomRef
            $dependencies += [ordered]@{
                ref = $bomRef
                dependsOn = @()
            }
        }
    }
    if ($null -eq $appHostAncestor) {
        throw "Dependency inventory is missing the reviewed apphost ancestor."
    }

    $applicationRef = "pkg:generic/WinShot@$Version"
    $sbom = [ordered]@{
        '$schema' = "https://cyclonedx.org/schema/bom-1.7.schema.json"
        bomFormat = "CycloneDX"
        specVersion = "1.7"
        version = 1
        metadata = [ordered]@{
            component = [ordered]@{
                type = "application"
                'bom-ref' = $applicationRef
                name = "WinShot"
                version = $Version
                pedigree = [ordered]@{
                    ancestors = @($appHostAncestor)
                }
            }
            properties = @(
                [ordered]@{
                    name = "winshot:scope"
                    value = "Exact external payload occurrences with source-derived declared-license evidence; legal review remains required."
                },
                [ordered]@{
                    name = "winshot:legalReviewApproved"
                    value = ([bool]$ComponentEvidence.legalReviewApproved).ToString().ToLowerInvariant()
                }
            )
        }
        components = $components
        dependencies = @(
            [ordered]@{
                ref = $applicationRef
                dependsOn = @($componentRefs)
            }
        ) + $dependencies
    }

    return $sbom
}

function Write-DependencyInventory {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][object]$ComponentEvidence
    )

    $sbom = Get-DependencyInventoryDocument `
        -ArtifactDirectory $ArtifactDirectory `
        -Version $Version `
        -ComponentEvidence $ComponentEvidence
    $path = Join-Path $ArtifactDirectory "SBOM.cdx.json"
    $json = $sbom | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText(
        $path,
        ($json + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
}

function Write-ChecksumFile {
    param([Parameter(Mandatory = $true)][string]$ArtifactDirectory)

    $lines = @()
    foreach ($file in (Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory |
        Where-Object { $_.Name -ne 'SHA256SUMS' })) {
        $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
        $lines += "$(Get-Sha256Hex -Path $file.FullName)  $relative"
    }

    $path = Join-Path $ArtifactDirectory "SHA256SUMS"
    [System.IO.File]::WriteAllText(
        $path,
        (($lines -join "`n") + "`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false,
            [System.Text.UTF8Encoding]::new($false))
        try {
            $fixedTimestamp = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Unspecified)
            foreach ($file in (Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory)) {
                $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
                $entry = $archive.CreateEntry(
                    $relative,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp
                $input = [System.IO.File]::OpenRead($file.FullName)
                try {
                    $output = $entry.Open()
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ReleaseZipMatchesDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expected = @{}
    foreach ($file in (Get-ReleaseFiles -ArtifactDirectory $ArtifactDirectory)) {
        $relative = Get-RelativeReleasePath -Root $ArtifactDirectory -Path $file.FullName
        $expected[$relative] = Get-Sha256Hex -Path $file.FullName
    }

    $stream = [System.IO.File]::OpenRead($ArchivePath)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false,
            [System.Text.UTF8Encoding]::new($false))
        try {
            $names = @($archive.Entries | ForEach-Object { $_.FullName })
            $sortedNames = Get-OrdinalSortedStrings -Values $names
            if (($sortedNames -join "`n") -cne ($names -join "`n")) {
                throw "Release archive entries are not deterministically sorted."
            }
            if ($names.Count -ne $expected.Count) {
                throw "Release archive entry count does not match the validated directory."
            }

            $fixedTimestamp = [DateTime]::new(2000, 1, 1, 0, 0, 0, [DateTimeKind]::Unspecified)
            foreach ($entry in $archive.Entries) {
                if (-not $expected.ContainsKey($entry.FullName)) {
                    throw "Unexpected archive entry '$($entry.FullName)'."
                }
                if ($entry.FullName.Contains('\') -or $entry.FullName.StartsWith('/') -or
                    $entry.FullName -match '(^|/)\.\.(/|$)') {
                    throw "Unsafe archive entry '$($entry.FullName)'."
                }
                if ($entry.LastWriteTime.DateTime -ne $fixedTimestamp) {
                    throw "Archive entry '$($entry.FullName)' has a non-normalized timestamp."
                }

                $entryStream = $entry.Open()
                try {
                    $sha = [System.Security.Cryptography.SHA256]::Create()
                    try {
                        $actualHash = [BitConverter]::ToString(
                            $sha.ComputeHash($entryStream)).Replace('-', '')
                    }
                    finally {
                        $sha.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }

                if ($actualHash -ne $expected[$entry.FullName]) {
                    throw "Archive hash mismatch for '$($entry.FullName)'."
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}
