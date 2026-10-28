# Contributing to FrameVault

Bug reports, usability improvements, documentation, and focused pull requests are welcome.
For larger changes, open an issue describing the problem and proposed behavior first.
Report vulnerabilities using [SECURITY.md](SECURITY.md).

## Development environment

- Windows 10/11 x64 and an NTFS working directory.
- .NET 8 SDK. `global.json` selects an installed stable .NET 8 feature band.
- PowerShell; PowerShell 7 is recommended.
- FFmpeg on `PATH` for the playback-preservation check. FFmpeg is a test tool, not an application dependency.

The repository has no third-party NuGet package dependencies. The SDK may still
download Microsoft's framework/runtime packs during restore and publishing.
An optional local SDK at `.tools/dotnet/dotnet.exe` is supported by `build.ps1`.

```powershell
dotnet run --project src/FrameVault
```

## Verification

Run the complete checks and create a portable build:

```powershell
.\build.ps1
```

Or run the checks separately:

```powershell
dotnet run --project tests/FrameVault.Tests -c Release
dotnet run --project tests/FrameVault.DesignChecks -c Release -- artifacts/previews
```

Both check projects are console programs that fail with a nonzero exit code.
`dotnet test` alone does not run them. Storage checks exercise real Windows
replacement, ACL, alternate-stream, and directory-junction behavior. A sandbox
that disallows creating junctions cannot run the complete suite; use a normal
Windows development shell. The app itself does not require administrator rights.

Interface checks render synthetic workspaces offscreen. Review the generated
PNGs in `artifacts/previews`, and manually exercise any changed window, dialog,
keyboard navigation, or Explorer workflow. Offscreen checks do not validate
native composition or third-party transparency mods.

## Changes and pull requests

- Keep changes focused and describe the user-visible result and verification.
- Follow `.editorconfig` and the surrounding code style; avoid unrelated formatting.
- Preserve existing workspaces and the original source MP4. Format changes need
  compatibility and migration checks, including failure and recovery paths.
- Use established authenticated encryption. Do not replace it with custom cryptography.
- Use synthetic files and test passwords. Never include private MP4s, extracted
  working folders, credentials, logs containing personal paths, or signing keys.
- Add meaningful regression checks for behavior changes; document any limits
  that affect data preservation or confidentiality.
- Update `CHANGELOG.md` for user-visible changes.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/FrameVault` | Windows WPF interface and native window integration |
| `src/FrameVault.Core` | MP4 storage, encryption, working folders, and recovery |
| `tests/FrameVault.Tests` | Storage and Windows filesystem checks |
| `tests/FrameVault.DesignChecks` | Offscreen interface checks and previews |
| `tests/assets` | Synthetic development-only video fixture |
| `docs` | User guide and container overview |
| `.github` | CI, issue forms, and pull request template |

Builds, local tools, and previews are ignored by Git. Portable ZIPs and checksums
belong in release assets, not source commits. The Windows CI workflow builds and
uploads artifacts; publishing a GitHub release remains a maintainer action.

Contributions are provided under the project's [MIT license](LICENSE).
