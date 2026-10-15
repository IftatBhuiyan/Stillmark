[CmdletBinding()]
param(
    [string]$DotnetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'Performance.Common.ps1')

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $DotnetPath = Resolve-WinShotPinnedDotnet
}

$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'The exact .NET SDK host was not found. Supply -DotnetPath explicitly.'
}

$sdkVersionOutput = @(& $DotnetPath --version)
$sdkVersionExitCode = $LASTEXITCODE
$sdkVersion = if ($sdkVersionOutput.Count -eq 1) {
    ([string]$sdkVersionOutput[0]).Trim()
}
else {
    ''
}
$expectedSdkVersion = Get-WinShotPinnedSdkVersion
if ($sdkVersionExitCode -ne 0 -or $sdkVersion -cne $expectedSdkVersion) {
    throw "The editor-output harness requires exact repository-pinned SDK $expectedSdkVersion; found '$sdkVersion'."
}

$project = Join-Path $PSScriptRoot 'WinShot.EditorOutput.Perf.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$packageReferences = @($projectXml.SelectNodes('//PackageReference'))
$projectReferences = @($projectXml.SelectNodes('//ProjectReference'))
$packageDownloads = @($projectXml.SelectNodes('//PackageDownload'))
$imports = @($projectXml.SelectNodes('//Import'))
if ($packageReferences.Count -ne 0 -or
    $projectReferences.Count -ne 0 -or
    $packageDownloads.Count -ne 0 -or
    $imports.Count -ne 0) {
    throw 'The isolated harness must not contain package/project/download references or imports.'
}

$lockSetting = @(
    $projectXml.Project.PropertyGroup.RestorePackagesWithLockFile |
        Where-Object { $null -ne $_ }
)
if ($lockSetting.Count -ne 1 -or
    $lockSetting[0].ToString() -ne 'false') {
    throw 'The isolated harness must explicitly disable package-lock generation.'
}

$defaultCompileSetting = @(
    $projectXml.Project.PropertyGroup.EnableDefaultCompileItems |
        Where-Object { $null -ne $_ }
)
if ($defaultCompileSetting.Count -ne 1 -or
    $defaultCompileSetting[0].ToString() -ne 'false') {
    throw 'The isolated harness must explicitly disable default compile items.'
}

$expectedCompileSources = @(
    'BenchmarkInfrastructure.cs|'
    'BenchmarkModels.cs|'
    'DispatcherHeartbeatBenchmark.cs|'
    'EditorOutputBenchmark.cs|'
    'GlobalUsings.cs|'
    'HarnessSelfTest.cs|'
    'Program.cs|'
    '..\..\..\src\WinShot.App\Editor\Annotations.cs|MeasuredSource\Annotations.cs'
    '..\..\..\src\WinShot.App\Editor\EditorDocument.cs|MeasuredSource\EditorDocument.cs'
    '..\..\..\src\WinShot.App\Editor\EditorResultPreparationCache.cs|MeasuredSource\EditorResultPreparationCache.cs'
    '..\..\..\src\WinShot.App\Editor\EditorResultPreparationCoordinator.cs|MeasuredSource\EditorResultPreparationCoordinator.cs'
    '..\..\..\src\WinShot.App\Editor\PixelFilters.cs|MeasuredSource\PixelFilters.cs'
    '..\..\..\src\WinShot.App\Editor\RegionEffectProcessing.cs|MeasuredSource\RegionEffectProcessing.cs'
    '..\..\..\src\WinShot.App\EncodedImagePayload.cs|MeasuredSource\EncodedImagePayload.cs'
    '..\..\..\src\WinShot.App\ImageBeautifier.cs|MeasuredSource\ImageBeautifier.cs'
    '..\..\..\src\WinShot.App\ImageSaveService.cs|MeasuredSource\ImageSaveService.cs'
    '..\..\..\src\WinShot.App\StillImageEncodingService.cs|MeasuredSource\StillImageEncodingService.cs'
    '..\..\..\src\WinShot.Core\Naming\FileNameTemplate.cs|MeasuredSource\FileNameTemplate.cs'
    '..\..\..\src\WinShot.Core\Naming\OutputFileReservation.cs|MeasuredSource\OutputFileReservation.cs'
    '..\..\..\src\WinShot.Core\Settings\AppSettings.cs|MeasuredSource\AppSettings.cs'
) | Sort-Object
$expectedEmbeddedSources = @(
    '..\..\..\src\WinShot.App\Editor\Annotations.cs|WinShot.EditorOutput.Measured.Annotations.cs'
    '..\..\..\src\WinShot.App\Editor\EditorDocument.cs|WinShot.EditorOutput.Measured.EditorDocument.cs'
    '..\..\..\src\WinShot.App\Editor\EditorResultPreparationCache.cs|WinShot.EditorOutput.Measured.EditorResultPreparationCache.cs'
    '..\..\..\src\WinShot.App\Editor\EditorResultPreparationCoordinator.cs|WinShot.EditorOutput.Measured.EditorResultPreparationCoordinator.cs'
    '..\..\..\src\WinShot.App\Editor\PixelFilters.cs|WinShot.EditorOutput.Measured.PixelFilters.cs'
    '..\..\..\src\WinShot.App\Editor\RegionEffectProcessing.cs|WinShot.EditorOutput.Measured.RegionEffectProcessing.cs'
    '..\..\..\src\WinShot.App\EncodedImagePayload.cs|WinShot.EditorOutput.Measured.EncodedImagePayload.cs'
    '..\..\..\src\WinShot.App\ImageBeautifier.cs|WinShot.EditorOutput.Measured.ImageBeautifier.cs'
    '..\..\..\src\WinShot.App\ImageSaveService.cs|WinShot.EditorOutput.Measured.ImageSaveService.cs'
    '..\..\..\src\WinShot.App\StillImageEncodingService.cs|WinShot.EditorOutput.Measured.StillImageEncodingService.cs'
    '..\..\..\src\WinShot.Core\Naming\FileNameTemplate.cs|WinShot.EditorOutput.Measured.FileNameTemplate.cs'
    '..\..\..\src\WinShot.Core\Naming\OutputFileReservation.cs|WinShot.EditorOutput.Measured.OutputFileReservation.cs'
    '..\..\..\src\WinShot.Core\Settings\AppSettings.cs|WinShot.EditorOutput.Measured.AppSettings.cs'
) | Sort-Object
$compileSources = @(
    $projectXml.SelectNodes('//Compile[@Include]') |
        ForEach-Object {
            "$($_.GetAttribute('Include'))|$($_.GetAttribute('Link'))"
        }
) | Sort-Object
$embeddedSources = @(
    $projectXml.SelectNodes('//EmbeddedResource[@Include]') |
        Where-Object {
            $_.GetAttribute('LogicalName').StartsWith(
                'WinShot.EditorOutput.Measured.',
                [StringComparison]::Ordinal)
        } |
        ForEach-Object {
            "$($_.GetAttribute('Include'))|$($_.GetAttribute('LogicalName'))"
        }
) | Sort-Object
if (($compileSources -join [char]0) -cne
        ($expectedCompileSources -join [char]0) -or
    ($embeddedSources -join [char]0) -cne
        ($expectedEmbeddedSources -join [char]0)) {
    throw 'The compile or embedded measured-source inventory changed.'
}

$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $PSCommandPath,
    [ref]$tokens,
    [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) {
    throw 'The editor-output harness wrapper did not parse cleanly.'
}

& $DotnetPath restore $project
if ($LASTEXITCODE -ne 0) {
    throw "The editor-output harness restore failed with exit code $LASTEXITCODE."
}

& $DotnetPath build `
    $project `
    -c Release `
    --no-restore `
    -p:Platform=x64 `
    -p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) {
    throw "The editor-output harness build failed with exit code $LASTEXITCODE."
}

& $DotnetPath run `
    --project $project `
    -c Release `
    --no-build `
    --no-restore `
    -p:Platform=x64 `
    -- `
    --self-test
if ($LASTEXITCODE -ne 0) {
    throw "The editor-output performance harness self-test failed with exit code $LASTEXITCODE."
}
