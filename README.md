# FrameVault

A portable Windows desktop app that stores a password-protected folder of files inside a playable MP4.

Create or open a workspace, unlock it, and work with its files in Windows File Explorer.
When finished, save your changes back into the MP4 and lock the workspace.
The original source video is preserved when creating a workspace.

## Features

- A single MP4 holds all saved workspace files, folders, and internal metadata.
- Optional password protection using AES-256-GCM and a password-derived key.
- Add, edit, rename, and organize files with your usual Windows applications.
- Manual **Save changes**, **Save & Lock**, and password management.
- Conflict detection, verified file replacement, and retained working copies for recovery.
- Import workspaces from the earlier Gallery, Notes, and Contacts format.
- Native Windows window controls and a portable, self-contained Windows x64 build.

**While unlocked, files are plaintext in a local working folder.** Saving is manual;
this is not a mounted encrypted drive. Unprotected workspaces contain readable data.
See [Security](SECURITY.md) and the [user guide](docs/USAGE.md) for protection boundaries
and recovery behavior.

## Run

Download the [portable ZIP](https://github.com/Plutonium-fream/framevault/releases/latest/download/FrameVault-portable-win-x64.zip)
or [standalone EXE](https://github.com/Plutonium-fream/framevault/releases/latest/download/FrameVault.exe)
from [Releases](https://github.com/Plutonium-fream/framevault/releases/latest).
On Windows 10/11 x64, extract the ZIP and run `FrameVault.exe`, or run the standalone EXE directly.
The portable build needs no installation, administrator rights,
separately installed .NET runtime, or FFmpeg. Builds are currently unsigned.

1. Choose **Create Workspace**, select a source MP4, name the workspace, and choose a new save path.
2. Set a password, or explicitly disable protection.
3. Choose **Open folder in Explorer** to add and edit files.
4. Close files in other applications, then choose **Save & Lock** to save and clear the working copy.

Use **Open Workspace** to return to a saved MP4. Detailed instructions, legacy
migration, error handling, and recovery are in [docs/USAGE.md](docs/USAGE.md).

## Build from source

Use Windows, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
PowerShell, and [FFmpeg](https://ffmpeg.org/download.html) on `PATH` for playback checks.
Run from the repository root:

```powershell
.\build.ps1
```

The script runs the storage/filesystem and offscreen interface checks, publishes
`dist/FrameVault-<version>/`, and creates:

- `dist/FrameVault-portable-win-x64.zip`
- `dist/FrameVault-portable-win-x64.zip.sha256`

To launch a development build:

```powershell
dotnet run --project src/FrameVault
```

For individual checks, prerequisites, and repository layout, see
[CONTRIBUTING.md](CONTRIBUTING.md). The tests are console programs run with
`dotnet run`; `dotnet test` alone does not execute them. The Windows CI workflow
runs the same build script and uploads the package and interface previews as artifacts.

## Limits

- Up to 128 MiB per file and 256 MiB of file data per workspace.
- Up to 4,096 files and 4,096 directory entries; this implementation keeps the saved payload in memory.
- No autosave, password recovery, or guarantee of physical erasure of temporary files.
- Editing or re-encoding the MP4 in other software can remove the workspace.
- Encryption protects saved contents; it does not hide the presence of the workspace extension.

See the [container overview](docs/FORMAT.md) for storage details and
[SECURITY.md](SECURITY.md) for the threat model and vulnerability reporting.

## Contributing and license

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.
User-visible changes are recorded in [CHANGELOG.md](CHANGELOG.md).

FrameVault is licensed under the [MIT license](LICENSE).
