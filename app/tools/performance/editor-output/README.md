# Editor-output synthetic performance evidence

This isolated, package-free .NET 10 Windows harness measures WinShot's exact linked editor-output
sources with deterministic generated pixels. It does not reference the application project,
launch WinShot, read captures or settings, touch the clipboard or registry, use the network, or
persist image evidence.

The default Release run uses one warmup and nine independent cold samples at 1920 x 1080 and
3840 x 2160. Each sample performs the revision-owned sequence used by editor Copy/Drag:

1. exact editor render;
2. one bounded, serialized PNG encode;
3. same-directory staging, asynchronous flush, and durable flush;
4. guarded namespace commit and exact-revision cache publication;
5. 31 cached `TryGet` plus existence checks without another render, encode, stage, or commit.

The JSON report records median/nearest-rank-p95 stage timing, process-wide managed-allocation
delta, best-effort peak private-byte delta, GC collection deltas, bounded byte counts, deterministic
pixel/PNG hashes, source fingerprints, invocation counts, and cleanup/guardrail results. Fixture
creation, hashing, decoding, and cleanup are excluded from stage timings.

## Self-test

Use the exact pinned SDK from `Setup.md`, or supply another explicit path to that exact SDK:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\performance\editor-output\Test-EditorOutputHarness.ps1 `
    -DotnetPath (Get-Command dotnet).Source
```

The wrapper refuses package/project references, SDK drift, package-lock generation, and linked or
embedded source-inventory drift. The executable self-test checks statistics, deterministic
render/PNG/file hashes, exact cache reuse, a real partial-write failure, cancellation before
publication, owned staging cleanup, privacy-safe JSON property names, source fingerprints, and a
1080p pumped-dispatcher heartbeat.

## Record the synthetic baseline

```powershell
$WinShotDotnet = (Get-Command dotnet -ErrorAction Stop).Source

& $WinShotDotnet run `
    --project .\tools\performance\editor-output\WinShot.EditorOutput.Perf.csproj `
    -c Release `
    --no-build `
    --no-restore `
    -p:Platform=x64 `
    -- --samples 9
```

Sample count is bounded from 3 through 31. Successful JSON is emitted only to stdout. Redirect it
to an explicitly selected evidence location when retention is required.

## Dispatcher heartbeat

```powershell
& $WinShotDotnet run `
    --project .\tools\performance\editor-output\WinShot.EditorOutput.Perf.csproj `
    -c Release `
    --no-build `
    --no-restore `
    -p:Platform=x64 `
    -- --dispatcher-heartbeat
```

This mode pumps a 5 ms WPF Dispatcher heartbeat through one synthetic 4K preparation and reports
p50/p95/maximum callback gaps, preparation time, worker observations, hashes, source binding, and
cleanup. Heartbeat timing is diagnostic local evidence, not a customer responsiveness guarantee.

## Guardrails and evidence limits

- Synthetic input is capped at 4K and the harness PNG cap is 128 MiB.
- Async encode/staging work receives a cooperative two-minute cancellation deadline. Synchronous
  WPF render, durable flush, and namespace commit are not forcibly interrupted; apply an external
  process timeout when validating a potentially unhealthy machine.
- Process-wide managed allocation and peak private-byte delta have 1 GiB fail-safe ceilings.
- Encoder concurrency must remain one.
- Every successful sample must publish one exact output, reuse it without mutation, and leave no
  final or `.partial` file.
- Output schemas omit paths, filenames, usernames, machine identity, process IDs, command lines,
  pixels, and encoded image bytes.
- Local timings are not universal budgets or speed claims. Compare before/after distributions on
  the same machine, exact SDK, Release configuration, case order, and sample count.
