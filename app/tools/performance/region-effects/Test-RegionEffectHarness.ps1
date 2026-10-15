[CmdletBinding()]
param(
    [string]$DotnetPath
)

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
    throw "The region-effect harness requires exact repository-pinned SDK $expectedSdkVersion; found '$sdkVersion'."
}

$project = Join-Path $PSScriptRoot 'WinShot.RegionEffects.Perf.csproj'
& $DotnetPath run `
    --project $project `
    -c Release `
    -p:TreatWarningsAsErrors=true `
    -- `
    --self-test
if ($LASTEXITCODE -ne 0) {
    throw "The region-effect performance harness self-test failed with exit code $LASTEXITCODE."
}
