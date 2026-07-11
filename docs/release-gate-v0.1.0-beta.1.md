# v0.1.0-beta.1 Release Gate Evidence

Recorded: 2026-07-11 (Asia/Taipei)

Decision: **source commit candidate; public beta release blocked**.

This record distinguishes checks executed on the current source tree from unavailable or approval-gated manual checks. It does not turn inferred hardware capabilities into release evidence.

## Automated gates

| Gate | Result | Evidence |
| --- | --- | --- |
| SDK selection and restore | PASS | Project-local .NET SDK `10.0.301`, selected by `global.json`; all four projects restored. NuGet access was required for vulnerability metadata. |
| Release build | PASS | `dotnet build Nyx.slnx --configuration Release --no-restore`: 0 warnings, 0 errors. |
| Core tests | PASS | `Nyx.Core.Tests`: 30 passed, 0 failed, 0 skipped. |
| Sentinel-protected integration tests | PARTIAL | `Nyx.DestructiveTests` with `NYX_RUN_DESTRUCTIVE_TESTS=1`: 52 passed, 0 failed, 2 skipped. Both skips require symbolic-link creation capability unavailable to the current standard-user process; junction coverage passed. |
| Report schema and privacy modes | PASS | Tests lock the complete top-level property set, conditional item property sets, app/version/batch/time/status fields, redacted `path`/`currentPath`, explicit paths, media, assurance, byte counts, error code, and failure stage. |
| Formatting | PASS | `dotnet format Nyx.slnx --verify-no-changes --no-restore`. |
| Manifest and publish configuration | PASS | `asInvoker`, `uiAccess=false`, Per-Monitor V2 DPI, long-path awareness, `win-x64`, self-contained, ReadyToRun, untrimmed, and non-single-file settings verified. |
| Runtime dependency boundary | PASS | App and Core have no NuGet `PackageReference`; the portable output contains the application and Microsoft self-contained runtime packs. |
| Portable package and checksum | PASS | The release script rebuilt the ZIP after the final source and documentation edits. Required application/runtime/docs/license/notices are present, PDB files are absent, and the independently computed SHA-256 matches the adjacent `SHA256SUMS.txt`. The adjacent checksum file is authoritative because its value cannot be embedded inside the archive it identifies. |

## Current machine evidence

| Area | Result | Evidence |
| --- | --- | --- |
| Supported OS | PASS | Windows 11 25H2, build `26200.8655`. |
| Architecture | PASS | x64 OS and AMD64 process architecture. |
| Privilege model | PASS | Checks ran as a standard user; Nyx remains `asInvoker`. |
| NTFS temporary-root integration | PASS | The sentinel-protected destructive suite ran only in its generated OS temporary root on NTFS. This is not a substitute for the full media matrix. |
| NVMe availability | OBSERVED | The machine reports two Samsung NVMe disk instances and fixed NTFS volumes. Non-admin access could not prove the volume-to-physical-disk mapping, so the full NVMe manual cell remains open. |
| OneDrive availability | OBSERVED | A Personal OneDrive sync root and client installation exist. No user cloud file was read, created, hydrated, changed, or deleted during this gate run. |

## Open manual gates

These gates were not executed and must not be described as passing:

- real HDD, USB HDD, USB flash, unknown-media, exFAT, and FAT32 cases;
- dedicated OneDrive fully-local, placeholder, partial/offline, and sync-lock fixtures;
- the 100 GB non-sparse NTFS/exFAT acceptance case and fixed-memory observation;
- keyboard-only end-to-end flow, Narrator, Accessibility Insights, light/dark/high-contrast, 200% DPI, and 200% text scaling;
- cold/subsequent startup timing and a clean supported Windows machine without a separately installed .NET runtime;
- program-icon path staging, busy single-instance forwarding, and both report-export flows as an interactive UI smoke test.

Launching the newly built GUI requires an action-time desktop approval. That approval was not received during this run, so no GUI smoke result is claimed. The 100 GB case was also not started because it consumes substantial time, temporary capacity, and SSD write endurance.

## GitHub and publication blockers

- The repository has no GitHub remote.
- The configured `gh` account is present but its token is invalid.
- No verified private vulnerability-reporting route is configured in `SECURITY.md`.
- No source commit, reviewed tag, hosted CI result, or GitHub Release existed when this evidence record was prepared.

The source may be committed to a feature branch. Do not create the public `v0.1.0-beta.1` release or tag until every open manual gate has evidence, GitHub authentication and the target repository are known, CI is green, and a private security contact exists.
