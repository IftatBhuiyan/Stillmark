[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 9223372036854775807)]
    [long]$StartTimeUtcTicks,

    [ValidateRange(1, 100)]
    [int]$CycleCount = 10,

    [ValidateRange(250, 10000)]
    [int]$HelperExitTimeoutMilliseconds = 5000,

    [ValidateRange(250, 10000)]
    [int]$WindowOpenTimeoutMilliseconds = 5000,

    [ValidateRange(250, 10000)]
    [int]$WindowCloseTimeoutMilliseconds = 5000,

    [ValidateRange(10, 500)]
    [int]$PollIntervalMilliseconds = 50,

    [ValidateRange(0, 2000)]
    [int]$InterCycleDelayMilliseconds = 100,

    [ValidateRange(1, 3600)]
    [int]$OverallTimeoutSeconds = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Performance.Common.ps1')

if (-not ('WinShotDashboardWorkloadNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WinShotDashboardWorkloadNative
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
'@
}

$wmClose = 0x0010
$completedCycles = 0
$failureCode = $null
$helperExitLatencies = [System.Collections.Generic.List[double]]::new()
$windowOpenLatencies = [System.Collections.Generic.List[double]]::new()
$windowCloseLatencies = [System.Collections.Generic.List[double]]::new()
$workloadClock = [Diagnostics.Stopwatch]::StartNew()
$normalizedExpectedPath = $null

function Stop-WinShotDashboardWorkload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Code
    )

    $exception = [InvalidOperationException]::new('Dashboard workload gate failed.')
    $exception.Data['WinShotFailureCode'] = $Code
    throw $exception
}

function Assert-WinShotWorkloadDeadline {
    if ($workloadClock.Elapsed.TotalSeconds -ge $OverallTimeoutSeconds) {
        Stop-WinShotDashboardWorkload -Code 'OverallTimeout'
    }
}

function Get-ExactWinShotResident {
    $resident = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $resident -or $resident.HasExited) {
        Stop-WinShotDashboardWorkload -Code 'ResidentExited'
    }

    try {
        $resident.Refresh()
        $actualPath = [IO.Path]::GetFullPath($resident.MainModule.FileName)
        if (-not [string]::Equals(
                $normalizedExpectedPath,
                $actualPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            Stop-WinShotDashboardWorkload -Code 'ResidentPathMismatch'
        }

        if ($resident.StartTime.ToUniversalTime().Ticks -ne $StartTimeUtcTicks) {
            Stop-WinShotDashboardWorkload -Code 'ResidentStartTimeMismatch'
        }

        if (-not $resident.Responding) {
            Stop-WinShotDashboardWorkload -Code 'ResidentUnresponsive'
        }
    }
    catch {
        if ($_.Exception.Data.Contains('WinShotFailureCode')) {
            throw
        }
        Stop-WinShotDashboardWorkload -Code 'ResidentIdentityUnavailable'
    }

    return $resident
}

function Test-WindowOwnedByResident {
    param(
        [Parameter(Mandatory = $true)]
        [IntPtr]$WindowHandle
    )

    if ($WindowHandle -eq [IntPtr]::Zero -or
        -not [WinShotDashboardWorkloadNative]::IsWindow($WindowHandle)) {
        return $false
    }

    [uint32]$ownerProcessId = 0
    [void][WinShotDashboardWorkloadNative]::GetWindowThreadProcessId(
        $WindowHandle,
        [ref]$ownerProcessId)
    return $ownerProcessId -eq [uint32]$ProcessId
}

function Get-AggregateLatency {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[double]]$Values
    )

    if ($Values.Count -eq 0) {
        return $null
    }

    [double[]]$samples = @($Values)
    return [pscustomobject][ordered]@{
        MinimumMilliseconds = [Math]::Round(($samples | Measure-Object -Minimum).Minimum, 3)
        MedianMilliseconds = [Math]::Round((Get-WinShotMedian -Values $samples), 3)
        MaximumMilliseconds = [Math]::Round(($samples | Measure-Object -Maximum).Maximum, 3)
    }
}

try {
    $resolvedExecutable = Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop
    if ($resolvedExecutable.Provider.Name -ne 'FileSystem') {
        Stop-WinShotDashboardWorkload -Code 'ExecutableNotFileSystem'
    }

    $normalizedExpectedPath = [IO.Path]::GetFullPath($resolvedExecutable.ProviderPath)
    if (-not [IO.File]::Exists($normalizedExpectedPath) -or
        -not [IO.Path]::GetExtension($normalizedExpectedPath).Equals(
            '.exe',
            [StringComparison]::OrdinalIgnoreCase)) {
        Stop-WinShotDashboardWorkload -Code 'ExecutableInvalid'
    }

    $initialResident = Get-ExactWinShotResident
    $initialHandle = [IntPtr]$initialResident.MainWindowHandle
    if ($initialHandle -ne [IntPtr]::Zero -and
        [WinShotDashboardWorkloadNative]::IsWindow($initialHandle) -and
        [WinShotDashboardWorkloadNative]::IsWindowVisible($initialHandle)) {
        $initialWindowFailure = if (Test-WindowOwnedByResident -WindowHandle $initialHandle) {
            'ResidentWindowAlreadyOpen'
        }
        else {
            'ResidentWindowOwnerMismatch'
        }
        Stop-WinShotDashboardWorkload -Code $initialWindowFailure
    }

    for ($cycle = 0; $cycle -lt $CycleCount; $cycle++) {
        Assert-WinShotWorkloadDeadline
        $resident = Get-ExactWinShotResident
        $residentHandle = [IntPtr]$resident.MainWindowHandle
        if ($residentHandle -ne [IntPtr]::Zero -and
            [WinShotDashboardWorkloadNative]::IsWindow($residentHandle) -and
            [WinShotDashboardWorkloadNative]::IsWindowVisible($residentHandle)) {
            $cycleWindowFailure = if (Test-WindowOwnedByResident -WindowHandle $residentHandle) {
                'ResidentWindowAlreadyOpen'
            }
            else {
                'ResidentWindowOwnerMismatch'
            }
            Stop-WinShotDashboardWorkload -Code $cycleWindowFailure
        }

        $helperClock = [Diagnostics.Stopwatch]::StartNew()
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $normalizedExpectedPath
        $startInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($normalizedExpectedPath)
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $helper = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $helper -or $helper.Id -eq $ProcessId) {
            Stop-WinShotDashboardWorkload -Code 'HelperLaunchFailed'
        }

        try {
            if (-not $helper.WaitForExit($HelperExitTimeoutMilliseconds)) {
                Stop-WinShotDashboardWorkload -Code 'HelperExitTimeout'
            }
            if ($helper.ExitCode -ne 0) {
                Stop-WinShotDashboardWorkload -Code 'HelperExitNonZero'
            }
        }
        finally {
            $helperClock.Stop()
            $helper.Dispose()
        }
        $helperExitLatencies.Add($helperClock.Elapsed.TotalMilliseconds)

        $windowOpenClock = [Diagnostics.Stopwatch]::StartNew()
        $windowHandle = [IntPtr]::Zero
        while ($windowOpenClock.ElapsedMilliseconds -lt $WindowOpenTimeoutMilliseconds) {
            Assert-WinShotWorkloadDeadline
            $resident = Get-ExactWinShotResident
            $candidateHandle = [IntPtr]$resident.MainWindowHandle
            if ($candidateHandle -ne [IntPtr]::Zero -and
                [WinShotDashboardWorkloadNative]::IsWindowVisible($candidateHandle)) {
                if (-not (Test-WindowOwnedByResident -WindowHandle $candidateHandle)) {
                    Stop-WinShotDashboardWorkload -Code 'ResidentWindowOwnerMismatch'
                }
                $windowHandle = $candidateHandle
                break
            }
            Start-Sleep -Milliseconds $PollIntervalMilliseconds
        }
        $windowOpenClock.Stop()
        if ($windowHandle -eq [IntPtr]::Zero) {
            Stop-WinShotDashboardWorkload -Code 'WindowOpenTimeout'
        }
        $windowOpenLatencies.Add($windowOpenClock.Elapsed.TotalMilliseconds)

        $resident = Get-ExactWinShotResident
        if (-not (Test-WindowOwnedByResident -WindowHandle $windowHandle)) {
            Stop-WinShotDashboardWorkload -Code 'ResidentWindowOwnerMismatch'
        }

        $windowCloseClock = [Diagnostics.Stopwatch]::StartNew()
        if (-not [WinShotDashboardWorkloadNative]::PostMessage(
                $windowHandle,
                $wmClose,
                [IntPtr]::Zero,
                [IntPtr]::Zero)) {
            Stop-WinShotDashboardWorkload -Code 'WindowClosePostFailed'
        }

        $windowClosed = $false
        while ($windowCloseClock.ElapsedMilliseconds -lt $WindowCloseTimeoutMilliseconds) {
            Assert-WinShotWorkloadDeadline
            $resident = Get-ExactWinShotResident
            $currentHandle = [IntPtr]$resident.MainWindowHandle
            if (-not [WinShotDashboardWorkloadNative]::IsWindow($windowHandle) -or
                -not [WinShotDashboardWorkloadNative]::IsWindowVisible($windowHandle)) {
                if ($currentHandle -ne [IntPtr]::Zero -and
                    $currentHandle -ne $windowHandle -and
                    [WinShotDashboardWorkloadNative]::IsWindow($currentHandle) -and
                    [WinShotDashboardWorkloadNative]::IsWindowVisible($currentHandle)) {
                    Stop-WinShotDashboardWorkload -Code 'ResidentWindowHandleChanged'
                }
                $windowClosed = $true
                break
            }
            if ($currentHandle -ne [IntPtr]::Zero -and $currentHandle -ne $windowHandle) {
                Stop-WinShotDashboardWorkload -Code 'ResidentWindowHandleChanged'
            }
            Start-Sleep -Milliseconds $PollIntervalMilliseconds
        }
        $windowCloseClock.Stop()
        if (-not $windowClosed) {
            Stop-WinShotDashboardWorkload -Code 'WindowCloseTimeout'
        }
        $windowCloseLatencies.Add($windowCloseClock.Elapsed.TotalMilliseconds)

        [void](Get-ExactWinShotResident)
        $completedCycles++
        if ($cycle -lt ($CycleCount - 1) -and $InterCycleDelayMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $InterCycleDelayMilliseconds
        }
    }
}
catch {
    if ($_.Exception.Data.Contains('WinShotFailureCode')) {
        $failureCode = [string]$_.Exception.Data['WinShotFailureCode']
    }
    else {
        $failureCode = 'WorkloadFailed'
    }
}
finally {
    $workloadClock.Stop()
}

$overallPassed = $null -eq $failureCode -and $completedCycles -eq $CycleCount
$summary = [pscustomobject][ordered]@{
    SchemaVersion = 1
    EvidenceKind = 'WinShotRepeatedDashboardWorkload'
    RequestedCycleCount = $CycleCount
    CompletedCycleCount = $completedCycles
    ActualDurationMilliseconds = [Math]::Round($workloadClock.Elapsed.TotalMilliseconds, 3)
    HelperExitLatency = Get-AggregateLatency -Values $helperExitLatencies
    WindowOpenLatency = Get-AggregateLatency -Values $windowOpenLatencies
    WindowCloseLatency = Get-AggregateLatency -Values $windowCloseLatencies
    FailureCode = $failureCode
    OverallPassed = $overallPassed
}

$summary | ConvertTo-Json -Depth 4 -Compress
if (-not $overallPassed) {
    exit 2
}
