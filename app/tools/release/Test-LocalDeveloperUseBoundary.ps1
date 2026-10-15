[CmdletBinding()]
param(
    [string]$DotnetPath = (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet-winshot-10.0.110\dotnet.exe")
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = Join-Path $repositoryRoot 'src\WinShot.App\WinShot.App.csproj'
$testProjectPath = Join-Path $repositoryRoot 'tests\WinShot.Core.Tests\WinShot.Core.Tests.csproj'
$debugAssemblyPath = Join-Path $repositoryRoot (
    'src\WinShot.App\bin\x64\Debug\net10.0-windows10.0.19041.0\WinShot.dll')
$developerMarker = 'WINSHOT_LOCAL_DEVELOPER_USE_ENTITLEMENT_V1'
$dotnet = [System.IO.Path]::GetFullPath($DotnetPath)
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned dotnet host '$dotnet' is unavailable."
}

function Invoke-BoundaryTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Configuration,
        [string[]]$AdditionalArguments = @()
    )

    $arguments = @(
        'msbuild',
        $projectPath,
        '-nologo',
        '-t:ValidateLocalDeveloperUseBoundary',
        "-p:Configuration=$Configuration",
        '-p:Platform=x64'
    ) + $AdditionalArguments
    [string[]]$output = @(& $dotnet @arguments 2>&1 | ForEach-Object { [string]$_ })
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-Pass {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][object]$Result
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Name unexpectedly failed."
    }
    Write-Output "PASS: $Name"
}

function Assert-Refusal {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][object]$Result,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    if ($Result.ExitCode -eq 0) {
        throw "$Name unexpectedly passed."
    }
    [string]$joined = $Result.Output -join [Environment]::NewLine
    if (-not $joined.Contains($ExpectedMessage)) {
        throw "$Name failed outside the intended boundary."
    }
    Write-Output "PASS: $Name refused"
}

function Invoke-FocusedDebugTests {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$DeveloperUse
    )

    $arguments = @(
        'test',
        $testProjectPath,
        '--configuration',
        'Debug',
        '--no-restore',
        '--filter',
        'FullyQualifiedName~LocalDeveloperUsePolicyTests',
        '-p:Platform=x64',
        '-p:TreatWarningsAsErrors=true',
        "-p:WinShotLocalDeveloperUse=$($DeveloperUse.ToString().ToLowerInvariant())"
    )
    [string[]]$output = @(& $dotnet @arguments 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        throw "$Name focused tests failed."
    }
    if (-not (Test-Path -LiteralPath $debugAssemblyPath -PathType Leaf)) {
        throw "$Name did not produce the expected Debug application assembly."
    }
    Write-Output "PASS: $Name focused tests"
}

function Test-DeveloperMarker {
    $bytes = [System.IO.File]::ReadAllBytes($debugAssemblyPath)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    $unicode = [System.Text.Encoding]::Unicode.GetString($bytes)
    $unicodeShifted = if ($bytes.Length -gt 1) {
        [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)
    }
    else {
        ''
    }
    return $ascii.Contains($developerMarker) -or
        $unicode.Contains($developerMarker) -or
        $unicodeShifted.Contains($developerMarker)
}

Assert-Pass -Name 'Debug default developer entitlement' -Result (
    Invoke-BoundaryTarget -Configuration 'Debug')
Assert-Pass -Name 'Debug explicit licensing opt-out' -Result (
    Invoke-BoundaryTarget -Configuration 'Debug' -AdditionalArguments @(
        '-p:WinShotLocalDeveloperUse=false'))
Assert-Pass -Name 'Release default customer licensing' -Result (
    Invoke-BoundaryTarget -Configuration 'Release')
Assert-Refusal `
    -Name 'Release developer property injection' `
    -Result (Invoke-BoundaryTarget -Configuration 'Release' -AdditionalArguments @(
        '-p:WinShotLocalDeveloperUse=true')) `
    -ExpectedMessage 'Local developer entitlement is permitted only in the exact Debug configuration.'
Assert-Refusal `
    -Name 'Release developer symbol injection' `
    -Result (Invoke-BoundaryTarget -Configuration 'Release' -AdditionalArguments @(
        '-p:DefineConstants=WINSHOT_LOCAL_DEVELOPER_USE')) `
    -ExpectedMessage 'The local developer entitlement symbol is forbidden outside the exact Debug configuration.'

Invoke-FocusedDebugTests `
    -Name 'Debug explicit licensing opt-out' `
    -DeveloperUse $false
if (Test-DeveloperMarker) {
    throw 'Debug licensing opt-out assembly contains the developer entitlement marker.'
}
Write-Output 'PASS: Debug explicit licensing opt-out marker absent'

# Build the developer-enabled configuration last so the local output is immediately usable.
Invoke-FocusedDebugTests `
    -Name 'Debug default developer entitlement' `
    -DeveloperUse $true
if (-not (Test-DeveloperMarker)) {
    throw 'Debug developer assembly does not contain the entitlement marker.'
}
Write-Output 'PASS: Debug default developer entitlement marker present'

Write-Output 'Local developer entitlement build boundary passed.'
