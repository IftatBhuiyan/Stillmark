# Region-effect CPU baseline

This isolated, package-free .NET 10 Windows harness measures the existing WinShot
`PixelFilters` implementation and records a separate WPF Dispatcher heartbeat while the real
`RegionEffectPipeline` prepares a deterministic blur. The project compiles and embeds the current
source files directly; it does not reference or modify the application project, solution, package
graph, captured content, registry, or settings.

The default run uses deterministic frozen `Bgr32` inputs at:

- 512 x 512
- 1920 x 1080
- 3840 x 2160

Each size measures the current full-region pixelate parameters and the current box blur
(`radius=7`, `passes=3`). One warmup per effect precedes nine independent worker samples. The JSON
report contains nearest-rank median/p95 worker duration, managed bytes allocated by the filter
call, best-effort peak private-byte delta, the embedded measured-source SHA-256, and deterministic
raw output-pixel SHA-256 values.

Worker time excludes output hashing. Managed allocation uses
`GC.GetAllocatedBytesForCurrentThread`; it does not represent native WPF allocations. Peak private
bytes are sampled approximately every millisecond and are diagnostic rather than a deterministic
unit-test gate.

## Self-test

From the repository root:

```powershell
$WinShotDotnet = (Get-Command dotnet -ErrorAction Stop).Source

powershell -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\performance\region-effects\Test-RegionEffectHarness.ps1 `
    -DotnetPath $WinShotDotnet
```

The self-test checks cross-thread frozen output, deterministic hashes, distinct blur/pixelate
results, allocation observation, source fingerprinting, the bounded report schema, and all six
default cases. It also runs deterministic 1080p blur through the real `RegionEffectPipeline` while
pumping a WPF Dispatcher, then verifies the frozen `Bgra32` output and its exact golden pixel hash.
The heartbeat count and gap distribution are structural evidence only; the self-test has no timing
threshold. It reports `PASS_REGION_EFFECT_HARNESS` on success.

## Record a baseline

Use the pinned SDK described in `Setup.md`:

```powershell
& $WinShotDotnet run `
    --project .\tools\performance\region-effects\WinShot.RegionEffects.Perf.csproj `
    -c Release --
```

To choose a bounded sample count from 3 through 31:

```powershell
& $WinShotDotnet run `
    --project .\tools\performance\region-effects\WinShot.RegionEffects.Perf.csproj `
    -c Release -- --samples 9
```

## Measure dispatcher heartbeat

The separate fail-closed mode links and embeds both `PixelFilters.cs` and
`RegionEffectProcessing.cs`. It runs one deterministic full-frame 4K blur through the real
pipeline worker while the STA thread pumps a 5 ms WPF Dispatcher heartbeat:

```powershell
& $WinShotDotnet run `
    --project .\tools\performance\region-effects\WinShot.RegionEffects.Perf.csproj `
    -c Release -- --dispatcher-heartbeat
```

Its separate JSON schema reports pipeline preparation time, heartbeat count, nearest-rank p95 and
maximum heartbeat gap, both measured-source hashes, frozen-output state, and the exact raw-pixel
SHA-256. These are local diagnostic measurements, not a UI-responsiveness guarantee or a
performance threshold.

If evidence must be retained, redirect it to an explicitly selected location outside the
repository or to a future root-owned evidence directory. The JSON omits captured pixels, paths,
user and machine names, process identifiers, and command lines.

Run baseline and comparison measurements on the same machine, SDK, architecture, Release
configuration, case order, sample count, and similar idle conditions. Compare distributions, not
one sample. This harness records current behavior; it does not prove UI responsiveness and makes
no performance-improvement claim.
