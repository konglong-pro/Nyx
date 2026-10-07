# Development

## Current status

The v0.1 implementation builds with the official .NET SDK 10.0.301 selected by `global.json`. Restore, Debug/Release builds, unit tests, guarded Windows integration tests, ReadyToRun publish, ZIP creation, and checksum verification have been exercised locally. Real-media, cloud-provider, accessibility, and clean-machine release gates remain manual.

## Prerequisites

- Windows 11 24H2 or later on x64
- .NET SDK 10.0.301, as pinned by [`global.json`](../global.json)
- A disposable local NTFS test directory for development; never use personal data as a fixture

Nyx is a Windows-only WPF application. It runs as the current user and development should not introduce an elevation requirement, service, registry integration, telemetry, or network dependency.

## Repository map

- `src/Nyx.App`: WPF presentation, window lifecycle, drag/drop, accessibility, and single-instance coordination.
- `src/Nyx.Core`: preflight, snapshots, native filesystem boundary, revalidation, shredding orchestration, result models, and report serialization.
- `tests/Nyx.Core.Tests`: non-destructive policy, orchestration, state, and report tests.
- `tests/Nyx.App.Tests`: bounded launch forwarding, single-instance coordination, progress coalescing, and offline WPF layout checks.
- `tests/Nyx.DestructiveTests`: read-only Windows inventory tests plus fault-injected and real-operation integration tests; mutation is limited to the sentinel-protected generated temp root.
- `docs/threat-model.md`: canonical safety boundary and result meanings.

The application layer must not duplicate or weaken core safety decisions. The native processor keeps its fault plan internal and test-only so integration tests can force short writes, flush failures, identity changes, and delete failures without exposing a production bypass.

`Nyx.Core` intentionally exports no public types. Friend-assembly access is limited to the WPF executable and the two test assemblies; v0.1 has no supported SDK surface.

## Intended local workflow

The restore and build commands below match `.github/workflows/ci.yml`; the run command follows the application project:

```powershell
dotnet restore Nyx.slnx
dotnet build Nyx.slnx -c Debug --no-restore
dotnet test tests/Nyx.Core.Tests/Nyx.Core.Tests.csproj -c Debug --no-build --no-restore
dotnet test tests/Nyx.App.Tests/Nyx.App.Tests.csproj -c Debug --no-build --no-restore
dotnet run --project src/Nyx.App/Nyx.App.csproj
```

CI uses `Release` rather than `Debug`. Release packaging is owned by `scripts/build-release.ps1`; use the documented script rather than assembling an archive manually. See [`release.md`](release.md).

For one local Release verification command, use `./scripts/verify.ps1`. It runs the core and application suites plus read-only inventory checks. Add `-IncludeDestructiveTests` to opt into sentinel-protected temporary fixtures, `-Package` to build and independently check a portable candidate in a fresh `artifacts/verification` directory, or `-NoRestore` after a successful restore.

The repository enables nullable analysis, deterministic builds, current recommended .NET analyzers, and warnings as errors through `Directory.Build.props`. Do not suppress a warning globally to make a local change pass.

## Design constraints

- Keep scanning and shredding serial and cancellation-aware. The 1 MiB data buffer bounds memory independently of file size.
- Cancellation means “stop after the current file and all of its streams”; there is no unsafe mid-file kill or pause state.
- The UI state machine is `Ready -> Checking -> Blocked/Review -> Confirmation -> Revalidating -> Shredding -> Results`.
- No launch argument may bypass review or confirmation. Launch paths only populate a pending batch.
- No blocker has a force-through path. A blocked descendant blocks its selected folder batch.
- Use stable volume/file identity and revalidated handles for destructive decisions; path strings alone are insufficient.
- Never follow a reparse point. Supported links affect only the link entry.
- Preserve the result taxonomy defined in the threat model; uncertain or partial work must never be reported as `Shredded`.
- Keep the runtime dependency surface to the .NET Windows Desktop framework unless a dependency is explicitly approved.

## Native and filesystem behavior

Native interop must expose Win32 errors without losing their numeric codes. Implementations must detect short writes, request write-through behavior, invoke `FlushFileBuffers`, and distinguish overwrite, flush, rename, and delete stages.

Read-only attributes may be cleared only after final revalidation. If later work fails and the object still exists, restoration is best effort and the failure stage must remain visible.

Directory traversal must be bottom-up. Enumeration, identity, attributes, sizes, timestamps, and named NTFS data streams form the review snapshot and must match the final pre-write scan.

## Reports and privacy

JSON export is a manual, local action. Keep `schemaVersion` explicit, use batch-local IDs by default, and never add content hashes or full paths silently. Any schema change requires tests and a compatibility note in the release documentation.

## Before handing off a change

- Run the narrowest relevant non-destructive tests once the SDK workflow is verified.
- Use fault injection instead of destructive fixtures wherever possible.
- If destructive integration coverage is necessary, follow [`testing.md`](testing.md) and confirm the harness guard before running it.
- Report files changed, commands actually run, commands skipped, and residual risks.
