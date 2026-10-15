[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AppPath,

    [ValidateSet(640, 760, 900)]
    [int]$Width = 760,

    [string]$DotnetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:HarnessSourceSha256 = ''

function Get-HarnessSourceFingerprint {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Specialized.OrderedDictionary]$SourceFiles
    )

    $fingerprintLines = foreach ($entry in $SourceFiles.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
            throw 'HarnessFingerprintSourceMissing'
        }
        $fileHash = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
        if ($fileHash -notmatch '^[0-9A-F]{64}$') {
            throw 'HarnessFingerprintSourceInvalid'
        }
        '{0}:{1}' -f $entry.Key, $fileHash
    }
    $fingerprintBytes = [Text.Encoding]::UTF8.GetBytes(
        (($fingerprintLines -join "`n") + "`n"))
    $fingerprintAlgorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $fingerprintHashBytes =
            $fingerprintAlgorithm.ComputeHash($fingerprintBytes)
    }
    finally {
        $fingerprintAlgorithm.Dispose()
    }
    $fingerprint =
        ([BitConverter]::ToString($fingerprintHashBytes)).Replace('-', '')
    if ($fingerprint -notmatch '^[0-9A-F]{64}$') {
        throw 'HarnessFingerprintInvalid'
    }
    return $fingerprint
}

function Write-HarnessFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FailureCode
    )

    [ordered]@{
        SchemaVersion = 4
        EvidenceKind = 'WinShotMainWindowUiAutomation'
        SdkVersion = ''
        Configuration = ''
        SourceCommit = ''
        SourceDirty = $false
        HarnessSourceSha256 = $script:HarnessSourceSha256
        AppSha256 = ''
        AppAssemblySha256 = ''
        ChildPid = 0
        ChildIdentityMatched = $false
        WidthDip = $Width
        WindowDpi = 0
        WindowWidthPixels = 0
        WindowWidthMatched = $false
        RecentStripFound = $false
        RecentButtonCount = 0
        ButtonsPassed = $false
        InvokeAckCount = 0
        FocusSequencePassed = $false
        BringIntoViewPassed = $false
        RealInputAckCount = 0
        TotalAckCount = 0
        RealInputPassed = $false
        ShutdownSignalSent = $false
        GracefulShutdown = $false
        FallbackKillUsed = $false
        ChildExited = $false
        TemporaryDirectoryRemoved = $false
        FailureCode = $FailureCode
        OverallPassed = $false
    } | ConvertTo-Json -Compress
    exit 2
}

try {
    if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
        $DotnetPath = Join-Path `
            ([Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData)) `
            'Microsoft\dotnet-winshot-10.0.110\dotnet.exe'
    }

    $DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        Write-HarnessFailure -FailureCode 'PinnedDotnetNotFound'
    }

    $sdkVersionOutput = @(& $DotnetPath --version 2>$null)
    $sdkVersionExitCode = $LASTEXITCODE
    $sdkVersion = if ($sdkVersionOutput.Count -eq 1) {
        ([string]$sdkVersionOutput[0]).Trim()
    }
    else {
        ''
    }
    if ($sdkVersionExitCode -ne 0 -or $sdkVersion -ne '10.0.110') {
        Write-HarnessFailure -FailureCode 'PinnedDotnetVersionMismatch'
    }

    $AppPath = [IO.Path]::GetFullPath($AppPath)
    if ([IO.Path]::GetFileName($AppPath) -ine 'WinShot.exe') {
        Write-HarnessFailure -FailureCode 'UnexpectedAppFileName'
    }
    $repositoryRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\..')
    ).TrimEnd('\')
    $expectedAppPath = Join-Path $repositoryRoot `
        'src\WinShot.App\bin\x64\Debug\net10.0-windows10.0.19041.0\WinShot.exe'
    if (-not [string]::Equals(
            $AppPath,
            [IO.Path]::GetFullPath($expectedAppPath),
            [StringComparison]::OrdinalIgnoreCase)) {
        Write-HarnessFailure -FailureCode 'AppIsNotRepositoryDebugOutput'
    }
    $debugOutputPattern =
        '(?i)\\bin\\x64\\Debug\\net10\.0-windows10\.0\.19041\.0\\WinShot\.exe$'
    if ($AppPath -notmatch $debugOutputPattern) {
        Write-HarnessFailure -FailureCode 'AppIsNotExpectedDebugOutput'
    }

    $projectDirectory = Join-Path $PSScriptRoot 'main-window-uia'
    $project = Join-Path $projectDirectory 'WinShot.MainWindow.Uia.csproj'
    $applicationManifest = Join-Path $projectDirectory 'app.manifest'
    $nugetConfig = Join-Path $projectDirectory 'NuGet.Config'
    $repositoryRoot = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\..'))
    $directoryBuildProps = Join-Path $repositoryRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $project -PathType Leaf) -or
        -not (Test-Path -LiteralPath $applicationManifest -PathType Leaf) -or
        -not (Test-Path -LiteralPath $nugetConfig -PathType Leaf) -or
        -not (Test-Path -LiteralPath $directoryBuildProps -PathType Leaf)) {
        Write-HarnessFailure -FailureCode 'HarnessSourceMissing'
    }

    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $externalReferences = @(
        $projectXml.SelectNodes(
            '//PackageReference|//ProjectReference|//PackageDownload|//Import')
    )
    if ($externalReferences.Count -ne 0) {
        Write-HarnessFailure -FailureCode 'HarnessDependencyBoundaryChanged'
    }
    if (@($projectXml.SelectNodes('//TargetFramework')).Count -ne 0) {
        Write-HarnessFailure -FailureCode 'HarnessMustInheritTargetFramework'
    }

    $defaultCompileItems = @($projectXml.SelectNodes('//EnableDefaultCompileItems'))
    $compileItems = @($projectXml.SelectNodes('//Compile'))
    $useWpf = @($projectXml.SelectNodes('//UseWPF'))
    $platformTarget = @($projectXml.SelectNodes('//PlatformTarget'))
    $platforms = @($projectXml.SelectNodes('//Platforms'))
    $manifestSettings = @($projectXml.SelectNodes('//ApplicationManifest'))
    $lockSetting = @($projectXml.SelectNodes('//RestorePackagesWithLockFile'))
    if ($defaultCompileItems.Count -ne 1 -or
        $defaultCompileItems[0].InnerText -cne 'false' -or
        $compileItems.Count -ne 1 -or
        $compileItems[0].GetAttribute('Include') -cne 'Program.cs' -or
        $useWpf.Count -ne 1 -or $useWpf[0].InnerText -cne 'true' -or
        $platformTarget.Count -ne 1 -or
        $platformTarget[0].InnerText -cne 'x64' -or
        $platforms.Count -ne 1 -or $platforms[0].InnerText -cne 'x64' -or
        $manifestSettings.Count -ne 1 -or
        $manifestSettings[0].InnerText -cne 'app.manifest' -or
        -not [string]::IsNullOrEmpty(
            $manifestSettings[0].GetAttribute('Condition')) -or
        $manifestSettings[0].ParentNode.LocalName -cne 'PropertyGroup' -or
        -not [string]::IsNullOrEmpty(
            $manifestSettings[0].ParentNode.GetAttribute('Condition')) -or
        $lockSetting.Count -ne 1 -or $lockSetting[0].InnerText -cne 'false') {
        Write-HarnessFailure -FailureCode 'HarnessProjectContractChanged'
    }

    [xml]$manifestXml = Get-Content -LiteralPath $applicationManifest -Raw
    $executionLevels = @(
        $manifestXml.SelectNodes(
            "//*[local-name()='requestedExecutionLevel']")
    )
    $dpiAwareSettings = @(
        $manifestXml.SelectNodes("//*[local-name()='dpiAware']")
    )
    $dpiAwarenessSettings = @(
        $manifestXml.SelectNodes("//*[local-name()='dpiAwareness']")
    )
    $longPathSettings = @(
        $manifestXml.SelectNodes("//*[local-name()='longPathAware']")
    )
    if ($executionLevels.Count -ne 1 -or
        $executionLevels[0].NamespaceURI -cne 'urn:schemas-microsoft-com:asm.v3' -or
        $executionLevels[0].GetAttribute('level') -cne 'asInvoker' -or
        $executionLevels[0].GetAttribute('uiAccess') -cne 'false' -or
        $dpiAwareSettings.Count -ne 1 -or
        $dpiAwareSettings[0].NamespaceURI -cne
            'http://schemas.microsoft.com/SMI/2005/WindowsSettings' -or
        $dpiAwareSettings[0].InnerText -cne 'true/pm' -or
        $dpiAwarenessSettings.Count -ne 1 -or
        $dpiAwarenessSettings[0].NamespaceURI -cne
            'http://schemas.microsoft.com/SMI/2016/WindowsSettings' -or
        $dpiAwarenessSettings[0].InnerText -cne 'PerMonitorV2' -or
        $longPathSettings.Count -ne 1 -or
        $longPathSettings[0].NamespaceURI -cne
            'http://schemas.microsoft.com/SMI/2016/WindowsSettings' -or
        $longPathSettings[0].InnerText -cne 'true') {
        Write-HarnessFailure -FailureCode 'HarnessManifestContractChanged'
    }

    $harnessSourceFiles = [ordered]@{
        'tools/accessibility/Test-MainWindowUiaHarness.ps1' = $PSCommandPath
        'tools/accessibility/main-window-uia/Program.cs' =
            (Join-Path $projectDirectory 'Program.cs')
        'tools/accessibility/main-window-uia/WinShot.MainWindow.Uia.csproj' =
            $project
        'tools/accessibility/main-window-uia/app.manifest' =
            $applicationManifest
        'tools/accessibility/main-window-uia/NuGet.Config' =
            $nugetConfig
        'Directory.Build.props' = $directoryBuildProps
    }
    try {
        $script:HarnessSourceSha256 =
            Get-HarnessSourceFingerprint -SourceFiles $harnessSourceFiles
    }
    catch {
        Write-HarnessFailure -FailureCode $_.Exception.Message
    }

    [xml]$directoryBuildPropsXml =
        Get-Content -LiteralPath $directoryBuildProps -Raw
    $inheritedTargetFrameworks = @(
        $directoryBuildPropsXml.SelectNodes('/Project/PropertyGroup/TargetFramework')
    )
    if ($inheritedTargetFrameworks.Count -ne 1 -or
        $inheritedTargetFrameworks[0].InnerText -cne
            'net10.0-windows10.0.19041.0') {
        Write-HarnessFailure -FailureCode 'InheritedTargetFrameworkChanged'
    }

    [xml]$nugetConfigXml = Get-Content -LiteralPath $nugetConfig -Raw
    $sourceClears = @(
        $nugetConfigXml.SelectNodes('/configuration/packageSources/clear')
    )
    $configuredSources = @(
        $nugetConfigXml.SelectNodes('/configuration/packageSources/add')
    )
    if ($sourceClears.Count -ne 1 -or $configuredSources.Count -ne 0) {
        Write-HarnessFailure -FailureCode 'HarnessNugetSourcesNotCleared'
    }

    $appProject = Join-Path $repositoryRoot 'src\WinShot.App\WinShot.App.csproj'
    $null = & $DotnetPath restore `
        $appProject `
        --locked-mode `
        -p:Platform=x64 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-HarnessFailure -FailureCode 'AppLockedRestoreFailed'
    }
    $null = & $DotnetPath build `
        $appProject `
        -c Debug `
        --no-restore `
        -t:Rebuild `
        -p:Platform=x64 `
        -p:TreatWarningsAsErrors=true 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-HarnessFailure -FailureCode 'AppDebugRebuildFailed'
    }
    if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
        Write-HarnessFailure -FailureCode 'AppDebugOutputMissing'
    }

    $appAssemblyPath = Join-Path (Split-Path -Parent $AppPath) 'WinShot.dll'
    if (-not (Test-Path -LiteralPath $appAssemblyPath -PathType Leaf)) {
        Write-HarnessFailure -FailureCode 'AppAssemblyMissing'
    }
    $appSha256 = (Get-FileHash -LiteralPath $AppPath -Algorithm SHA256).Hash
    $appAssemblySha256 =
        (Get-FileHash -LiteralPath $appAssemblyPath -Algorithm SHA256).Hash
    if ($appSha256 -notmatch '^[0-9A-F]{64}$' -or
        $appAssemblySha256 -notmatch '^[0-9A-F]{64}$') {
        Write-HarnessFailure -FailureCode 'AppHashInvalid'
    }
    $sourceCommitOutput = @(
        & git -C $repositoryRoot rev-parse HEAD 2>$null
    )
    if ($LASTEXITCODE -ne 0 -or $sourceCommitOutput.Count -ne 1) {
        Write-HarnessFailure -FailureCode 'SourceCommitUnavailable'
    }
    $sourceCommit = ([string]$sourceCommitOutput[0]).Trim()
    if ($sourceCommit -notmatch '^[0-9a-f]{40}$') {
        Write-HarnessFailure -FailureCode 'SourceCommitInvalid'
    }
    $sourceStatus = @(
        & git -C $repositoryRoot status --porcelain=v1 --untracked-files=all 2>$null
    )
    if ($LASTEXITCODE -ne 0) {
        Write-HarnessFailure -FailureCode 'SourceStatusUnavailable'
    }
    $sourceDirty = $sourceStatus.Count -gt 0

    $null = & $DotnetPath restore `
        $project `
        --configfile $nugetConfig `
        -p:Platform=x64 `
        -p:RestorePackagesWithLockFile=false 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-HarnessFailure -FailureCode 'HarnessRestoreFailed'
    }

    $null = & $DotnetPath build `
        $project `
        -c Release `
        --no-restore `
        -p:Platform=x64 `
        -p:TreatWarningsAsErrors=true 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-HarnessFailure -FailureCode 'HarnessBuildFailed'
    }

    $runOutput = @(
        & $DotnetPath run `
            --project $project `
            -c Release `
            --no-build `
            --no-restore `
            -p:Platform=x64 `
            -- `
            --app $AppPath `
            --width $Width 2>&1
    )
    $runExitCode = $LASTEXITCODE
    $summaries = @()
    foreach ($line in $runOutput) {
        try {
            $candidate = ([string]$line) | ConvertFrom-Json
            if ($candidate.EvidenceKind -ceq 'WinShotMainWindowUiAutomation') {
                $summaries += $candidate
            }
        }
        catch {
        }
    }
    if ($summaries.Count -ne 1) {
        Write-HarnessFailure -FailureCode 'HarnessSummaryMissing'
    }

    $summary = $summaries[0]
    $expectedProperties = @(
        'SchemaVersion'
        'EvidenceKind'
        'ChildPid'
        'ChildIdentityMatched'
        'WidthDip'
        'WindowDpi'
        'WindowWidthPixels'
        'WindowWidthMatched'
        'RecentStripFound'
        'RecentButtonCount'
        'ButtonsPassed'
        'InvokeAckCount'
        'FocusSequencePassed'
        'BringIntoViewPassed'
        'RealInputAckCount'
        'TotalAckCount'
        'RealInputPassed'
        'ShutdownSignalSent'
        'GracefulShutdown'
        'FallbackKillUsed'
        'ChildExited'
        'TemporaryDirectoryRemoved'
        'FailureCode'
        'OverallPassed'
    )
    $actualProperties = @($summary.PSObject.Properties.Name)
    if (($actualProperties -join [char]0) -cne
        ($expectedProperties -join [char]0)) {
        Write-HarnessFailure -FailureCode 'HarnessSummarySchemaChanged'
    }

    $booleanProperties = @(
        'ChildIdentityMatched'
        'WindowWidthMatched'
        'RecentStripFound'
        'ButtonsPassed'
        'FocusSequencePassed'
        'BringIntoViewPassed'
        'RealInputPassed'
        'ShutdownSignalSent'
        'GracefulShutdown'
        'FallbackKillUsed'
        'ChildExited'
        'TemporaryDirectoryRemoved'
        'OverallPassed'
    )
    foreach ($property in $booleanProperties) {
        if ($summary.$property -isnot [bool]) {
            Write-HarnessFailure -FailureCode 'HarnessSummaryTypeMismatch'
        }
    }
    if (($summary.SchemaVersion -isnot [int] -and
         $summary.SchemaVersion -isnot [long]) -or
        ($summary.ChildPid -isnot [int] -and
         $summary.ChildPid -isnot [long]) -or
        ($summary.WidthDip -isnot [int] -and
         $summary.WidthDip -isnot [long]) -or
        ($summary.WindowDpi -isnot [int] -and
         $summary.WindowDpi -isnot [long]) -or
        ($summary.WindowWidthPixels -isnot [int] -and
         $summary.WindowWidthPixels -isnot [long]) -or
        ($summary.RecentButtonCount -isnot [int] -and
         $summary.RecentButtonCount -isnot [long]) -or
        ($summary.InvokeAckCount -isnot [int] -and
         $summary.InvokeAckCount -isnot [long]) -or
        ($summary.RealInputAckCount -isnot [int] -and
         $summary.RealInputAckCount -isnot [long]) -or
        ($summary.TotalAckCount -isnot [int] -and
         $summary.TotalAckCount -isnot [long]) -or
        $summary.EvidenceKind -isnot [string] -or
        $summary.FailureCode -isnot [string]) {
        Write-HarnessFailure -FailureCode 'HarnessSummaryTypeMismatch'
    }

    $appSha256After = (Get-FileHash -LiteralPath $AppPath -Algorithm SHA256).Hash
    $appAssemblySha256After =
        (Get-FileHash -LiteralPath $appAssemblyPath -Algorithm SHA256).Hash
    try {
        $harnessSourceSha256After =
            Get-HarnessSourceFingerprint -SourceFiles $harnessSourceFiles
    }
    catch {
        Write-HarnessFailure -FailureCode $_.Exception.Message
    }
    $successInvariant =
        $runExitCode -eq 0 -and
        $summary.SchemaVersion -eq 3 -and
        $summary.EvidenceKind -ceq 'WinShotMainWindowUiAutomation' -and
        $summary.ChildPid -gt 0 -and
        $summary.ChildIdentityMatched -and
        $summary.WidthDip -eq $Width -and
        $summary.WindowDpi -gt 0 -and
        $summary.WindowWidthPixels -gt 0 -and
        $summary.WindowWidthMatched -and
        $summary.RecentStripFound -and
        $summary.RecentButtonCount -eq 5 -and
        $summary.ButtonsPassed -and
        $summary.InvokeAckCount -eq 1 -and
        $summary.FocusSequencePassed -and
        $summary.BringIntoViewPassed -and
        $summary.RealInputAckCount -eq 3 -and
        $summary.TotalAckCount -eq 4 -and
        $summary.RealInputPassed -and
        $summary.ShutdownSignalSent -and
        $summary.GracefulShutdown -and
        -not $summary.FallbackKillUsed -and
        $summary.ChildExited -and
        $summary.TemporaryDirectoryRemoved -and
        $summary.FailureCode -ceq 'None' -and
        $summary.OverallPassed -and
        $harnessSourceSha256After -ceq $script:HarnessSourceSha256 -and
        $appSha256After -ceq $appSha256 -and
        $appAssemblySha256After -ceq $appAssemblySha256
    if (-not $successInvariant) {
        Write-HarnessFailure -FailureCode 'HarnessExitSummaryMismatch'
    }

    [ordered]@{
        SchemaVersion = 4
        EvidenceKind = 'WinShotMainWindowUiAutomation'
        SdkVersion = $sdkVersion
        Configuration = 'Debug'
        SourceCommit = $sourceCommit
        SourceDirty = $sourceDirty
        HarnessSourceSha256 = $script:HarnessSourceSha256
        AppSha256 = $appSha256
        AppAssemblySha256 = $appAssemblySha256
        ChildPid = [int]$summary.ChildPid
        ChildIdentityMatched = $summary.ChildIdentityMatched
        WidthDip = [int]$summary.WidthDip
        WindowDpi = [int]$summary.WindowDpi
        WindowWidthPixels = [int]$summary.WindowWidthPixels
        WindowWidthMatched = $summary.WindowWidthMatched
        RecentStripFound = $summary.RecentStripFound
        RecentButtonCount = [int]$summary.RecentButtonCount
        ButtonsPassed = $summary.ButtonsPassed
        InvokeAckCount = [int]$summary.InvokeAckCount
        FocusSequencePassed = $summary.FocusSequencePassed
        BringIntoViewPassed = $summary.BringIntoViewPassed
        RealInputAckCount = [int]$summary.RealInputAckCount
        TotalAckCount = [int]$summary.TotalAckCount
        RealInputPassed = $summary.RealInputPassed
        ShutdownSignalSent = $summary.ShutdownSignalSent
        GracefulShutdown = $summary.GracefulShutdown
        FallbackKillUsed = $summary.FallbackKillUsed
        ChildExited = $summary.ChildExited
        TemporaryDirectoryRemoved = $summary.TemporaryDirectoryRemoved
        FailureCode = $summary.FailureCode
        OverallPassed = $summary.OverallPassed
    } | ConvertTo-Json -Compress
    exit 0
}
catch {
    Write-HarnessFailure -FailureCode 'HarnessWrapperFailed'
}
