[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$sourceDirectory = Join-Path $repositoryRoot 'src\WinShot.Launcher'
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts\launcher'))
$artifactsPrefix = $artifactsRoot.TrimEnd('\') + '\'
if (-not $outputRoot.StartsWith(
        $artifactsPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Launcher output must be a child of '$artifactsRoot'."
}

$dotnet = [System.IO.Path]::GetFullPath($DotnetPath)
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned dotnet host '$dotnet' does not exist."
}
$sdkVersion = (& $dotnet --version).Trim()
$sdkConfiguration = Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw |
    ConvertFrom-Json
$expectedSdkVersion = [string]$sdkConfiguration.sdk.version
if ($sdkVersion -cne $expectedSdkVersion) {
    throw "Launcher build requires repository-pinned SDK $expectedSdkVersion; actual '$sdkVersion'."
}

$sdkRoot = Join-Path (Split-Path -Parent $dotnet) "sdk\$sdkVersion"
$compiler = Join-Path $sdkRoot 'Roslyn\bincore\csc.dll'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "Pinned Roslyn compiler '$compiler' is unavailable."
}

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    (Join-Path $frameworkRoot 'mscorlib.dll'),
    (Join-Path $frameworkRoot 'System.dll'),
    (Join-Path $frameworkRoot 'System.Core.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) {
        throw "Required in-box framework reference '$reference' is unavailable."
    }
}

$sources = @(
    'LauncherIdentity.cs',
    'LauncherContracts.cs',
    'LauncherRuntime.cs',
    'LauncherReleaseAttestationVerifier.cs',
    'WindowsLauncherTrustVerifier.cs',
    'Program.cs',
    'AssemblyInfo.cs'
) | ForEach-Object {
    $path = Join-Path $sourceDirectory $_
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Launcher source '$path' is unavailable."
    }
    $path
}
$manifestPath = Join-Path $sourceDirectory 'app.manifest'
$iconPath = Join-Path $repositoryRoot 'src\WinShot.App\Assets\winshot.ico'
foreach ($inputPath in @($manifestPath, $iconPath)) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "Launcher build input '$inputPath' is unavailable."
    }
}

if (Test-Path -LiteralPath $outputRoot) {
    if ((Get-ChildItem -LiteralPath $outputRoot -Force | Measure-Object).Count -ne 0) {
        throw "Launcher output '$outputRoot' must not already contain files."
    }
}
else {
    New-Item -ItemType Directory -Path $outputRoot | Out-Null
}

$workRoot = Join-Path $artifactsRoot ('.work-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null

function Invoke-LauncherCompile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination | Out-Null
    $target = Join-Path $Destination 'WinShot.exe'
    $arguments = @(
        $compiler,
        '/noconfig',
        '/nostdlib+',
        '/target:winexe',
        '/platform:x64',
        '/optimize+',
        '/debug-',
        '/deterministic+',
        '/warnaserror+',
        '/langversion:5',
        '/utf8output',
        "/pathmap:$repositoryRoot=/_/",
        "/win32manifest:$manifestPath",
        "/win32icon:$iconPath",
        "/out:$target"
    )
    foreach ($reference in $references) {
        $arguments += "/reference:$reference"
    }
    $arguments += $sources

    $compilerOutput = @(& $dotnet @arguments 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
        $compilerOutput | ForEach-Object { Write-Host $_ }
        throw "Stable-launcher compilation failed with exit code $LASTEXITCODE."
    }
    return $target
}

try {
    $firstPath = Invoke-LauncherCompile -Destination (Join-Path $workRoot 'first')
    $secondPath = Invoke-LauncherCompile -Destination (Join-Path $workRoot 'second')
    $firstHash = (Get-FileHash -LiteralPath $firstPath -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath $secondPath -Algorithm SHA256).Hash
    if ($firstHash -cne $secondHash) {
        throw "Two isolated stable-launcher builds were not byte-identical."
    }

    $finalPath = Join-Path $outputRoot 'WinShot.exe'
    [System.IO.File]::Copy($firstPath, $finalPath, $false)
    $inputPaths = @($sources) + @($manifestPath, $iconPath, $compiler, $dotnet) + $references
    $inputs = @(
        foreach ($inputPath in $inputPaths) {
            [ordered]@{
                path = if ($inputPath.StartsWith(
                    $repositoryRoot + '\',
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                    $inputPath.Substring($repositoryRoot.Length + 1).Replace('\', '/')
                }
                else {
                    [System.IO.Path]::GetFileName($inputPath)
                }
                sha256 = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
            }
        }
    )
    $metadata = [ordered]@{
        schemaVersion = 1
        artifactKind = 'UNSIGNED_STABLE_LAUNCHER_ENGINEERING'
        packageId = 'PROVISIONAL.WinShot'
        version = '1.0.0'
        architecture = 'x64'
        subsystem = 'WindowsGui'
        clrContract = 'InBoxNetFramework4Clr'
        minimumWindowsContract = 'Windows10Version1809WithNetFramework472'
        sdkVersion = $sdkVersion
        launcherSha256 = $firstHash
        deterministicRebuildSha256 = $secondHash
        inputs = $inputs
    }
    $metadataPath = Join-Path $outputRoot 'launcher-build.json'
    $metadataJson = $metadata | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $metadataPath,
        $metadataJson + "`n",
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText(
        (Join-Path $outputRoot 'SHA256SUMS'),
        "$firstHash  WinShot.exe`n",
        [System.Text.UTF8Encoding]::new($false))
}
finally {
    $resolvedWorkRoot = [System.IO.Path]::GetFullPath($workRoot)
    if (-not $resolvedWorkRoot.StartsWith(
            $artifactsPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean launcher work outside '$artifactsRoot'."
    }
    if (Test-Path -LiteralPath $resolvedWorkRoot) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}

& (Join-Path $PSScriptRoot 'Test-StableLauncher.ps1') `
    -ArtifactDirectory $outputRoot `
    -Mode Authoring `
    -DotnetPath $dotnet

Write-Host "Stable launcher built and validated: $outputRoot"
