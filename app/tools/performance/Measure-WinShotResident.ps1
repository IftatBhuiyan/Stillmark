[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExecutablePath,

    [ValidateSet('Smoke', 'Soak')]
    [string]$Mode = 'Smoke',

    [ValidateSet('LocalDebug', 'InstalledRelease')]
    [string]$EvidenceClass = 'LocalDebug',

    [ValidateRange(0, 86400)]
    [double]$DurationSeconds = 0,

    [ValidateRange(0, 300)]
    [double]$IntervalSeconds = 0,

    [ValidateRange(-1, 86399)]
    [double]$WarmupSeconds = -1,

    [ValidateRange(1, 100000)]
    [int]$AnalysisWindowSamples = 3,

    [ValidateRange(2, 100000)]
    [int]$MinimumAnalysisSamples = 6,

    [ValidateRange(-1, 1048576)]
    [double]$MaxPrivateGrowthMiB = -1,

    [ValidateRange(-1, 1048576)]
    [double]$MaxWorkingSetGrowthMiB = -1,

    [ValidateRange(-1, 1000000)]
    [int]$MaxHandleGrowth = -1,

    [ValidateRange(-1, 1000000)]
    [int]$MaxThreadGrowth = -1,

    [ValidateRange(-1, 1000000)]
    [int]$MaxGdiGrowth = -1,

    [ValidateRange(-1, 1000000)]
    [int]$MaxUserGrowth = -1,

    [ValidateRange(-1, 100)]
    [double]$MaxAverageCpuPercent = -1,

    [ValidateRange(-1, 1000000)]
    [int]$MaxUnresponsiveSamples = -1,

    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'WinShotPerformanceEvidence')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Performance.Common.ps1')

if ($Mode -eq 'Smoke') {
    if ($DurationSeconds -eq 0) { $DurationSeconds = 12 }
    if ($IntervalSeconds -eq 0) { $IntervalSeconds = 1 }
    if ($WarmupSeconds -lt 0) { $WarmupSeconds = 3 }
    if ($MaxPrivateGrowthMiB -lt 0) { $MaxPrivateGrowthMiB = 32 }
    if ($MaxWorkingSetGrowthMiB -lt 0) { $MaxWorkingSetGrowthMiB = 64 }
    if ($MaxHandleGrowth -lt 0) { $MaxHandleGrowth = 32 }
    if ($MaxThreadGrowth -lt 0) { $MaxThreadGrowth = 8 }
    if ($MaxGdiGrowth -lt 0) { $MaxGdiGrowth = 16 }
    if ($MaxUserGrowth -lt 0) { $MaxUserGrowth = 16 }
    if ($MaxAverageCpuPercent -lt 0) { $MaxAverageCpuPercent = 10 }
    if ($MaxUnresponsiveSamples -lt 0) { $MaxUnresponsiveSamples = 0 }
}
else {
    if ($DurationSeconds -eq 0) { $DurationSeconds = 1800 }
    if ($IntervalSeconds -eq 0) { $IntervalSeconds = 5 }
    if ($WarmupSeconds -lt 0) { $WarmupSeconds = 60 }
    if ($MaxPrivateGrowthMiB -lt 0) { $MaxPrivateGrowthMiB = 64 }
    if ($MaxWorkingSetGrowthMiB -lt 0) { $MaxWorkingSetGrowthMiB = 96 }
    if ($MaxHandleGrowth -lt 0) { $MaxHandleGrowth = 64 }
    if ($MaxThreadGrowth -lt 0) { $MaxThreadGrowth = 16 }
    if ($MaxGdiGrowth -lt 0) { $MaxGdiGrowth = 32 }
    if ($MaxUserGrowth -lt 0) { $MaxUserGrowth = 32 }
    if ($MaxAverageCpuPercent -lt 0) { $MaxAverageCpuPercent = 10 }
    if ($MaxUnresponsiveSamples -lt 0) { $MaxUnresponsiveSamples = 0 }
}

if ($IntervalSeconds -lt 0.25) {
    throw 'IntervalSeconds must be at least 0.25 seconds.'
}

if ($WarmupSeconds -ge $DurationSeconds) {
    throw 'WarmupSeconds must be less than DurationSeconds.'
}

$availableMeasurementSamples = [Math]::Floor(($DurationSeconds - $WarmupSeconds) / $IntervalSeconds)
$requiredMeasurementSamples = [Math]::Max($MinimumAnalysisSamples, $AnalysisWindowSamples * 2)
if ($availableMeasurementSamples -lt $requiredMeasurementSamples) {
    throw "The duration, warmup, and interval allow only $availableMeasurementSamples post-warmup samples; at least $requiredMeasurementSamples are required."
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
$normalizedExpectedPath = [IO.Path]::GetFullPath($resolvedExecutable)
if (-not [IO.File]::Exists($normalizedExpectedPath)) {
    throw 'The exact executable path does not exist.'
}

$targetProcess = Get-Process -Id $ProcessId -ErrorAction Stop
$normalizedActualPath = [IO.Path]::GetFullPath($targetProcess.MainModule.FileName)
if (-not [string]::Equals($normalizedExpectedPath, $normalizedActualPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The process executable does not match the exact requested path.'
}

$targetStartTimeUtc = $targetProcess.StartTime.ToUniversalTime()
$executableHash = (Get-FileHash -LiteralPath $normalizedExpectedPath -Algorithm SHA256).Hash.ToLowerInvariant()

if (-not ('WinShotPerformanceNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinShotPerformanceNative
{
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr processHandle, uint resourceType);
}
'@
}

$budgets = @{
    MaxPrivateBytesGrowth = [double]($MaxPrivateGrowthMiB * 1MB)
    MaxWorkingSetBytesGrowth = [double]($MaxWorkingSetGrowthMiB * 1MB)
    MaxHandleCountGrowth = [double]$MaxHandleGrowth
    MaxThreadCountGrowth = [double]$MaxThreadGrowth
    MaxGdiObjectCountGrowth = [double]$MaxGdiGrowth
    MaxUserObjectCountGrowth = [double]$MaxUserGrowth
    MaxAverageCpuPercent = [double]$MaxAverageCpuPercent
    MaxUnresponsiveSamples = [int]$MaxUnresponsiveSamples
}

$samples = [System.Collections.Generic.List[object]]::new()
$runFailureCode = $null
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
$previousElapsedSeconds = $null
$previousTotalCpuSeconds = $null
$sampleIndex = 0
$logicalProcessorCount = [Math]::Max(1, [Environment]::ProcessorCount)

while ($stopwatch.Elapsed.TotalSeconds -le $DurationSeconds) {
    $elapsedSeconds = $stopwatch.Elapsed.TotalSeconds

    try {
        if ($targetProcess.HasExited) {
            $runFailureCode = 'TargetExited'
            break
        }

        $targetProcess.Refresh()
        $actualStartTimeUtc = $targetProcess.StartTime.ToUniversalTime()
        if ($actualStartTimeUtc -ne $targetStartTimeUtc) {
            $runFailureCode = 'TargetIdentityChanged'
            break
        }

        $totalCpuSeconds = $targetProcess.TotalProcessorTime.TotalSeconds
        $cpuPercent = $null
        if ($null -ne $previousElapsedSeconds -and $elapsedSeconds -gt $previousElapsedSeconds) {
            $cpuDelta = $totalCpuSeconds - $previousTotalCpuSeconds
            $elapsedDelta = $elapsedSeconds - $previousElapsedSeconds
            $cpuPercent = [Math]::Max(0.0, ($cpuDelta / $elapsedDelta / $logicalProcessorCount) * 100.0)
        }

        $phase = if ($elapsedSeconds -lt $WarmupSeconds) { 'Warmup' } else { 'Measurement' }
        $samples.Add([pscustomobject][ordered]@{
            SampleIndex = $sampleIndex
            ElapsedSeconds = [Math]::Round($elapsedSeconds, 3)
            Phase = $phase
            CpuPercent = if ($null -eq $cpuPercent) { $null } else { [Math]::Round($cpuPercent, 4) }
            PrivateBytes = [long]$targetProcess.PrivateMemorySize64
            WorkingSetBytes = [long]$targetProcess.WorkingSet64
            HandleCount = [int]$targetProcess.HandleCount
            ThreadCount = [int]$targetProcess.Threads.Count
            GdiObjectCount = [int][WinShotPerformanceNative]::GetGuiResources($targetProcess.Handle, 0)
            UserObjectCount = [int][WinShotPerformanceNative]::GetGuiResources($targetProcess.Handle, 1)
            Responding = [bool]$targetProcess.Responding
        })

        $previousElapsedSeconds = $elapsedSeconds
        $previousTotalCpuSeconds = $totalCpuSeconds
        $sampleIndex++
    }
    catch {
        $runFailureCode = 'SampleFailed'
        break
    }

    $nextSampleAt = $sampleIndex * $IntervalSeconds
    while ($stopwatch.Elapsed.TotalSeconds -lt $nextSampleAt -and $stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $remainingMilliseconds = [int][Math]::Ceiling(
            [Math]::Min(
                250.0,
                ($nextSampleAt - $stopwatch.Elapsed.TotalSeconds) * 1000.0
            )
        )
        if ($remainingMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $remainingMilliseconds
        }
    }
}

$stopwatch.Stop()

$analysis = Test-WinShotResidentBudgets `
    -Samples @($samples) `
    -WindowSize $AnalysisWindowSamples `
    -MinimumAnalysisSamples $MinimumAnalysisSamples `
    -Budgets $budgets

$overallPassed = ($null -eq $runFailureCode -and $analysis.OverallPassed)
$endedUtc = [DateTime]::UtcNow
$startedUtc = $endedUtc.AddSeconds(-$stopwatch.Elapsed.TotalSeconds)
$artifactStem = 'winshot-resident-{0}-{1}-{2}' -f $Mode.ToLowerInvariant(), $startedUtc.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$jsonPath = Join-Path $OutputDirectory ($artifactStem + '.json')
$csvPath = Join-Path $OutputDirectory ($artifactStem + '.csv')

$jsonDocument = [pscustomobject][ordered]@{
    SchemaVersion = 1
    EvidenceKind = 'WinShotResidentResourceMeasurement'
    EvidenceClass = $EvidenceClass
    Mode = $Mode
    StartedUtc = $startedUtc.ToString('o')
    EndedUtc = $endedUtc.ToString('o')
    RequestedDurationSeconds = $DurationSeconds
    ActualDurationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
    IntervalSeconds = $IntervalSeconds
    WarmupSeconds = $WarmupSeconds
    Target = [pscustomobject][ordered]@{
        ProcessId = $ProcessId
        ExecutableName = [IO.Path]::GetFileName($normalizedExpectedPath)
        ExecutableFileSha256 = $executableHash
    }
    Environment = [pscustomobject][ordered]@{
        LogicalProcessorCount = $logicalProcessorCount
        OperatingSystemVersion = [Environment]::OSVersion.Version.ToString()
        Is64BitOperatingSystem = [Environment]::Is64BitOperatingSystem
        Is64BitProcess = [Environment]::Is64BitProcess
    }
    Analysis = [pscustomobject][ordered]@{
        Method = 'FirstAndLastPostWarmupMedianWindows'
        WindowSize = $analysis.WindowSize
        RequiredSampleCount = $analysis.RequiredSampleCount
        MeasurementSampleCount = $analysis.MeasurementSampleCount
        SufficientData = $analysis.SufficientData
        Checks = $analysis.Checks
    }
    SampleCount = $samples.Count
    RunFailureCode = $runFailureCode
    OverallPassed = $overallPassed
    Samples = @($samples)
}

$utf8NoBom = [Text.UTF8Encoding]::new($false)
$jsonText = $jsonDocument | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($jsonPath, $jsonText, $utf8NoBom)

$csvLines = [System.Collections.Generic.List[string]]::new()
$csvLines.Add('SampleIndex,ElapsedSeconds,Phase,CpuPercent,PrivateBytes,WorkingSetBytes,HandleCount,ThreadCount,GdiObjectCount,UserObjectCount,Responding')
foreach ($sample in $samples) {
    $cpuText = if ($null -eq $sample.CpuPercent) {
        ''
    }
    else {
        ([double]$sample.CpuPercent).ToString('0.####', [Globalization.CultureInfo]::InvariantCulture)
    }
    $csvLines.Add([string]::Join(',', @(
        ([int]$sample.SampleIndex).ToString([Globalization.CultureInfo]::InvariantCulture),
        ([double]$sample.ElapsedSeconds).ToString('0.###', [Globalization.CultureInfo]::InvariantCulture),
        $sample.Phase,
        $cpuText,
        ([long]$sample.PrivateBytes).ToString([Globalization.CultureInfo]::InvariantCulture),
        ([long]$sample.WorkingSetBytes).ToString([Globalization.CultureInfo]::InvariantCulture),
        ([int]$sample.HandleCount).ToString([Globalization.CultureInfo]::InvariantCulture),
        ([int]$sample.ThreadCount).ToString([Globalization.CultureInfo]::InvariantCulture),
        ([int]$sample.GdiObjectCount).ToString([Globalization.CultureInfo]::InvariantCulture),
        ([int]$sample.UserObjectCount).ToString([Globalization.CultureInfo]::InvariantCulture),
        ([bool]$sample.Responding).ToString().ToLowerInvariant()
    )))
}
[IO.File]::WriteAllLines($csvPath, $csvLines, $utf8NoBom)

[pscustomobject][ordered]@{
    OverallPassed = $overallPassed
    RunFailureCode = $runFailureCode
    SampleCount = $samples.Count
    JsonFileName = [IO.Path]::GetFileName($jsonPath)
    CsvFileName = [IO.Path]::GetFileName($csvPath)
}

if (-not $overallPassed) {
    exit 2
}
