# WinShot stable launcher

This folder contains the dependency-free, installer-owned forwarding launcher. It is deliberately
separate from `WinShot.sln` and the flat portable application artifact because both binaries must
ultimately be named `WinShot.exe` in different directories.

The root launcher resolves only:

```text
{LocalAppData}\PROVISIONAL.WinShot\WinShot.exe
{LocalAppData}\PROVISIONAL.WinShot\active-version
{LocalAppData}\PROVISIONAL.WinShot\versions\{active-version}\WinShot.exe
```

`active-version` is exact non-empty ASCII with no BOM, newline, whitespace, path separator, or
rooted path. It uses the same bounded safe version-segment grammar as the application policy. The
launcher never scans version directories, selects a highest version, downloads an update, mutates
startup registration, supervises the resident process, or silently falls back to another version.

Before process creation it retains read-only, write/delete-denying handles for the launcher and
payload, rejects reparse/final-path mismatches, verifies offline Windows trust and matching Azure
Artifact Signing identity against exact configured publisher-subject and durable subscriber-EKU
pins, reopens and compares the payload file identity, then invokes `CreateProcessW` with an
explicit application name, fixed allowlisted arguments, no inherited handles, and the payload
directory as the working directory. Both trust pins remain `UNSET` in authoring source, so the real
verifier deliberately refuses every engineering payload. This bounds but cannot eliminate the
documented pathname race because Windows does not create a process from an existing file handle.

Production trust also requires `activation-release-attestation.v1` and its raw detached
`activation-release-attestation.sig`. The exact canonical envelope is signed with the independently
approved RSA-PSS/SHA-256 release key and binds package, active version, channel, RID, paid major,
release-manifest hash, and the ordinal path/length/SHA-256 inventory of every version-directory
file other than the two envelope files. The launcher parses the public key as strict DER
SubjectPublicKeyInfo, checks its embedded fingerprint, opens every inventory file without following
a reparse point, and retains all verified file handles until process creation returns. The release
key, channel, publisher subject, and subscriber EKU remain `UNSET`, so authoring artifacts cannot
accidentally pass this production boundary.

The launcher accepts only no arguments, tray-only casing normalized to exact `--tray`, or exact
`--tray --recovered`. Background failures are modal-free and leave a bounded privacy-safe support
record. Interactive failures show one native repair message without a raw path or exception. The
public recovery shape is translated to the private `--tray --launcher-recovered` child shape after
active-version selection. An installed payload reached directly by Windows Application Restart
first authenticates and redirects to the exact package stable root, while the translated child
continues recovery without redirecting again.

The engineering build targets the in-box CLR 4 surface available on WinShot's Windows 10 1809
floor, using only APIs present in .NET Framework 4.7.2. It is compiled by the Microsoft-signed
in-box x64 compiler with explicit framework references and recorded tool hashes. This avoids a
new package acquisition and a self-extracting runtime stub. Production promotion still requires a
pinned clean build image, final cleared package/product identity, signed launcher and payload,
exact timestamp/publisher evidence, an independently signed exact managed/native payload
inventory, installer/updater integration, and clean Windows 10/11 tests.
