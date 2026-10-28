# Container and encryption overview

[Project overview](../README.md) · [Security](../SECURITY.md)

## Container and encryption

All saved file bytes, relative paths, directory entries, workspace metadata, and timestamps are stored inside the MP4. There is no external workspace database or cloud account. The unlocked working folder is the deliberate temporary plaintext exception described in the [working folder and recovery guide](USAGE.md#working-folder-and-recovery).

FrameVault uses an ISO Base Media File Format uuid extension box. It does not append an unstructured archive to the video. Existing video boxes and their byte offsets remain intact. A terminal MP4 box whose size is zero is normalized to its explicit size in the new copy before the extension is written. Ordinary players ignore the extension and play the carrier video. The [MP4 Registration Authority documents the uuid extension mechanism](https://mp4ra.org/registered-types/boxes).

Protected workspaces encrypt the complete saved workspace payload with AES-256-GCM and a 128-bit authentication tag. A 256-bit key is derived with PBKDF2-HMAC-SHA256, 600,000 iterations, and a random 128-bit salt. Every save uses a fresh 96-bit nonce, and the envelope header is authenticated. Implementation uses the platform's [AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm) and [Rfc2898DeriveBytes.Pbkdf2](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rfc2898derivebytes.pbkdf2) APIs. The password is not stored in the file, and session keys are cleared on disposal. Workspaces created without a password contain readable data.

Saves first build a replacement MP4 in the same directory, flush it, and replace the previous file with a temporary recovery backup. The new file is verified before that backup is removed. Conflicting external edits are rejected to prevent stale sessions overwriting newer work. Replacement files contain encrypted payloads when password protection is enabled. The source carrier selected during creation is never changed.

A crash or rare replacement/cleanup failure can leave temporary MP4 recovery copies beside the workspace. FrameVault reports retained copies when it can. A previous copy can use an earlier password, contain subsequently deleted files, or be unencrypted if protection was just added. Keep recovery copies until the current MP4 is verified, then remove them deliberately. Changing the current workspace password does not change those older copies.

## Source map

- `src/FrameVault.Core/WorkspaceStore.cs`: MP4 parsing, envelope validation, key derivation, and model bounds.
- `src/FrameVault.Core/WorkspaceSession.cs`: authenticated loading, saving, password updates, and key disposal.
- `src/FrameVault.Core/WorkspaceReplacement.cs`: verified replacement and recovery paths.
- `src/FrameVault.Core/WorkspaceFolder.cs`: unlocked working folders and file snapshots.

Current saves use workspace format version 2. Legacy version 1 workspaces are migrated on save; see [the user guide](USAGE.md#existing-framevault-workspaces). This is an implementation overview, not a complete interoperability specification.
