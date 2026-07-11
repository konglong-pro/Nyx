# Nyx

Nyx is a small Windows 11 utility for best-effort secure deletion of local files and folders. It overwrites addressable file data, requests that writes be flushed, weakens the original name, and then deletes the object.

> **Project status:** `v0.1.0-beta.1` is under development. No public binary or GitHub remote is configured yet. Do not use a development build on the only copy of important data.

## What it is for

- Review and shred one or more files or folders through a Windows desktop interface.
- Detect unsupported or unsafe targets before the first destructive write.
- Process data serially with bounded memory and show distinct partial-failure outcomes.
- Work completely offline, without telemetry, accounts, services, or background residency.
- Expose no supported CLI or SDK; launch paths only stage the review UI.

Nyx is not a disk-sanitization or compliance tool. SSD wear levelling, device remapping, snapshots, backups, cloud history, filesystem metadata, caches, and prior copies can retain data. Read the [threat model](docs/threat-model.md) before relying on Nyx.

Nyx does not clean old MFT/USN/FAT records, search indexes, thumbnails, page files, filesystem slack, unallocated space, or device-reserved areas.

## Supported environment

- Windows 11 24H2 or later while the Windows release remains supported by Microsoft
- x64 processors
- Local fixed or removable NTFS, exFAT, or FAT32 volumes
- Standard-user execution; Nyx does not request elevation

Network paths, mapped drives, raw devices, volume roots, ReFS, protected system trees, whole-drive erasure, and free-space wiping are outside the v0.1 scope.

## Intended beta workflow

1. Extract the self-contained release ZIP and run `Nyx.exe`. The beta is unsigned, so Windows may show a SmartScreen warning.
2. Drop files or folders onto the window, use the picker, or pass a small set by dropping them on the program icon. Startup paths are only staged; shredding never begins automatically.
3. Review the resolved objects, size, storage warning, and any blockers.
4. Confirm the batch. Batches containing a folder or at least 100 resolved objects require typing the case-sensitive word `SHRED`.
5. Inspect the result of every object. A stopped batch finishes the current file and its streams, then leaves remaining objects untouched.

Deletion is irreversible once mutation starts. A successful Nyx result means the documented logical overwrite-and-delete procedure completed; it does **not** mean the data is physically unrecoverable.

## Build from source

The repository is pinned to .NET SDK 10.0.301 by `global.json`. Restore, Release build, automated tests, and portable packaging have been verified with that SDK. See [development](docs/development.md) for commands and [testing](docs/testing.md) for the destructive-test guard.

## Documentation

- [Threat model and safety boundaries](docs/threat-model.md)
- [Development setup and architecture](docs/development.md)
- [Testing and destructive-test safeguards](docs/testing.md)
- [Release process](docs/release.md)
- [Security policy](SECURITY.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

## License

Nyx is available under the [MIT License](LICENSE).
