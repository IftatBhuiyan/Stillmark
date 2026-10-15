function Assert-ReleasePayloadSourceBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ArtifactDirectory,

        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$DotnetPath,

        [Parameter(Mandatory = $true)]
        [object]$ComponentEvidence
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = "Stop"

    $maximumJsonBytes = 67108864
    $maximumBoundFileBytes = 268435456
    $maximumArtifactItems = 4096
    $maximumArtifactBytes = 536870912
    $maximumPathCharacters = 512

    function Test-SourceBindingLink {
        param([Parameter(Mandatory = $true)][object]$Item)

        $linkType = $Item.PSObject.Properties['LinkType']
        if ($linkType -and
            -not [string]::IsNullOrWhiteSpace([string]$linkType.Value)) {
            return $true
        }
        $target = $Item.PSObject.Properties['Target']
        if (-not $target -or $null -eq $target.Value) {
            return $false
        }
        return @($target.Value | Where-Object {
                $null -ne $_ -and
                -not [string]::IsNullOrWhiteSpace([string]$_)
            }).Count -gt 0
    }

    function Get-SourceBindingProperty {
        param(
            [Parameter(Mandatory = $true)][object]$Object,
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][string]$Context
        )

        $propertyCandidates = @($Object.PSObject.Properties | Where-Object {
            [string]::Equals(
                [string]$_.Name,
                $Name,
                [System.StringComparison]::Ordinal)
        })
        if ($propertyCandidates.Count -ne 1) {
            throw "$Context must contain exactly one case-exact '$Name' property."
        }

        return $propertyCandidates[0].Value
    }

    function Read-SourceBindingJson {
        param(
            [Parameter(Mandatory = $true)][string]$Path,
            [Parameter(Mandatory = $true)][string]$Description,
            [long]$MaximumBytes = $maximumJsonBytes
        )

        $file = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if (-not ($file -is [System.IO.FileInfo]) -or
            (Test-SourceBindingLink -Item $file)) {
            throw "$Description must be a regular, non-reparse file."
        }
        if ($file.Length -lt 2 -or $file.Length -gt $MaximumBytes) {
            throw "$Description is outside its bounded JSON size."
        }

        try {
            return Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop |
                ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "$Description is not valid JSON."
        }
    }

    function Assert-SourceBindingRoot {
        param(
            [Parameter(Mandatory = $true)][string]$Path,
            [Parameter(Mandatory = $true)][string]$Description
        )

        $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        if (-not ($item -is [System.IO.DirectoryInfo]) -or
            (Test-SourceBindingLink -Item $item)) {
            throw "$Description must be a regular, non-reparse directory."
        }

        return $fullPath
    }

    function Assert-SourceBindingRelativePath {
        param(
            [Parameter(Mandatory = $true)][string]$RelativePath,
            [Parameter(Mandatory = $true)][string]$Description,
            [switch]$TopLevelOnly
        )

        if ([string]::IsNullOrWhiteSpace($RelativePath) -or
            $RelativePath.Length -gt $maximumPathCharacters -or
            [System.IO.Path]::IsPathRooted($RelativePath) -or
            $RelativePath.Contains('\') -or
            $RelativePath.Contains(':') -or
            $RelativePath.Contains("`0") -or
            $RelativePath.StartsWith('/') -or
            $RelativePath.EndsWith('/')) {
            throw "$Description contains an unsafe or noncanonical relative path."
        }

        $segments = @($RelativePath.Split('/'))
        if ($TopLevelOnly -and $segments.Count -ne 1) {
            throw "$Description must be a top-level payload path."
        }
        foreach ($segment in $segments) {
            $deviceStem = [System.IO.Path]::GetFileNameWithoutExtension($segment).
                TrimEnd([char[]]@(' ', '.'))
            if ([string]::IsNullOrWhiteSpace($segment) -or
                $segment -eq '.' -or $segment -eq '..' -or
                $segment.EndsWith(' ') -or $segment.EndsWith('.') -or
                $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                $deviceStem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
                throw "$Description contains an unsafe path segment."
            }
        }
    }

    function Resolve-SourceBindingPath {
        param(
            [Parameter(Mandatory = $true)][string]$Root,
            [Parameter(Mandatory = $true)][string]$RelativePath,
            [Parameter(Mandatory = $true)][string]$Description,
            [ValidateSet("Leaf", "Container")]
            [string]$PathType = "Leaf",
            [switch]$TopLevelOnly
        )

        Assert-SourceBindingRelativePath `
            -RelativePath $RelativePath `
            -Description $Description `
            -TopLevelOnly:$TopLevelOnly

        $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
        $rootPrefix = $rootPath + '\'
        $resolved = [System.IO.Path]::GetFullPath(
            (Join-Path $rootPath $RelativePath.Replace('/', '\')))
        if (-not $resolved.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Description resolves outside its owned root."
        }

        $current = $rootPath
        foreach ($segment in $RelativePath.Split('/')) {
            $current = Join-Path $current $segment
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (Test-SourceBindingLink -Item $item) {
                throw "$Description traverses a reparse point."
            }
        }

        $final = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
        if ($PathType -eq "Leaf" -and -not ($final -is [System.IO.FileInfo])) {
            throw "$Description must resolve to a regular file."
        }
        if ($PathType -eq "Container" -and -not ($final -is [System.IO.DirectoryInfo])) {
            throw "$Description must resolve to a regular directory."
        }

        return $final
    }

    function Get-SourceBindingHash {
        param(
            [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
            [Parameter(Mandatory = $true)][string]$Description
        )

        if ($File.Length -gt $maximumBoundFileBytes) {
            throw "$Description exceeds the bounded source-binding file size."
        }

        $stream = [System.IO.File]::Open(
            $File.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            if ($stream.Length -ne $File.Length) {
                throw "$Description changed before hashing."
            }
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                return [System.BitConverter]::ToString(
                    $sha.ComputeHash($stream)).Replace('-', '')
            }
            finally {
                $sha.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }

    function Get-SourceBindingSha512Base64 {
        param(
            [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
            [Parameter(Mandatory = $true)][string]$Description
        )

        if ($File.Length -gt $maximumBoundFileBytes) {
            throw "$Description exceeds the bounded source-binding file size."
        }

        $stream = [System.IO.File]::Open(
            $File.FullName,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            if ($stream.Length -ne $File.Length) {
                throw "$Description changed before hashing."
            }
            $sha = [System.Security.Cryptography.SHA512]::Create()
            try {
                return [Convert]::ToBase64String($sha.ComputeHash($stream))
            }
            finally {
                $sha.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }

    function Assert-SourceBindingFilesEqual {
        param(
            [Parameter(Mandatory = $true)][System.IO.FileInfo]$ArtifactFile,
            [Parameter(Mandatory = $true)][System.IO.FileInfo]$SourceFile,
            [Parameter(Mandatory = $true)][string]$Description
        )

        if ($ArtifactFile.Length -ne $SourceFile.Length) {
            throw "$Description does not match its reviewed source length."
        }
        $artifactHash = Get-SourceBindingHash `
            -File $ArtifactFile `
            -Description "$Description payload"
        $sourceHash = Get-SourceBindingHash `
            -File $SourceFile `
            -Description "$Description source"
        if ($artifactHash -cne $sourceHash) {
            throw "$Description does not match its reviewed source SHA-256."
        }
    }

    function Get-SourceBindingPackageRoot {
        param(
            [Parameter(Mandatory = $true)][object]$ProjectAssets,
            [Parameter(Mandatory = $true)][string]$PackageRelativePath,
            [Parameter(Mandatory = $true)][string]$Description
        )

        Assert-SourceBindingRelativePath `
            -RelativePath $PackageRelativePath `
            -Description "$Description package path"

        $packageFolders = @($ProjectAssets.packageFolders.PSObject.Properties |
            ForEach-Object { [System.IO.Path]::GetFullPath([string]$_.Name).TrimEnd('\') } |
            Sort-Object -CaseSensitive)
        if ($packageFolders.Count -lt 1 -or $packageFolders.Count -gt 16) {
            throw "Restore metadata contains an invalid package-folder count."
        }

        $packageCandidates = @()
        foreach ($packageFolder in $packageFolders) {
            if (-not (Test-Path -LiteralPath $packageFolder -PathType Container)) {
                continue
            }
            $folderRoot = Assert-SourceBindingRoot `
                -Path $packageFolder `
                -Description "NuGet global package folder"
            $candidate = Join-Path $folderRoot $PackageRelativePath.Replace('/', '\')
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $relativeFromFolder = $PackageRelativePath
                $packageCandidates += Resolve-SourceBindingPath `
                    -Root $folderRoot `
                    -RelativePath $relativeFromFolder `
                    -Description $Description `
                    -PathType Container
            }
        }
        if ($packageCandidates.Count -ne 1) {
            throw "$Description must resolve in exactly one restored global package folder."
        }

        return [string]$packageCandidates[0].FullName
    }

    function Get-SourceBindingLibraryAssets {
        param(
            [Parameter(Mandatory = $true)][object]$Library,
            [Parameter(Mandatory = $true)][string]$Description
        )

        $paths = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($assetKind in @("runtime", "native")) {
            $property = @($Library.PSObject.Properties | Where-Object {
                [string]::Equals(
                    [string]$_.Name,
                    $assetKind,
                    [System.StringComparison]::Ordinal)
            })
            if ($property.Count -gt 1) {
                throw "$Description contains duplicate '$assetKind' asset groups."
            }
            if ($property.Count -eq 0) {
                continue
            }
            foreach ($asset in @($property[0].Value.PSObject.Properties)) {
                $relative = ([string]$asset.Name).Replace('\', '/')
                Assert-SourceBindingRelativePath `
                    -RelativePath $relative `
                    -Description "$Description $assetKind asset" `
                    -TopLevelOnly
                if (-not $paths.Add($relative)) {
                    throw "$Description contains a duplicate runtime/native asset '$relative'."
                }
            }
        }

        $result = [string[]]@($paths)
        [Array]::Sort($result, [System.StringComparer]::Ordinal)
        return @($result)
    }

    function Get-SourceBindingArtifactFiles {
        param([Parameter(Mandatory = $true)][string]$Root)

        $queue = [System.Collections.Generic.Queue[string]]::new()
        $queue.Enqueue($Root)
        $files = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
        $seenDirectories = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        [long]$totalBytes = 0
        $itemCount = 0

        while ($queue.Count -gt 0) {
            $directory = $queue.Dequeue()
            if (-not $seenDirectories.Add($directory)) {
                throw "Artifact directory traversal encountered a duplicate directory."
            }
            foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
                $itemCount++
                if ($itemCount -gt $maximumArtifactItems) {
                    throw "Artifact source binding exceeds the bounded item count."
                }
                if (Test-SourceBindingLink -Item $item) {
                    throw "Artifact source binding refuses reparse points."
                }
                if ($item.PSIsContainer) {
                    $queue.Enqueue([string]$item.FullName)
                    continue
                }
                if (-not ($item -is [System.IO.FileInfo])) {
                    throw "Artifact source binding encountered a non-file payload item."
                }
                if ($totalBytes -gt ($maximumArtifactBytes - [long]$item.Length)) {
                    throw "Artifact source binding exceeds the bounded total size."
                }
                $totalBytes += [long]$item.Length
                $files.Add([System.IO.FileInfo]$item)
            }
        }

        return @($files)
    }

    $artifactRoot = Assert-SourceBindingRoot `
        -Path $ArtifactDirectory `
        -Description "Release artifact directory"
    $repositoryPath = Assert-SourceBindingRoot `
        -Path $RepositoryRoot `
        -Description "Repository root"

    $dotnetFile = Get-Item -LiteralPath ([System.IO.Path]::GetFullPath($DotnetPath)) `
        -Force -ErrorAction Stop
    if (-not ($dotnetFile -is [System.IO.FileInfo]) -or
        (Test-SourceBindingLink -Item $dotnetFile) -or
        -not [string]::Equals(
            $dotnetFile.Name,
            "dotnet.exe",
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "DotnetPath must identify a regular dotnet.exe file."
    }
    $dotnetRoot = Assert-SourceBindingRoot `
        -Path $dotnetFile.DirectoryName `
        -Description "Pinned dotnet root"
    foreach ($legalMaterial in @(
        [pscustomobject]@{
            Source = "LICENSE.txt"
            Destination = "DOTNET-LICENSE.txt"
        },
        [pscustomobject]@{
            Source = "ThirdPartyNotices.txt"
            Destination = "DOTNET-THIRD-PARTY-NOTICES.txt"
        })) {
        $legalSource = Resolve-SourceBindingPath `
            -Root $dotnetRoot `
            -RelativePath $legalMaterial.Source `
            -Description "Pinned .NET legal source '$($legalMaterial.Source)'" `
            -TopLevelOnly
        $legalArtifact = Resolve-SourceBindingPath `
            -Root $artifactRoot `
            -RelativePath $legalMaterial.Destination `
            -Description "Shipped .NET legal material '$($legalMaterial.Destination)'" `
            -TopLevelOnly
        Assert-SourceBindingFilesEqual `
            -ArtifactFile $legalArtifact `
            -SourceFile $legalSource `
            -Description "Shipped .NET legal material '$($legalMaterial.Destination)'"
    }

    $assetsPath = Join-Path $repositoryPath "src\WinShot.App\obj\project.assets.json"
    $lockPath = Join-Path $repositoryPath "src\WinShot.App\packages.lock.json"
    $depsPath = Join-Path $artifactRoot "WinShot.deps.json"
    $runtimeConfigPath = Join-Path $artifactRoot "WinShot.runtimeconfig.json"
    $projectAssets = Read-SourceBindingJson `
        -Path $assetsPath `
        -Description "WinShot.App restore assets"
    $lockFile = Read-SourceBindingJson `
        -Path $lockPath `
        -Description "WinShot.App locked dependency graph" `
        -MaximumBytes 4194304
    $deps = Read-SourceBindingJson `
        -Path $depsPath `
        -Description "Release dependency manifest"
    $runtimeConfig = Read-SourceBindingJson `
        -Path $runtimeConfigPath `
        -Description "Release runtime configuration" `
        -MaximumBytes 1048576

    $restoreLockProperties = Get-SourceBindingProperty `
        -Object $projectAssets.project.restore `
        -Name "restoreLockProperties" `
        -Context "Restore metadata"
    if ([bool](Get-SourceBindingProperty `
        -Object $restoreLockProperties `
        -Name "restoreLockedMode" `
        -Context "Restore lock metadata") -ne $true) {
        throw "Source binding requires project assets produced in locked restore mode."
    }

    $ridTargets = @($projectAssets.targets.PSObject.Properties | Where-Object {
        ([string]$_.Name).EndsWith(
            "/win-x64",
            [System.StringComparison]::Ordinal)
    })
    if ($ridTargets.Count -ne 1) {
        throw "Restore assets must contain exactly one win-x64 target."
    }
    $sourceTargetName = [string]$ridTargets[0].Name
    $sourceTarget = $ridTargets[0].Value
    $sourceFrameworkName = $sourceTargetName.Substring(
        0,
        $sourceTargetName.Length - "/win-x64".Length)

    $depsRuntimeTarget = [string](Get-SourceBindingProperty `
        -Object $deps.runtimeTarget `
        -Name "name" `
        -Context "Release dependency runtime target")
    if (-not $depsRuntimeTarget.EndsWith(
        "/win-x64",
        [System.StringComparison]::Ordinal)) {
        throw "Release dependency manifest is not the exact win-x64 target."
    }
    $depsTarget = Get-SourceBindingProperty `
        -Object $deps.targets `
        -Name $depsRuntimeTarget `
        -Context "Release dependency targets"

    $evidenceByName = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($component in @($ComponentEvidence.components)) {
        $evidenceByName.Add([string]$component.name, $component)
    }

    $boundPaths = [System.Collections.Generic.Dictionary[string,string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    function Register-SourceBinding {
        param(
            [Parameter(Mandatory = $true)][string]$RelativePath,
            [Parameter(Mandatory = $true)][string]$Origin
        )

        if ($boundPaths.ContainsKey($RelativePath)) {
            throw "Payload '$RelativePath' has ambiguous source bindings '$($boundPaths[$RelativePath])' and '$Origin'."
        }
        $boundPaths.Add($RelativePath, $Origin)
    }

    $screenVersion = "6.3.0"
    $screenIdentity = "ScreenRecorderLib/$screenVersion"
    $sourceScreenTarget = Get-SourceBindingProperty `
        -Object $sourceTarget `
        -Name $screenIdentity `
        -Context "win-x64 restore target"
    if ([string](Get-SourceBindingProperty `
        -Object $sourceScreenTarget `
        -Name "type" `
        -Context $screenIdentity) -cne "package") {
        throw "$screenIdentity is not a restored package."
    }
    $sourceScreenLibrary = Get-SourceBindingProperty `
        -Object $projectAssets.libraries `
        -Name $screenIdentity `
        -Context "Restore libraries"
    if ([string](Get-SourceBindingProperty `
        -Object $sourceScreenLibrary `
        -Name "type" `
        -Context "$screenIdentity library") -cne "package") {
        throw "$screenIdentity has an unexpected restore-library type."
    }
    $screenPackagePath = [string](Get-SourceBindingProperty `
        -Object $sourceScreenLibrary `
        -Name "path" `
        -Context "$screenIdentity library")
    if ($screenPackagePath -cne "screenrecorderlib/6.3.0") {
        throw "$screenIdentity has an unexpected global-package path."
    }
    $screenAssetsSha512 = [string](Get-SourceBindingProperty `
        -Object $sourceScreenLibrary `
        -Name "sha512" `
        -Context "$screenIdentity library")
    $screenFiles = @((Get-SourceBindingProperty `
        -Object $sourceScreenLibrary `
        -Name "files" `
        -Context "$screenIdentity library") | ForEach-Object { [string]$_ })
    if ($screenFiles -cnotcontains "build/x64/ScreenRecorderLib.dll") {
        throw "$screenIdentity does not declare its exact x64 payload asset."
    }

    $lockFrameworkNames = @(
        $lockFile.dependencies.PSObject.Properties.Name |
            Where-Object {
                [string]::Equals(
                    [string]$_,
                    $sourceFrameworkName,
                    [System.StringComparison]::Ordinal) -or
                [string]::Equals(
                    [string]$_,
                    ($sourceFrameworkName -replace '\.0$', ''),
                    [System.StringComparison]::Ordinal)
            }
    )
    if ($lockFrameworkNames.Count -ne 1) {
        throw "Locked dependency frameworks must contain one exact or NuGet-normalized target for '$sourceFrameworkName'."
    }
    $lockFramework = Get-SourceBindingProperty `
        -Object $lockFile.dependencies `
        -Name ([string]$lockFrameworkNames[0]) `
        -Context "Locked dependency frameworks"
    $lockedScreen = Get-SourceBindingProperty `
        -Object $lockFramework `
        -Name "ScreenRecorderLib" `
        -Context "Locked $sourceFrameworkName dependencies"
    if ([string](Get-SourceBindingProperty `
        -Object $lockedScreen `
        -Name "type" `
        -Context "Locked ScreenRecorderLib") -cne "Direct" -or
        [string](Get-SourceBindingProperty `
        -Object $lockedScreen `
        -Name "resolved" `
        -Context "Locked ScreenRecorderLib") -cne $screenVersion) {
        throw "ScreenRecorderLib must be a direct dependency locked to exact version 6.3.0."
    }
    $screenLockedSha512 = [string](Get-SourceBindingProperty `
        -Object $lockedScreen `
        -Name "contentHash" `
        -Context "Locked ScreenRecorderLib")
    if ($screenLockedSha512 -cne $screenAssetsSha512) {
        throw "ScreenRecorderLib restore assets do not match the locked package content hash."
    }

    $screenPackageRoot = Get-SourceBindingPackageRoot `
        -ProjectAssets $projectAssets `
        -PackageRelativePath $screenPackagePath `
        -Description $screenIdentity
    $screenMetadataFile = Resolve-SourceBindingPath `
        -Root $screenPackageRoot `
        -RelativePath ".nupkg.metadata" `
        -Description "$screenIdentity NuGet restore metadata" `
        -TopLevelOnly
    $screenMetadata = Read-SourceBindingJson `
        -Path $screenMetadataFile.FullName `
        -Description "$screenIdentity NuGet restore metadata" `
        -MaximumBytes 4096
    if ([int](Get-SourceBindingProperty `
        -Object $screenMetadata `
        -Name "version" `
        -Context "$screenIdentity NuGet restore metadata") -lt 2 -or
        [string](Get-SourceBindingProperty `
        -Object $screenMetadata `
        -Name "contentHash" `
        -Context "$screenIdentity NuGet restore metadata") -cne $screenLockedSha512 -or
        [string](Get-SourceBindingProperty `
        -Object $screenMetadata `
        -Name "source" `
        -Context "$screenIdentity NuGet restore metadata") -cne
            "https://api.nuget.org/v3/index.json") {
        throw "$screenIdentity global package metadata does not match the locked nuget.org restore."
    }
    $screenShaFile = Resolve-SourceBindingPath `
        -Root $screenPackageRoot `
        -RelativePath "screenrecorderlib.6.3.0.nupkg.sha512" `
        -Description "$screenIdentity package content hash" `
        -TopLevelOnly
    if ($screenShaFile.Length -lt 80 -or $screenShaFile.Length -gt 1024) {
        throw "$screenIdentity package content hash file has an invalid size."
    }
    $screenPackageSha512 = (Get-Content -LiteralPath $screenShaFile.FullName -Raw).Trim()
    if ($screenPackageSha512 -notmatch '^[A-Za-z0-9+/]{86}==$') {
        throw "$screenIdentity global package SHA-512 is malformed."
    }
    $screenNupkgFile = Resolve-SourceBindingPath `
        -Root $screenPackageRoot `
        -RelativePath "screenrecorderlib.6.3.0.nupkg" `
        -Description "$screenIdentity cached package archive" `
        -TopLevelOnly
    $actualScreenPackageSha512 = Get-SourceBindingSha512Base64 `
        -File $screenNupkgFile `
        -Description "$screenIdentity cached package archive"
    if ($actualScreenPackageSha512 -cne $screenPackageSha512) {
        throw "$screenIdentity cached package archive does not match its global-package SHA-512."
    }
    $screenEvidence = $evidenceByName['ScreenRecorderLib']
    if ((Get-SourceBindingHash `
            -File $screenNupkgFile `
            -Description "$screenIdentity cached package archive") -cne
        [string]$screenEvidence.sourceSha256) {
        throw "$screenIdentity cached package archive does not match reviewed source SHA-256 evidence."
    }

    $screenSourceFile = Resolve-SourceBindingPath `
        -Root $screenPackageRoot `
        -RelativePath "build/x64/ScreenRecorderLib.dll" `
        -Description "$screenIdentity x64 package asset"
    $screenArtifactFile = Resolve-SourceBindingPath `
        -Root $artifactRoot `
        -RelativePath "ScreenRecorderLib.dll" `
        -Description "Shipped ScreenRecorderLib.dll" `
        -TopLevelOnly
    Assert-SourceBindingFilesEqual `
        -ArtifactFile $screenArtifactFile `
        -SourceFile $screenSourceFile `
        -Description "Shipped ScreenRecorderLib.dll"
    if ((Get-SourceBindingHash `
            -File $screenArtifactFile `
            -Description "Shipped ScreenRecorderLib.dll") -cne
            [string]$screenEvidence.payloadSha256 -or
        [string](Get-AuthenticodeSignature -LiteralPath $screenArtifactFile.FullName).Status -cne
            [string]$screenEvidence.payloadAuthenticodeStatus) {
        throw "Shipped ScreenRecorderLib.dll does not match reviewed payload hash or signature evidence."
    }

    $screenAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName(
        $screenSourceFile.FullName).Version.ToString()
    $screenFileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
        $screenSourceFile.FullName).FileVersion
    if ($screenAssemblyVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' -or
        $screenFileVersion -cne '6.3.0.0') {
        throw "$screenIdentity package asset has unexpected assembly or file version metadata."
    }
    $screenReferenceIdentity = "ScreenRecorderLib.Reference/$screenAssemblyVersion"
    $depsScreenLibrary = Get-SourceBindingProperty `
        -Object $deps.libraries `
        -Name $screenReferenceIdentity `
        -Context "Release dependency libraries"
    if ([string](Get-SourceBindingProperty `
            -Object $depsScreenLibrary `
            -Name "type" `
            -Context "Release $screenReferenceIdentity") -cne "reference" -or
        [bool](Get-SourceBindingProperty `
            -Object $depsScreenLibrary `
            -Name "serviceable" `
            -Context "Release $screenReferenceIdentity") -or
        [string](Get-SourceBindingProperty `
            -Object $depsScreenLibrary `
            -Name "sha512" `
            -Context "Release $screenReferenceIdentity") -cne '') {
        throw "Release dependency metadata does not identify the exact build-injected ScreenRecorderLib reference."
    }
    $depsScreenTarget = Get-SourceBindingProperty `
        -Object $depsTarget `
        -Name $screenReferenceIdentity `
        -Context "Release dependency target"
    $depsScreenRuntime = Get-SourceBindingProperty `
        -Object $depsScreenTarget `
        -Name "runtime" `
        -Context "Release $screenReferenceIdentity target"
    $depsScreenAsset = Get-SourceBindingProperty `
        -Object $depsScreenRuntime `
        -Name "ScreenRecorderLib.dll" `
        -Context "Release $screenReferenceIdentity runtime"
    if ([string](Get-SourceBindingProperty `
            -Object $depsScreenAsset `
            -Name "assemblyVersion" `
            -Context "Release ScreenRecorderLib.dll metadata") -cne $screenAssemblyVersion -or
        [string](Get-SourceBindingProperty `
            -Object $depsScreenAsset `
            -Name "fileVersion" `
            -Context "Release ScreenRecorderLib.dll metadata") -cne $screenFileVersion) {
        throw "Release dependency metadata does not bind the reviewed ScreenRecorderLib payload version."
    }
    Register-SourceBinding `
        -RelativePath "ScreenRecorderLib.dll" `
        -Origin $screenIdentity

    $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    function Get-IncludedFrameworkVersion {
        param([Parameter(Mandatory = $true)][string]$Name)

        $frameworkCandidates = @($includedFrameworks | Where-Object {
            [string]::Equals(
                [string]$_.name,
                $Name,
                [System.StringComparison]::Ordinal)
        })
        if ($frameworkCandidates.Count -ne 1 -or
            [string]($frameworkCandidates[0].version) -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
            throw "Runtime configuration must contain one exact '$Name' version."
        }
        return [string]($frameworkCandidates[0].version)
    }

    $netCoreVersion = Get-IncludedFrameworkVersion -Name "Microsoft.NETCore.App"
    $windowsDesktopVersion = Get-IncludedFrameworkVersion -Name "Microsoft.WindowsDesktop.App"
    $runtimePackCount = 0
    foreach ($framework in @(
        [pscustomobject]@{
            Name = "Microsoft.NETCore.App"
            Version = $netCoreVersion
        },
        [pscustomobject]@{
            Name = "Microsoft.WindowsDesktop.App"
            Version = $windowsDesktopVersion
        })) {
        $runtimePackIdentity =
            "runtimepack.$($framework.Name).Runtime.win-x64/$($framework.Version)"
        $runtimePackLibrary = Get-SourceBindingProperty `
            -Object $depsTarget `
            -Name $runtimePackIdentity `
            -Context "Release dependency target"
        $runtimePackMetadata = Get-SourceBindingProperty `
            -Object $deps.libraries `
            -Name $runtimePackIdentity `
            -Context "Release dependency libraries"
        if ([string](Get-SourceBindingProperty `
            -Object $runtimePackMetadata `
            -Name "type" `
            -Context $runtimePackIdentity) -cne "runtimepack") {
            throw "$runtimePackIdentity has an unexpected dependency type."
        }
        $runtimeComponentName = "$($framework.Name).Runtime.win-x64"
        $runtimeEvidence = $evidenceByName[$runtimeComponentName]
        $runtimePackageRelative =
            "$($runtimeComponentName.ToLowerInvariant())/$($framework.Version)"
        $runtimePackageRoot = Get-SourceBindingPackageRoot `
            -ProjectAssets $projectAssets `
            -PackageRelativePath $runtimePackageRelative `
            -Description "$runtimeComponentName/$($framework.Version)"
        $runtimeNupkgName =
            "$($runtimeComponentName.ToLowerInvariant()).$($framework.Version).nupkg"
        $runtimeNupkgFile = Resolve-SourceBindingPath `
            -Root $runtimePackageRoot `
            -RelativePath $runtimeNupkgName `
            -Description "$runtimeComponentName/$($framework.Version) cached package archive" `
            -TopLevelOnly
        if ((Get-SourceBindingHash `
                -File $runtimeNupkgFile `
                -Description "$runtimeComponentName/$($framework.Version) cached package archive") -cne
            [string]$runtimeEvidence.sourceSha256) {
            throw "$runtimeComponentName/$($framework.Version) cached package archive does not match reviewed source SHA-256 evidence."
        }

        $sharedRootRelative = "shared/$($framework.Name)/$($framework.Version)"
        $sharedFrameworkRoot = Resolve-SourceBindingPath `
            -Root $dotnetRoot `
            -RelativePath $sharedRootRelative `
            -Description "$($framework.Name) shared framework" `
            -PathType Container
        $assets = Get-SourceBindingLibraryAssets `
            -Library $runtimePackLibrary `
            -Description $runtimePackIdentity
        if ($assets.Count -lt 1 -or $assets.Count -gt 1024) {
            throw "$runtimePackIdentity has an invalid payload asset count."
        }

        foreach ($relative in $assets) {
            $artifactFile = Resolve-SourceBindingPath `
                -Root $artifactRoot `
                -RelativePath $relative `
                -Description "$runtimePackIdentity payload asset" `
                -TopLevelOnly
            if ($framework.Name -ceq "Microsoft.NETCore.App" -and
                $relative -ceq "hostfxr.dll") {
                # hostfxr is part of the self-contained runtime pack but the installed dotnet
                # layout keeps it beside the matching host version rather than in shared/.
                $hostFxrRoot = Resolve-SourceBindingPath `
                    -Root $dotnetRoot `
                    -RelativePath "host/fxr/$($framework.Version)" `
                    -Description "Matching dotnet hostfxr version" `
                    -PathType Container
                $sourceFile = Resolve-SourceBindingPath `
                    -Root $hostFxrRoot.FullName `
                    -RelativePath $relative `
                    -Description "$runtimePackIdentity dotnet-host asset" `
                    -TopLevelOnly
            }
            else {
                $sourceFile = Resolve-SourceBindingPath `
                    -Root $sharedFrameworkRoot.FullName `
                    -RelativePath $relative `
                    -Description "$runtimePackIdentity shared-framework asset" `
                    -TopLevelOnly
            }
            Assert-SourceBindingFilesEqual `
                -ArtifactFile $artifactFile `
                -SourceFile $sourceFile `
                -Description "$runtimePackIdentity asset '$relative'"
            Register-SourceBinding `
                -RelativePath $relative `
                -Origin $runtimePackIdentity
            $runtimePackCount++
        }
    }

    $artifactFiles = Get-SourceBindingArtifactFiles -Root $artifactRoot
    $artifactPrefix = $artifactRoot.TrimEnd('\') + '\'
    $windowsDesktopSharedRoot = Resolve-SourceBindingPath `
        -Root $dotnetRoot `
        -RelativePath "shared/Microsoft.WindowsDesktop.App/$windowsDesktopVersion" `
        -Description "Microsoft.WindowsDesktop.App satellite source" `
        -PathType Container
    $satelliteCount = 0
    foreach ($file in $artifactFiles) {
        if (-not $file.Name.EndsWith(
            ".resources.dll",
            [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not $file.FullName.StartsWith(
            $artifactPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Artifact satellite resolves outside the artifact root."
        }
        $relative = $file.FullName.Substring($artifactPrefix.Length).Replace('\', '/')
        Assert-SourceBindingRelativePath `
            -RelativePath $relative `
            -Description "Artifact satellite"
        $segments = @($relative.Split('/'))
        if ($segments.Count -ne 2 -or
            $segments[0] -notmatch '^[A-Za-z]{2}(?:-[A-Za-z]{2,4})?$' -or
            -not $segments[1].EndsWith(
                ".resources.dll",
                [System.StringComparison]::Ordinal)) {
            throw "Artifact satellite '$relative' has a noncanonical satellite path."
        }

        $sourceFile = Resolve-SourceBindingPath `
            -Root $windowsDesktopSharedRoot.FullName `
            -RelativePath $relative `
            -Description "WindowsDesktop satellite '$relative'"
        Assert-SourceBindingFilesEqual `
            -ArtifactFile $file `
            -SourceFile $sourceFile `
            -Description "WindowsDesktop satellite '$relative'"
        Register-SourceBinding `
            -RelativePath $relative `
            -Origin "runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/$windowsDesktopVersion"
        $satelliteCount++
    }

    $sourceFramework = Get-SourceBindingProperty `
        -Object $projectAssets.project.frameworks `
        -Name $sourceFrameworkName `
        -Context "Restore project frameworks"
    $downloadDependencies = @($sourceFramework.downloadDependencies | Where-Object {
        [string]::Equals(
            [string]$_.name,
            "Microsoft.Windows.SDK.NET.Ref",
            [System.StringComparison]::Ordinal)
    })
    if ($downloadDependencies.Count -ne 1) {
        throw "Restore metadata must contain one Microsoft.Windows.SDK.NET.Ref download dependency."
    }
    $windowsSdkVersionRange = [string]($downloadDependencies[0].version)
    if ($windowsSdkVersionRange -notmatch '^\[([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+), \1\]$') {
        throw "Microsoft.Windows.SDK.NET.Ref must be restored at one exact version."
    }
    $windowsSdkVersion = [string]$Matches[1]
    $windowsSdkIdentity = "runtimepack.Microsoft.Windows.SDK.NET.Ref/$windowsSdkVersion"
    $windowsSdkLibrary = Get-SourceBindingProperty `
        -Object $depsTarget `
        -Name $windowsSdkIdentity `
        -Context "Release dependency target"
    $windowsSdkMetadata = Get-SourceBindingProperty `
        -Object $deps.libraries `
        -Name $windowsSdkIdentity `
        -Context "Release dependency libraries"
    if ([string](Get-SourceBindingProperty `
        -Object $windowsSdkMetadata `
        -Name "type" `
        -Context $windowsSdkIdentity) -cne "runtimepack") {
        throw "$windowsSdkIdentity has an unexpected dependency type."
    }

    $windowsSdkPackagePath = "microsoft.windows.sdk.net.ref/$windowsSdkVersion"
    $windowsSdkPackageRoot = Get-SourceBindingPackageRoot `
        -ProjectAssets $projectAssets `
        -PackageRelativePath $windowsSdkPackagePath `
        -Description "Microsoft.Windows.SDK.NET.Ref/$windowsSdkVersion"
    $windowsSdkNupkgFile = Resolve-SourceBindingPath `
        -Root $windowsSdkPackageRoot `
        -RelativePath "microsoft.windows.sdk.net.ref.$windowsSdkVersion.nupkg" `
        -Description "Microsoft.Windows.SDK.NET.Ref/$windowsSdkVersion cached package archive" `
        -TopLevelOnly
    if ((Get-SourceBindingHash `
            -File $windowsSdkNupkgFile `
            -Description "Microsoft.Windows.SDK.NET.Ref/$windowsSdkVersion cached package archive") -cne
        [string]$evidenceByName['Microsoft.Windows.SDK.NET.Ref'].sourceSha256) {
        throw "Microsoft.Windows.SDK.NET.Ref/$windowsSdkVersion cached package archive does not match reviewed source SHA-256 evidence."
    }
    $windowsSdkAssets = Get-SourceBindingLibraryAssets `
        -Library $windowsSdkLibrary `
        -Description $windowsSdkIdentity
    if ($windowsSdkAssets.Count -lt 1 -or $windowsSdkAssets.Count -gt 64) {
        throw "$windowsSdkIdentity has an invalid runtime asset count."
    }

    $windowsSdkLibRoot = Resolve-SourceBindingPath `
        -Root $windowsSdkPackageRoot `
        -RelativePath "lib" `
        -Description "$windowsSdkIdentity package library root" `
        -PathType Container
    $windowsSdkLibDirectories = @(
        Get-ChildItem -LiteralPath $windowsSdkLibRoot.FullName -Directory -Force |
            Sort-Object -Property Name -CaseSensitive)
    if ($windowsSdkLibDirectories.Count -lt 1 -or
        $windowsSdkLibDirectories.Count -gt 16) {
        throw "$windowsSdkIdentity has an invalid package library-directory count."
    }
    foreach ($libraryDirectory in $windowsSdkLibDirectories) {
        if ($libraryDirectory.Name -notmatch '^net[0-9]+\.[0-9]+$' -or
            (Test-SourceBindingLink -Item $libraryDirectory)) {
            throw "$windowsSdkIdentity has an unsafe package library directory."
        }
    }
    $windowsSdkCount = 0
    foreach ($relative in $windowsSdkAssets) {
        $sourceCandidates = @(
            foreach ($libraryDirectory in $windowsSdkLibDirectories) {
                $candidateRelative = "$($libraryDirectory.Name)/$relative"
                $candidatePath = Join-Path `
                    $windowsSdkLibRoot.FullName `
                    $candidateRelative.Replace('/', '\')
                if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
                    Resolve-SourceBindingPath `
                        -Root $windowsSdkLibRoot.FullName `
                        -RelativePath $candidateRelative `
                        -Description "$windowsSdkIdentity package asset '$relative'"
                }
            })
        if ($sourceCandidates.Count -ne 1) {
            throw "$windowsSdkIdentity package asset '$relative' must resolve from exactly one reviewed library folder."
        }
        $sourceFile = $sourceCandidates[0]
        $artifactFile = Resolve-SourceBindingPath `
            -Root $artifactRoot `
            -RelativePath $relative `
            -Description "$windowsSdkIdentity payload asset '$relative'" `
            -TopLevelOnly
        Assert-SourceBindingFilesEqual `
            -ArtifactFile $artifactFile `
            -SourceFile $sourceFile `
            -Description "$windowsSdkIdentity asset '$relative'"
        Register-SourceBinding `
            -RelativePath $relative `
            -Origin $windowsSdkIdentity
        $windowsSdkCount++
    }

    $appHostTemplate = Resolve-SourceBindingPath `
        -Root $dotnetRoot `
        -RelativePath "packs/Microsoft.NETCore.App.Host.win-x64/$netCoreVersion/runtimes/win-x64/native/apphost.exe" `
        -Description "Microsoft.NETCore.App.Host.win-x64/$netCoreVersion apphost template"
    if ((Get-SourceBindingHash `
            -File $appHostTemplate `
            -Description "Microsoft.NETCore.App.Host.win-x64/$netCoreVersion apphost template") -cne
        [string]$evidenceByName['Microsoft.NETCore.App.Host.win-x64'].sourceSha256) {
        throw "Microsoft.NETCore.App.Host.win-x64/$netCoreVersion template does not match reviewed source SHA-256 evidence."
    }

    return [pscustomobject]@{
        Result = "PASS_SOURCE_BINDING"
        ScreenRecorderFiles = 1
        RuntimePackFiles = $runtimePackCount
        SatelliteFiles = $satelliteCount
        WindowsSdkFiles = $windowsSdkCount
        LegalEvidenceFiles = 2
        TotalBoundFiles = $boundPaths.Count
        ScreenRecorderContentHash = $screenLockedSha512
        ScreenRecorderNupkgSha512 = $screenPackageSha512
        NetCoreVersion = $netCoreVersion
        WindowsDesktopVersion = $windowsDesktopVersion
        WindowsSdkVersion = $windowsSdkVersion
    }
}
