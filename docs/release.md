# Release Process

## Current release state

The intended first release is `v0.1.0-beta.1`. The local build, automated tests, portable ZIP, bundled notices, and checksum flow have been exercised with .NET SDK 10.0.301. The repository owner and GitHub remote are not configured, the binary is intentionally unsigned, and the manual release matrix is incomplete; no public release should be claimed until those gates have evidence.

The current candidate evidence and unresolved gates are recorded in [`release-gate-v0.1.0-beta.1.md`](release-gate-v0.1.0-beta.1.md).

## Release contents

Publish a Windows 11 x64, self-contained, ReadyToRun directory build in a ZIP. Do not trim it and do not collapse it to a single executable. The release must include or link clearly to:

- the source corresponding to the tag;
- `LICENSE` and `THIRD-PARTY-NOTICES.md`, plus authoritative notices emitted with the .NET runtime packs;
- SHA-256 checksums for distributed binaries/archives;
- known limitations and a link to the threat model;
- an explicit unsigned/SmartScreen warning.

Nyx has no installer, updater, background service, telemetry, or runtime download in v0.1.

## Release automation

`scripts/build-release.ps1` publishes `win-x64`, self-contained, ReadyToRun, untrimmed, non-single-file output; checks for `Nyx.exe` and `coreclr.dll`; creates the ZIP in an isolated staging directory; and writes a lowercase SHA-256 entry. Its default outputs are:

- `artifacts/release/Nyx-v0.1.0-beta.1-win-x64-portable.zip`
- `artifacts/release/SHA256SUMS.txt`

The release script has been run locally with the SDK selected by `global.json`:

```powershell
./scripts/build-release.ps1 -NoRestore
```

Run it with `-NoRestore` only after the matching application runtime assets have been restored. Without `-NoRestore`, the script restores the application project with `NuGet.Config`. The portable ZIP includes README, all project documentation, security policy, license, project/runtime notices, and `global.json`; the release publisher must also attach the corresponding source archive.

Packaging refuses to overwrite either an existing ZIP or checksum file. Use a fresh output directory for another local candidate, for example `./scripts/verify.ps1 -NoRestore -Package`, which writes beneath `artifacts/verification` and checks the resulting checksum independently. A candidate remains a local development artifact until every release gate is completed.

## Required gates

1. Confirm the tag/version is `v0.1.0-beta.1` and the working tree contains the intended source.
2. Restore and build with the SDK selected by `global.json`; retain the exact SDK and workload information.
3. Run all non-destructive tests and the safely guarded temporary-root integration suite.
4. Complete the manual filesystem, storage, cloud, accessibility, scale, and startup matrix in [`testing.md`](testing.md).
5. Inspect the published dependency graph and output; update third-party notices and bundle all required authoritative notice files.
6. Verify the app manifest remains `asInvoker`, Per-Monitor V2 DPI aware, and long-path aware.
7. Smoke-test the ZIP on a clean supported Windows 11 x64 system without a separately installed .NET runtime.
8. Verify path arguments only stage a batch, no destructive action begins without review/confirmation, and single-instance forwarding never queues work while busy.
9. Export both privacy-preserving and explicit-path JSON reports and validate their schema and warnings.
10. Generate and independently re-check SHA-256 checksums for the final immutable artifacts.

## Release notes

Every beta release note must state:

- “best-effort logical overwrite and deletion,” never “unrecoverable,” “military-grade,” or standards-certified;
- supported Windows, architecture, drive, and filesystem boundaries;
- that SSD/flash, snapshots, backups, cloud versions, metadata, caches, slack, unallocated space, and device-reserved areas are not sanitized;
- that the binary is unsigned and may trigger SmartScreen;
- known partial-result or compatibility issues;
- the private vulnerability reporting route configured in [`SECURITY.md`](../SECURITY.md).

## GitHub publication

Once the repository owner and remote are known, configure the release workflow and private vulnerability reporting before publication. Publish the ZIP, checksum file, source, license/notices, and known limitations from the same reviewed tag. Real-device evidence is a human gate; workflow success alone is insufficient.

## Rollback and corrections

There is no automatic updater. If a release can target the wrong object, follow a link, overstate a result, or bypass confirmation, remove the affected binary from normal distribution, post a clear security notice, and publish a corrected version only after repeating every required gate. Never replace an existing artifact in place while keeping its checksum or version.
