# Release scaffold

This directory contains credential-free inputs for the WinShot release gate. The current package
identity and publisher are deliberately provisional. Nothing here authorizes a public release.
The generated ZIP is portable validation material, not an installer or updater.

The payload also contains `WinShot.Startup.exe`, a dormant no-argument bridge for a future
packaged `windows.startupTask` declaration. It is a versioned payload file, not the installer-root
stable launcher, and it refuses to run outside a positively identified immutable package install
root. The unsigned ZIP and current custom installer-layout evidence do not activate it; package
identity, manifest declaration, signing, and installed startup testing remain production gates.

`tools/release/Build-ReleaseArtifact.ps1` creates a self-contained `win-x64`, untrimmed,
multi-file **unsigned test artifact** from the application project. It:

- performs a runtime-specific locked restore, then publishes with the pinned SDK;
- validates the app/runtime/native payload and x64 PE architecture;
- applies bounded file-count, path, per-file, text-read, total-size, and archive-size limits;
- emits a typed, ordinal `payload-attribution.json` for every non-metadata payload, including exact
  path, bytes, SHA-256, owning component, source component, and source kind;
- binds runtime/native files and satellites to the pinned installed framework, Windows SDK files
  to the restored package, ScreenRecorderLib to its exact locked package asset, source archives to
  reviewed SHA-256 evidence, and the SDK-patched app host to its reviewed host-template ancestor;
- requires exact `NotSigned` status for WinShot-owned assemblies and the recorder library, and
  rejects source, tests, symbols, XML docs, key generators, private-key formats, key-pool CSVs,
  Debug paths, user settings/history/license data, and repository path leakage;
- adds the unsigned-test warning, current third-party notice, and byte-verified `.NET` product
  license/third-party-notice files from the pinned toolchain;
- records the Git HEAD, dirty-state flags, a build-input tree digest, pinned SDK, and packaging-tool
  digest without recording source paths or file contents;
- emits an ordinally sorted file manifest, canonical CycloneDX 1.7 SBOM, `SHA256SUMS`, and a ZIP
  whose entry order and timestamps are normalized. Two archives are expected to be byte-identical
  only when their recorded source inputs, packaging tools, SDK/runtime, and execution environment
  are the same;
- revalidates both the unpacked directory and a GUID-temporary extraction of the final ZIP before
  returning it.

From the repository root:

```powershell
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\release\Build-ReleaseArtifact.ps1 `
    -Mode UnsignedTest `
    -DotnetPath $dotnet `
    -OutputRoot .\artifacts\release
```

Validate an unpacked artifact independently:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\release\Test-ReleaseArtifact.ps1 `
    -ArtifactDirectory .\artifacts\release\WinShot-1.0.0-win-x64-UNSIGNED-TEST `
    -ExpectedVersion 1.0.0 `
    -DotnetPath $dotnet
```

Validate the distributable ZIP independently:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\release\Test-ReleaseArchive.ps1 `
    -ArchivePath .\artifacts\release\WinShot-1.0.0-win-x64-UNSIGNED-TEST.zip `
    -ExpectedVersion 1.0.0 `
    -DotnetPath $dotnet
```

Exercise the unchanged positive control and manifest-order, checksum-order, case-collision,
traversal, recorder-substitution, and dependency-injection refusals:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
    .\tools\release\Invoke-ReleaseArtifactTests.ps1 `
    -ArtifactDirectory .\artifacts\release\WinShot-1.0.0-win-x64-UNSIGNED-TEST `
    -ExpectedVersion 1.0.0 `
    -DotnetPath $dotnet
```

Archive validation rejects traversal, directory/link entries, case-insensitive duplicate paths,
unexpected executables/DLLs, excessive counts or sizes, and invalid unsigned-owned signature
states before acceptance. Extraction uses a new GUID-owned directory directly beneath system temp;
files are created without overwrite and only that exact owned directory is removed afterward.

Validators intentionally reject legacy artifacts whose manifests predate source provenance.
Generate a fresh artifact in a new output root; never retrofit metadata into or overwrite an older
artifact.

`-Mode Production` is a fail-closed boundary, not a signing implementation. It first requires an
explicit final owner, publisher, non-provisional package ID, signing-certificate reference, and
HTTPS RFC 3161 timestamp URL, then stops because the signed installer/updater pipeline and
signature verification are not yet implemented. Never weaken that stop to publish a ZIP.

The generated CycloneDX 1.7 document contains the four direct external payload components with
exact occurrences and models the pre-patch .NET apphost template as the application pedigree
ancestor. Package/template hashes and declared-license evidence are source-bound, and license
acknowledgement is explicitly `declared`. `legalReviewApproved` remains false: the generated
inventory and shipped upstream notice material are engineering evidence, not legal advice or
authorization to distribute. A pinned offline CycloneDX schema-validation dependency is also not
yet selected under the package-age policy. Production still requires qualified notice/terms
review, installer/updater selection, signing, attestation, and clean-VM lifecycle evidence.
