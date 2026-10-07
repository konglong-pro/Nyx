# Testing

## Safety rule

Tests must prove that Nyx does not mutate anything outside the reviewed target. Unit tests should use injected native/filesystem doubles. A destructive test may run only inside the fresh OS-temporary directory created by the harness. The harness requires `NYX_RUN_DESTRUCTIVE_TESTS=1`, creates a unique `Nyx.Shred.<GUID>` directory, writes `.nyx-destructive-tests-sentinel`, and rechecks both the temp-root prefix and sentinel before every fixture mutation and cleanup.

## Command status

The following sequence has been verified with .NET SDK 10.0.301:

```powershell
dotnet restore Nyx.slnx
dotnet build Nyx.slnx --configuration Release --no-restore
dotnet test tests/Nyx.Core.Tests/Nyx.Core.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Nyx.App.Tests/Nyx.App.Tests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Nyx.DestructiveTests/Nyx.DestructiveTests.csproj --configuration Release --no-build --no-restore --filter TestCategory=Inventory
```

The inventory category is read-only and does not require opt-in. To run the sentinel-guarded shredder integration tests, set the opt-in for that process deliberately:

```powershell
$env:NYX_RUN_DESTRUCTIVE_TESTS = '1'
dotnet test tests/Nyx.DestructiveTests/Nyx.DestructiveTests.csproj --configuration Release --no-build --no-restore --filter TestCategory=Destructive
Remove-Item Env:NYX_RUN_DESTRUCTIVE_TESTS
```

Without the exact value `1`, destructive cases report inconclusive and do not create a shred target. Do not set this variable globally or use fixtures containing real data.

`./scripts/verify.ps1` runs the Release build, core/application tests, and read-only inventory tests. `-IncludeDestructiveTests` opts in only for that run and restores the previous process environment afterward. `-Package` creates a candidate in a new verification directory and independently checks its SHA-256 checksum. Existing release artifacts are never overwritten.

## Unit coverage

Non-destructive tests should cover:

- protected-path classification and supported filesystem/drive checks;
- ancestor folding, volume/file-ID deduplication, and stable batch ordering;
- warning versus blocker classification, including media, links, attributes, cloud state, hard links, and unsupported NTFS attributes;
- confirmation rules for folders and batches resolving to at least 100 objects;
- snapshot comparison across identity, attributes, sizes, timestamps, children, and named data streams;
- all native-operation failure points, including short write, partial write, flush, truncate, rename, delete, and attribute restoration;
- stop-after-current-file semantics and fatal batch-stop conditions;
- exact mapping to every result status;
- JSON schema versioning, default path omission, explicit path inclusion, and absence of content hashes.
- immutable review snapshots, atomic report replacement on failure/cancellation, bounded launch messages, and progress coalescing across thousands of updates.

## Isolated integration coverage

Once the destructive harness has an audited guard, exercise disposable fixtures for:

- zero-byte files and 1 MiB buffer boundaries;
- Unicode names, reserved-name-adjacent cases, and long paths;
- multiple file ADS and directory ADS on NTFS;
- read-only, locked, hard-linked, compressed, sparse, and EFS cases;
- symbolic links, junctions, and mount points with a canary in the target;
- identity replacement between review, revalidation, and execution;
- a new child appearing during folder processing;
- normal nested-folder batches, including named directory streams, whose timestamps change as reviewed children are removed;
- injected or real rename/delete denial after overwrite;
- user stop while a multi-stream file is active;
- 100 GB non-sparse generated or preallocated acceptance data on NTFS/exFAT only, with constant-memory observation; sparse files are a separate blocker test.

Large-file tests must account for actual writes and runtime; they are release acceptance tests, not routine per-commit tests. FAT32 is limited to its native per-file maximum and must not receive the 100 GB case.

## Canary invariants

Every integration fixture should place known-content canaries outside the selected object and, for links, inside the link target. After the run:

- link targets are byte-for-byte unchanged;
- all objects in a preflight-blocked batch are byte-for-byte unchanged;
- `NotStarted` objects are byte-for-byte unchanged;
- no object outside the explicitly marked disposable root changed;
- a partial result is never promoted to `Shredded`.

The harness must fail closed if its generated root is not a direct descendant of the resolved OS temp directory or its sentinel is absent. Test code must not accept a caller-supplied cleanup root.

## Manual release matrix

Before a beta release, validate on real Windows 11 x64 hardware:

| Area | Required cases |
| --- | --- |
| Media | HDD, NVMe SSD, USB HDD, USB flash, and unknown media classification |
| Filesystem | NTFS, exFAT, FAT32 within native limits |
| Cloud | fully local OneDrive object, placeholder, partial/offline state, sync lock |
| UI | keyboard-only, Narrator, Accessibility Insights, light/dark/high contrast, 200% DPI and text scaling |
| Scale | thousands of reviewed objects, fixed-memory large file, responsive stop request |
| Startup | cold start <= 3 seconds and subsequent start target <= 1 second on a representative Windows 11 x64 SSD system |

Record the Windows build, device/media type, filesystem, Nyx commit, and observed result for manual evidence. Do not claim a media cell passed based on an emulated or inferred device.

## CI status and expectation

The checked-in Windows workflow restores and builds the solution, runs `Nyx.Core.Tests`, runs the complete Windows integration project with the exact temp-sandbox opt-in, and invokes the release script. Real-media tests remain manual and must never be represented as CI coverage. Local equivalents of the workflow commands and package verification have been exercised; the hosted GitHub workflow cannot run until a remote is configured.
