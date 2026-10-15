# Installer and update contract

This directory defines a credential-free, tool-agnostic contract for a future signed WinShot
installer and updater. It is not an installer recipe, does not select or install a packaging
dependency, and does not authorize a public artifact.

`installer-contract.json` deliberately remains in an authoring state. Product identity, publisher,
packaging technology, signing profile, prerequisite source, update channels, retention terms, and
legal payload all require explicit approval. The contract keeps those values replaceable while
fixing the safety invariants that must survive any later WiX, Velopack, Inno Setup, MSIX, or other
technology decision:

- raw contract JSON is scanned before binding; duplicate property names, including
  Unicode-escaped aliases, are refused at every object depth;
- per-user, non-elevated installation under an installer-owned Local AppData root;
- a stable launcher that survives version replacement and owns shortcuts and sign-in launch;
- no installer-created sign-in consent, with disabled and unconfigured preferences preserved;
- a self-contained .NET payload and recording-only VC++/Media Foundation prerequisite behavior;
  the per-user app install never elevates, while a future official VC++ bootstrap may prompt only
  after explicit disclosure and consent, with refusal leaving recording unavailable rather than
  blocking the screenshot application;
- HTTPS, hash, channel, entitlement, architecture, Authenticode, and timestamp checks before
  activating an update;
- an independently release-key-signed activation attestation for every production payload. The
  exact `activation-release-attestation.v1` bytes use strict UTF-8 without a BOM, LF-only line
  endings, one final LF, fixed ordered headers, and ordinally sorted tab-delimited file records.
  The signed headers bind package ID, version, channel, `win-x64`, paid major, and the exact
  `release-manifest.json` SHA-256. Every payload file except the attestation and its detached
  signature is bound by path, byte length, and SHA-256, including `SHA256SUMS`; therefore a party
  that edits a DLL/EXE or regenerates local checksums/manifests cannot manufacture release trust;
- a raw detached `activation-release-attestation.sig` using RSA-PSS/SHA-256 and a minimum 3072-bit
  release key. The validator accepts only strict DER SubjectPublicKeyInfo, requires its SHA-256 to
  match the approved contract, and requires exact reuse of the independently approved update
  descriptor release-key fingerprint. The public key is a credential-free validation input;
  private release keys never belong in the repository or payload;
- a strict detached `update-candidate.json` signature using RSA-PSS/SHA-256 with at least a
  3072-bit release key, exact staging inventory and release-manifest identity binding, plus a
  short-lived single-use handoff capability that also requires platform Authenticode evidence;
  the current fail-closed verifier accepts only an exact signed production EXE and deliberately
  rejects `.nupkg` until archive-specific trust and safe extraction are implemented;
- capture, recording, and transcode deferral plus an explicit restart-to-apply handoff whose
  technology adapter must bind the exact verified candidate, retain quiescence, and complete
  `Ready -> Commit -> CommitAccepted` before parent shutdown;
- confirmed pre-commit abort restores Application Restart before activity admission reopens;
  uncertain child or commit state retains a fail-closed resident safety hold instead of pretending
  the updater is inert;
- transactional activation recovery and signed forward-fix rollback after activation;
- removal of binaries, shortcuts, updater state, and the owned startup value on uninstall;
- an exact, technology-neutral inventory of owned roaming/local files, subdirectories, and registry
  values, with the install root required to remain distinct from both application-data roots;
- preservation of settings, licensing/trial state, managed History, and recoverable recording
  workspaces by default, plus an explicit, unchecked choice and recovery/license warnings before
  private app data removal;
- removal of bounded clipboard backing, privacy-safe logs, and crash-loop state by default without
  traversing the configured capture directory; user-selected output is never an uninstall target;
- hard exclusion of unsigned engineering artifacts, test/debug payloads, key generators, and
  private signing material; and
- Azure Artifact Signing publisher validation pins the durable exact Public Trust subscriber
  identity EKU together with code-signing and Public Trust marker EKUs, rather than pinning a
  short-lived leaf certificate thumbprint. The PowerShell production gate intentionally refuses
  all artifacts until rigorous primary-PE-signature-to-RFC3161-token parsing is implemented and
  approved; `Get-AuthenticodeSignature` timestamp presence alone is not accepted as that proof; and
- explicit third-party attribution and a separate approved hash/signature-state decision for
  ScreenRecorderLib, without presenting it as WinShot-owned or silently re-signing it.

The Core resident-side coordinator implements the provider-neutral ordering and safety-hold
contract. A separate dependency-free engineering stable launcher now implements the custom
versioned-layout boundary: the root GUI executable reads one bounded exact-ASCII
`active-version`, holds that pointer and the selected immutable payload against replacement,
verifies matching offline Windows/Azure Artifact Signing identity, and invokes the payload through
explicit `CreateProcessW` without shell interpretation or inherited handles. Its authoring artifact
is unsigned and cannot launch the unsigned engineering payload through the production trust path.
The concrete child rendezvous/updater, activation health decision, install actions, signed rollback,
and uninstall actions remain unimplemented.

The activation attestation schema is intentionally parseable by the dependency-free C# 5 launcher.
After the magic line, each header is `<name><TAB><value>` in the exact order below. Each remaining
line is `file<TAB><uppercase SHA-256><TAB><canonical decimal bytes><TAB><relative slash path>`.
Records are ordinally sorted by path. Paths cannot be absolute, contain backslashes, colons,
control characters, or `..` segments. Decimal values have no signs or leading formatting, hashes
are exactly 64 uppercase hexadecimal characters, a BOM and CR are forbidden, and the exact bytes
end in one LF:

```text
WINSHOT-ACTIVATION-RELEASE-ATTESTATION-V1
package-id	<approved package ID>
version	<exact release version>
channel	<exact paid-major channel>
runtime-identifier	win-x64
paid-major	<positive major>
release-manifest-sha256	<64 uppercase hex>
file-count	<bounded canonical decimal>
file	<64 uppercase hex>	<canonical decimal bytes>	<relative/path>
```

## Data ownership and uninstall boundary

The contract names the exact current product-owned surfaces instead of allowing an installer to
interpret broad labels such as "temp":

- `%APPDATA%\{DataDirectoryName}` owns bounded settings and corrupt-settings evidence;
- `%LOCALAPPDATA%\{DataDirectoryName}` owns History metadata/copies, clipboard backing, recording
  recovery workspaces, protected license state, local trial state, privacy-safe logs, and crash-loop
  state;
- the owned HKCU values are the exact sign-in value and the exact trial value. The Run key is never
  deleted, and the trial key may be removed only when empty after deleting the owned value; and
- the configured capture directory is user-selected output, even when it is the default Pictures
  folder. Uninstall never traverses it or deletes `.winshot-recording-*` recovery workspaces there.

`tmp` is deliberately classified as recoverable media, not disposable cache. A native writer may
leave the only recoverable MP4/GIF in one of its owned workspaces. Normal uninstall therefore
retains it. The separate private-data choice may remove it only after warning about unrecovered
recordings and reminding provider-license users to deactivate first. Uninstall never contacts the
license provider automatically; deleting both local trial stores also removes the local trial
record.

These declarations do not authorize recursive deletion based on an unresolved environment
variable. A future adapter must resolve and canonicalize the exact expected product roots, reject
environment roots and reparse points, stop the application, and refuse deletion outside those
roots. Root removal belongs only to the explicit private-data choice.

`TECHNOLOGY-DECISION.md` records the current evidence-backed package candidates: Velopack 1.2.0
first, with WiX Toolset 7.0.0 as fallback. The checked schema-3 contract now standardizes the
custom root-launcher/atomic-pointer/version-directory topology. Velopack remains `UNSET` because
its own root stub/replaceable `current` model conflicts with that topology unless the owner
explicitly chooses and revalidates a reconciled contract. Release-age, final identity, signing,
and clean-VM gates also remain unresolved.

Validate the checked-in authoring contract from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
    .\tools\installer\Test-InstallerContract.ps1 -Mode Authoring
```

Run the validator's mutation/refusal tests:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
    .\tools\installer\Invoke-InstallerContractTests.ps1
```

Production validation intentionally fails until every approval and bounded production value is
present. Once an installer technology is approved and implemented, the production gate also
requires exact signed installer/updater paths, a signed payload directory, and the exact external
DER SubjectPublicKeyInfo for the approved release-attestation key:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
    .\tools\installer\Test-InstallerContract.ps1 -Mode Production `
    -InstallerPath <exact-installer-path> `
    -UpdaterPath <exact-updater-path> `
    -StableLauncherPath <exact-signed-stable-launcher-path> `
    -PayloadDirectory <exact-signed-payload-directory> `
    -ReleaseAttestationPublicKeyPath <exact-approved-release-public-key.spki.der>
```

The validator is a static release boundary, not clean-machine evidence. A production candidate
still needs a clean standard-user install, first launch, sign-in launch, update, restart recovery,
failed-update recovery, forward-fix rollback, and uninstall pass under the final signing identity.
The `InventoryTrustTest` mode exists only for credential-free adversarial validation of the shared
attestation parser and is not a substitute for `Production` or an approval gate.
