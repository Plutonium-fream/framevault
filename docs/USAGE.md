# Using FrameVault

[Project overview](../README.md) · [Security](../SECURITY.md)

## Create, work, save, and lock

1. Choose **Create Workspace**. Select an existing MP4, give the workspace a name, and select a new .mp4 destination. Password protection is selected by default; use a strong, unique password. Creating a workspace never overwrites or modifies the selected source video.
2. FrameVault creates a new MP4 containing an empty workspace.
3. Choose **Open folder in Explorer**. FrameVault has already created a private, ordinary working folder containing the unlocked workspace. Copy files into it, create folders, rename items, and edit files with your normal applications. The app shows the exact folder path.
4. Close files in other applications, then choose **Save changes** to write the complete folder back into the MP4. Saving includes additions, edits, renames, deletions, and empty folders. The working folder stays open so you can continue.
5. When finished, close files in other applications and choose **Save & Lock**. FrameVault saves the current folder, removes its working copy after a successful save, and releases the unlocked session and encryption key.

Saving is manual. There is no background synchronization or autosave into the MP4. Until a save succeeds, changes exist only in the working folder. Counts on the archive desk describe the last scanned contents, rather than a live file listing.

To return later, choose **Open Workspace** and select the MP4. A protected workspace asks for its password; an unprotected workspace opens directly. Password controls are available after unlocking so protection can be added, changed, or removed.

## Working folder and recovery

**Files in the working folder are plaintext while unlocked.** Password protection applies to the saved MP4, not the unlocked working copy. This design provides normal Explorer access without a driver; it does not provide VeraCrypt-style encryption of every filesystem access.

By default, FrameVault places its uniquely named working folders under:

    %LOCALAPPDATA%\FrameVault\Unlocked

The exact path is shown in the app and can be selected and copied. Working folders have user-restricted Windows permissions. Applications running as your Windows user, administrators, backup tools, and security software may still be able to read them. Nonsecret ownership information and a session lease are kept beside the visible Files directory, outside your editable file tree.

If saving or locking fails, FrameVault keeps the working folder and unlocked session available. Errors before replacement leave the previously saved MP4 intact. An error during verification or cleanup can occur after a new save has reached disk, so keep the working copy until the reported problem is resolved. Files still open in another application can prevent a safe save or lock; close them and retry. Do not force-close FrameVault after an error if you have unsaved changes.

If FrameVault cannot verify the MP4 after a replacement failure, it marks the file's protection as **not verified** and blocks further saves and password changes. The MP4 might already use the new password or be unencrypted after a failed password-removal operation. Keep your working files and reopen a verified MP4 or recovery copy before continuing. Closing an uncertain workspace offers to keep its plaintext working folder for recovery and release the session key; this does not claim that the workspace was safely locked.

A crash or forced shutdown can leave a plaintext working folder behind. FrameVault does not silently delete those folders. The start screen offers **Open recovery location** when it finds an inactive working copy. Check retained folders before removing anything. To recover files manually, reopen the saved MP4, copy the required files from the retained folder into the newly opened working folder, and save again. Keep the retained copy until you have verified the saved result.

Removing a working folder does not guarantee physical erasure on SSDs or other storage devices. Other applications may also create autosaves, thumbnails, temporary files, or backups outside the folder. Those copies, Windows paging, and crash dumps are outside FrameVault's protection.

## Existing FrameVault workspaces

FrameVault 2.0 can open the earlier Gallery, Notes, and Contacts format. Their contents are made available as ordinary files:

- Photos appear under Gallery, grouped by album when present.
- Notes appear as text files under Notes.
- Contacts appear as text files under Contacts, containing their name, phone number, email address, and notes.

Legacy names are made safe for Windows paths and duplicates receive distinct names. The existing MP4 is not changed simply by opening it. The first successful save stores the folder format and removes the superseded app collections from that saved workspace. Keep your own backup before converting an important workspace.

New saves use workspace format version 2. Earlier FrameVault versions cannot open that format; continue using FrameVault 2.0 for converted files.

## Limits and practical details

- A generic file may contain up to 128 MiB of data. Total stored file data is limited to 256 MiB; any legacy photo data shares that total until converted. A workspace supports up to 4,096 files and 4,096 directory entries. Serialized payloads are bounded to 384 MiB. This in-memory implementation is for personal collections, not multi-gigabyte archives.
- Windows-compatible relative file paths are supported. Symbolic links, junctions, other reparse points, alternate data streams, reserved device names, and ambiguous path collisions are rejected. FrameVault does not follow links outside its owned working folder.
- Save errors and file locks must be resolved before locking. Never assume that a changed working folder is saved just because an editor has finished writing to it.
- Saving rewrites the carrier video as part of the replacement MP4, so very large carrier videos make saves slower and require enough free space for the replacement.
- There is no password recovery. A locked protected workspace cannot be recovered without its password.
- Password protection does not hide the carrier video, the presence of the extension, or its approximate size. Managed strings cannot be reliably wiped from process memory.
- Transcoding, editing, optimizing, or re-saving the MP4 in another tool may strip its extension and destroy the workspace. Copy or rename the whole file instead.
- Changing a password removes the prior payload from the current file but cannot erase previous backups or guarantee physical erasure of old disk blocks.
- Keep important workspaces backed up. Use writable local storage that supports atomic replacement; background synchronization and concurrent editing can cause save conflicts.

## Window appearance

FrameVault uses the native Windows frame for maximizing, resizing, snapping, and taskbar boundaries. Content and dialogs use opaque surfaces and readable form labels.

The [Translucent Windows Windhawk mod](https://windhawk.net/mods/translucent-windows) can extend glass across a window and affect readability. FrameVault applies a best-effort compatibility reset only to its own windows when they initialize, render, or activate. It does not change Windhawk settings. Offscreen checks do not verify native composition; test visual changes with your Windows configuration.
