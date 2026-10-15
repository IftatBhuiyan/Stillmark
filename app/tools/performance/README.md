# WinShot resident-process performance evidence

The sampler observes an already-running WinShot process by both exact PID and exact executable path. It is read-only: it does not launch, stop, restart, or otherwise control the target process. A separate bounded workload script can repeatedly signal and close only the resident dashboard for explicit local lifecycle testing.

It records bounded-interval samples for CPU, private bytes, working set, handles, threads, GDI objects, USER objects, and UI responsiveness. JSON and CSV artifacts exclude executable paths, command lines, user names, machine names, window titles, and captured content.

## Validate the harness

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\performance\Test-PerformanceHarness.ps1
```

This combined gate validates the resident sampler/workload contracts and runs both isolated
source-bound WPF self-tests: `region-effects` and `editor-output`. It resolves only the exact
repository-pinned .NET SDK from the repository's `global.json`. The editor-output tool also has a standalone README with
commands for its synthetic 1080p/4K timing and Dispatcher-heartbeat modes.

## Short manual smoke

First resolve the exact resident process yourself. Do not select by process name alone:

```powershell
$exe = 'C:\exact\path\to\WinShot.exe'
$process = Get-Process -Name WinShot |
    Where-Object { $_.MainModule.FileName -eq $exe } |
    Select-Object -Single 1

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\performance\Measure-WinShotResident.ps1 `
    -ProcessId $process.Id `
    -ExecutablePath $exe `
    -Mode Smoke `
    -EvidenceClass LocalDebug `
    -OutputDirectory "$env:TEMP\WinShotPerformanceEvidence"
```

The smoke preset samples for 12 seconds at one-second intervals, excludes the first three seconds as warmup, and uses median baseline/tail windows so one transient sample does not create a false growth failure.

## Longer soak

Run this against the installed Release executable under a standard-user session:

```powershell
$exe = 'C:\exact\installed\path\WinShot.exe'
$process = Get-Process -Name WinShot |
    Where-Object { $_.MainModule.FileName -eq $exe } |
    Select-Object -Single 1

powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\performance\Measure-WinShotResident.ps1 `
    -ProcessId $process.Id `
    -ExecutablePath $exe `
    -Mode Soak `
    -EvidenceClass InstalledRelease `
    -DurationSeconds 3600 `
    -IntervalSeconds 5 `
    -WarmupSeconds 60 `
    -OutputDirectory "$env:TEMP\WinShotPerformanceEvidence"
```

The soak preset defaults to 30 minutes if `DurationSeconds` is omitted. Duration is bounded to 24 hours and interval to at least 250 milliseconds.

## Repeated dashboard open/close workload

Resolve one exact resident process and capture its start-time ticks before invoking the workload:

```powershell
$exe = 'C:\exact\path\to\WinShot.exe'
$resident = Get-Process -Name WinShot |
    Where-Object { $_.MainModule.FileName -eq $exe } |
    Select-Object -Single 1

powershell -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\performance\Invoke-WinShotDashboardWorkload.ps1 `
    -ProcessId $resident.Id `
    -ExecutablePath $exe `
    -StartTimeUtcTicks $resident.StartTime.ToUniversalTime().Ticks `
    -CycleCount 10
```

Begin with the dashboard closed. Every cycle revalidates the resident PID, executable path,
start time, and responsiveness; launches the exact executable as a distinct single-instance
helper; requires that helper to exit; verifies the resulting nonzero visible window handle belongs
to the resident PID; posts `WM_CLOSE`; and proves that exact window becomes hidden-to-tray while
the same resident remains responsive. Cycle count, per-step timeouts, polling, and total duration
are bounded.

The script emits one compact JSON summary containing only cycle counts, aggregate helper/open/close
timings, a stable failure code, and pass/fail. It does not emit paths, process IDs, start times,
window handles, titles, user or machine names, command lines, or captured content. It never
terminates a process and does not touch the registry, settings, capture actions, or files.

## Budgets

The defaults are conservative local gates, not universal product claims. Override them for a declared workload:

```powershell
    -MaxPrivateGrowthMiB 64 `
    -MaxWorkingSetGrowthMiB 96 `
    -MaxHandleGrowth 64 `
    -MaxThreadGrowth 16 `
    -MaxGdiGrowth 32 `
    -MaxUserGrowth 32 `
    -MaxAverageCpuPercent 10 `
    -MaxUnresponsiveSamples 0
```

Growth checks compare the median of the first post-warmup window with the median of the final post-warmup window. Peak values remain in the samples for diagnosis but are not used as flaky pass/fail gates. Too few post-warmup samples fail rather than producing weak evidence. Exit code `0` means all budgets passed; exit code `2` means a budget, target-liveness, or sample-integrity gate failed.

## Evidence limits

- `LocalDebug` output is local engineering evidence only.
- Commercial-release promotion requires an installed, signed Release build measured in a standard-user session.
- Run separate idle, capture, recording, and repeated-open/close workloads. The sampler does not
  generate workload; the dashboard script generates only its explicitly bounded dashboard cycle.
- A passing short smoke does not prove long-term leak freedom. Use the longer soak and inspect trends.
- This harness does not collect crash dumps, ETW traces, GPU metrics, disk I/O, network activity, command lines, or capture content.
- The exact path is used for identity validation but is deliberately omitted from artifacts. The artifact includes the executable file SHA-256 instead.
