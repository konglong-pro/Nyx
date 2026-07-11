# Third-Party Notices

This file summarizes framework and test dependencies restored with .NET SDK 10.0.301 as of 2026-07-10. `Nyx.App` and `Nyx.Core` have no runtime `PackageReference`; the restored MSTest graph is development-only. The portable build script preserves the authoritative .NET SDK license and third-party notice files alongside this project notice.

## Distributed with self-contained builds

Nyx targets the Microsoft .NET 10 Windows Desktop runtime and WPF. Self-contained release packages therefore include Microsoft-authored framework/runtime components even though Nyx has no direct non-framework runtime `PackageReference`.

- [.NET runtime](https://github.com/dotnet/runtime) — MIT License; see the repository's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`.
- [Windows Presentation Foundation (WPF)](https://github.com/dotnet/wpf) — MIT License; see the repository's license and third-party notices.

The exact SDK files are packaged as `DOTNET-LICENSE.txt` and `DOTNET-THIRD-PARTY-NOTICES.txt`; those files are authoritative for the self-contained runtime payload.

## Development and test only

- [MSTest 4.0.2](https://github.com/microsoft/testfx) — MIT License. It is referenced by the two test projects and is not intended to ship as an application runtime dependency.

## Nyx source

Nyx's own source is licensed under the repository [MIT License](LICENSE). Microsoft, .NET, Windows, and WPF are trademarks of their respective owners; their inclusion here does not imply endorsement.
