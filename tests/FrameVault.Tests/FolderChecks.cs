using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrameVault.Core;

internal static class FolderChecks
{
    public static void Run(string testRoot, string carrier, byte[] sourceBytes, Action<string, Action> test)
    {
        var root = Path.Combine(testRoot, "folder-checks");
        Directory.CreateDirectory(root);
        var workingRoot = Path.Combine(root, "unlocked");
        Directory.CreateDirectory(workingRoot);
        var sequence = 0;
        var openedFolders = new List<WorkspaceFolder>();
        string NewPath(string label) => Path.Combine(root, $"{++sequence:D3}-{label}.mp4");
        WorkspaceFolder Open(WorkspaceSession session)
        {
            var folder = WorkspaceFolder.Open(session, workingRoot);
            openedFolders.Add(folder);
            return folder;
        }

        test("Generic file models round-trip binary data, empty files, folders, and timestamps", () =>
        {
            var path = NewPath("files");
            var bytes = Enumerable.Range(0, 2048).Select(i => (byte)(i % 256)).ToArray();
            var timestamp = DateTimeOffset.Parse("2026-10-25T12:34:56Z");
            using (var session = WorkspaceStore.Create(carrier, path, "Files", "test password"))
            {
                session.Data.Files.Add(new WorkspaceFile { Path = "Documents/binary.dat", Data = bytes.ToArray(), LastWriteTimeUtc = timestamp });
                session.Data.Files.Add(new WorkspaceFile { Path = "empty.txt", Data = [] });
                session.Data.Directories.AddRange(["Documents", "Documents/empty", "An empty folder"]);
                session.Save();
            }
            using var opened = WorkspaceStore.Open(path, "test password");
            Check(opened.Data.Files.Single(f => f.Path == "Documents/binary.dat").Data.SequenceEqual(bytes), "Arbitrary binary bytes changed.");
            Check(opened.Data.Files.Single(f => f.Path == "empty.txt").Data.Length == 0, "Empty file was lost.");
            Check(opened.Data.Directories.Contains("Documents/empty") && opened.Data.Directories.Contains("An empty folder"), "Empty directory metadata was lost.");
            Check(opened.Data.Files[0].LastWriteTimeUtc == timestamp, "File timestamp was lost.");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("Documents/binary.dat"), "Encrypted file path leaked.");
        });

        test("Explorer-style folder edits survive save, lock, move, and reopening only the MP4", () =>
        {
            var path = NewPath("explorer-roundtrip");
            using var session = WorkspaceStore.Create(carrier, path, "Explorer workspace", "folder password");
            var folder = Open(session);
            var firstPath = folder.WorkPath;
            Check(IsChild(firstPath, workingRoot), "Working folder escaped its selected root.");
            Check(!Directory.EnumerateFileSystemEntries(firstPath).Any(), "New workspace should start empty.");
            Directory.CreateDirectory(Path.Combine(firstPath, "Documents", "Empty"));
            File.WriteAllText(Path.Combine(firstPath, "Documents", "東京 résumé.txt"), "First draft — café.", new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(firstPath, "binary.bin"), [0, 255, 127, 1, 0, 42]);
            File.WriteAllText(Path.Combine(firstPath, "delete-me.txt"), "temporary");
            var timestamp = new DateTime(2026, 10, 25, 10, 11, 12, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(firstPath, "Documents", "東京 résumé.txt"), timestamp);
            folder.Save();
            Check(session.Data.Files.Count == 3 && session.Data.Directories.Contains("Documents/Empty"), "Save did not capture the complete tree.");
            Check((session.Data.Files.Single(f => f.Path.EndsWith("résumé.txt")).LastWriteTimeUtc.UtcDateTime - timestamp).Duration() < TimeSpan.FromSeconds(1), "File modified time changed.");
            File.AppendAllText(Path.Combine(firstPath, "Documents", "東京 résumé.txt"), "\nFinal version.", new UTF8Encoding(false));
            File.Move(Path.Combine(firstPath, "binary.bin"), Path.Combine(firstPath, "Documents", "renamed.dat"));
            File.Delete(Path.Combine(firstPath, "delete-me.txt"));
            File.WriteAllText(Path.Combine(firstPath, "new.txt"), "Added after the first save");
            var savedBuffer = session.Data.Files.Single(f => f.Path == "binary.bin").Data;
            folder.SaveAndLock();
            Check(!folder.IsOpen && !Directory.Exists(firstPath), "Successful lock retained the plaintext working folder.");
            Check(savedBuffer.All(b => b == 0), "A superseded generic file buffer was not cleared.");
            Reject(() => { _ = session.Data; }, "Locked folder retained an accessible session.");
            var moved = NewPath("moved"); File.Move(path, moved);
            using var reopened = WorkspaceStore.Open(moved, "folder password");
            Check(reopened.Data.Files.Count == 3 && !reopened.Data.Files.Any(f => f.Path == "delete-me.txt" || f.Path == "binary.bin"), "Deleted or renamed entries remained in MP4 metadata.");
            var again = Open(reopened);
            Check(File.ReadAllText(Path.Combine(again.WorkPath, "Documents", "東京 résumé.txt")).Contains("Final version."), "Edited text was not restored.");
            Check(File.ReadAllBytes(Path.Combine(again.WorkPath, "Documents", "renamed.dat")).SequenceEqual(new byte[] { 0, 255, 127, 1, 0, 42 }), "Renamed binary file changed.");
            Check(Directory.Exists(Path.Combine(again.WorkPath, "Documents", "Empty")), "Empty directory was not restored.");
            again.DiscardAndLock();
            Check(File.ReadAllBytes(carrier).SequenceEqual(sourceBytes), "Folder workflow modified the source video.");
        });

        test("Save and Lock removes only its owned working directory and clears generic buffers", () =>
        {
            var sentinel = Path.Combine(workingRoot, "unrelated.txt"); File.WriteAllText(sentinel, "keep unrelated file");
            using var session = WorkspaceStore.Create(carrier, NewPath("cleanup"), "Cleanup", "password");
            var folder = Open(session);
            File.WriteAllText(Path.Combine(folder.WorkPath, "private.txt"), "secret marker e282f");
            folder.Save();
            var buffer = session.Data.Files.Single().Data;
            folder.SaveAndLock();
            Check(File.ReadAllText(sentinel) == "keep unrelated file", "Cleanup touched a sibling file.");
            Check(buffer.All(b => b == 0), "Lock retained decrypted generic file bytes.");
            Check(!Directory.EnumerateFiles(workingRoot, "private.txt", SearchOption.AllDirectories).Any(), "Plaintext content remained after a successful lock.");
        });

        test("Open files prevent locking without losing edits, encryption keys, or the saved MP4", () =>
        {
            var path = NewPath("open-file");
            using var session = WorkspaceStore.Create(carrier, path, "Open file", "password");
            var folder = Open(session); var file = Path.Combine(folder.WorkPath, "editing.txt");
            File.WriteAllText(file, "Unsaved editor contents");
            var before = File.ReadAllBytes(path);
            using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Reject(folder.SaveAndLock, "Lock accepted a file still open in another application.");
            Check(folder.IsOpen && Directory.Exists(folder.WorkPath) && File.ReadAllText(Path.Combine(folder.WorkPath, "editing.txt")) == "Unsaved editor contents", "Failed lock lost the working copy.");
            Check(session.IsProtected && File.ReadAllBytes(path).SequenceEqual(before), "Failed lock changed the MP4 or disposed its key.");
            folder.SaveAndLock();
            using var reopened = WorkspaceStore.Open(path, "password");
            Check(Encoding.UTF8.GetString(reopened.Data.Files.Single().Data) == "Unsaved editor contents", "Retry after releasing the file lost edits.");
        });

        test("Unexpected files beside the working folder prevent a false successful lock", () =>
        {
            using var session = WorkspaceStore.Create(carrier, NewPath("unexpected-parent-content"), "Parent safety", "password");
            var folder = Open(session);
            File.WriteAllText(Path.Combine(folder.WorkPath, "inside.txt"), "Keep the working content");
            var unexpected = Path.Combine(Path.GetDirectoryName(folder.WorkPath)!, "unexpected.txt");
            File.WriteAllText(unexpected, "Keep this unrecognized sibling too");
            Reject(folder.SaveAndLock, "Lock reported success while unrecognized plaintext remained beside its working folder.");
            Check(folder.IsOpen && session.IsProtected, "Safety failure disposed the session or reported it locked.");
            Check(File.ReadAllText(unexpected) == "Keep this unrecognized sibling too", "Cleanup deleted an unrecognized sibling.");
            Check(File.ReadAllText(Path.Combine(folder.WorkPath, "inside.txt")) == "Keep the working content", "Safety failure lost working files.");
            File.Delete(unexpected);
            folder.SaveAndLock();
        });

        test("A failed MP4 replacement preserves working files and rolls back the staged file model", () =>
        {
            var path = NewPath("failed-save");
            using var session = WorkspaceStore.Create(carrier, path, "Failed save", "password");
            session.Data.Files.Add(new WorkspaceFile { Path = "document.txt", Data = Encoding.UTF8.GetBytes("Saved version") }); session.Save();
            var oldBuffer = session.Data.Files[0].Data;
            var folder = Open(session); File.WriteAllText(Path.Combine(folder.WorkPath, "document.txt"), "Unsaved replacement");
            var before = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(folder.SaveAndLock, "Replacement succeeded while the destination denied replacement.");
            Check(folder.IsOpen && session.IsProtected, "Failed save unexpectedly locked the session.");
            Check(Encoding.UTF8.GetString(session.Data.Files.Single().Data) == "Saved version" && oldBuffer.Any(b => b != 0), "Failed save cleared or replaced the previous in-memory model.");
            Check(File.ReadAllText(Path.Combine(folder.WorkPath, "document.txt")) == "Unsaved replacement", "Failed save removed the unsaved working file.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Failed save changed the original MP4.");
            folder.SaveAndLock();
        });

        test("Competing unlocked folders reject lost updates and retain the losing working copy", () =>
        {
            var path = NewPath("competing-folders");
            using var firstSession = WorkspaceStore.Create(carrier, path, "Concurrent", "password");
            using var secondSession = WorkspaceStore.Open(path, "password");
            var first = Open(firstSession); var second = Open(secondSession);
            File.WriteAllText(Path.Combine(first.WorkPath, "first.txt"), "First saved writer"); first.Save();
            File.WriteAllText(Path.Combine(second.WorkPath, "second.txt"), "Keep this unsaved work");
            Reject(second.SaveAndLock, "A stale folder overwrote another saved workspace.");
            Check(second.IsOpen && File.ReadAllText(Path.Combine(second.WorkPath, "second.txt")) == "Keep this unsaved work", "Conflict discarded the losing working copy.");
            using (var check = WorkspaceStore.Open(path, "password")) Check(check.Data.Files.Single().Path == "first.txt", "First writer was lost.");
            second.DiscardAndLock(); first.SaveAndLock();
        });

        test("Unsafe stored paths and Windows filename aliases are rejected before extraction", () =>
        {
            foreach (var unsafePath in new[] { "../escape.txt", "/root.txt", "C:/root.txt", "C:\\root.txt", "a/../../out.txt", "file.txt:stream", "NUL", "dir/CON.txt", "CON .txt", "LPT1 .txt", "dir/end.", "dir/end ", "a//b.txt", "." })
            {
                var path = NewPath("unsafe");
                WriteFixture(path, sourceBytes, new { Name = "Unsafe", CreatedAt = DateTimeOffset.UtcNow, Photos = Array.Empty<object>(), Notes = Array.Empty<object>(), Contacts = Array.Empty<object>(), Files = new[] { new { Path = unsafePath, Data = new byte[] { 42 }, LastWriteTimeUtc = DateTimeOffset.UtcNow } }, Directories = Array.Empty<string>() }, 2);
                Reject(() => { using var session = WorkspaceStore.Open(path); var folder = Open(session); folder.DiscardAndLock(); }, "Unsafe stored path accepted: " + unsafePath);
            }
            Check(!File.Exists(Path.Combine(root, "escape.txt")), "An unsafe fixture wrote outside the working folder.");
        });

        test("Case-insensitive duplicates and file-directory collisions cannot be saved", () =>
        {
            using var session = WorkspaceStore.Create(carrier, NewPath("path-collisions"), "Collisions");
            session.Data.Files.AddRange([new WorkspaceFile { Path = "Same.txt", Data = [1] }, new WorkspaceFile { Path = "same.txt", Data = [2] }]);
            Reject(session.Save, "Case-equivalent filenames were accepted.");
            session.Data.Files.Clear();
            session.Data.Files.AddRange([new WorkspaceFile { Path = "parent", Data = [1] }, new WorkspaceFile { Path = "parent/child.txt", Data = [2] }]);
            Reject(session.Save, "A file was accepted as another file's parent directory.");
            session.Data.Files.Clear(); session.Data.Directories.AddRange(["same", "SAME"]);
            Reject(session.Save, "Case-equivalent directories were accepted.");
            session.Data.Directories.Clear();
        });

        test("A directory junction is rejected without traversing or deleting its external target", () =>
        {
            var path = NewPath("junction");
            using var session = WorkspaceStore.Create(carrier, path, "Junction", "password");
            var folder = Open(session);
            var target = Path.Combine(root, "external-junction-target"); Directory.CreateDirectory(target);
            var sentinel = Path.Combine(target, "outside.txt"); File.WriteAllText(sentinel, "Never traverse this target");
            var link = Path.Combine(folder.WorkPath, "linked");
            CreateJunction(link, target);
            try
            {
                var before = File.ReadAllBytes(path);
                Reject(folder.SaveAndLock, "A junction was followed or silently omitted during lock.");
                Check(folder.IsOpen && File.ReadAllText(sentinel) == "Never traverse this target", "Junction handling touched the external target.");
                Check(File.ReadAllBytes(path).SequenceEqual(before), "Rejected junction changed the saved MP4.");
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
            }
            folder.DiscardAndLock();
            Check(File.ReadAllText(sentinel) == "Never traverse this target", "Cleanup followed a junction into unrelated data.");
        });

        test("NTFS alternate data streams are rejected instead of silently lost on lock", () =>
        {
            using var session = WorkspaceStore.Create(carrier, NewPath("alternate-stream"), "Streams", "password");
            var folder = Open(session);
            var path = Path.Combine(folder.WorkPath, "ordinary.txt"); File.WriteAllText(path, "Ordinary content");
            var alternate = path + ":hidden"; File.WriteAllText(alternate, "Alternate stream must not be silently deleted");
            Reject(folder.SaveAndLock, "An alternate stream was omitted and its host file deleted.");
            Check(folder.IsOpen && File.ReadAllText(alternate).Contains("must not"), "Rejected alternate stream was lost.");
            File.Delete(alternate); folder.DiscardAndLock();
        });

        test("Oversized working files are rejected before snapshot allocation", () =>
        {
            using var session = WorkspaceStore.Create(carrier, NewPath("oversized-working-file"), "Bounds");
            var folder = Open(session); var path = Path.Combine(folder.WorkPath, "too-large.bin");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.SetLength(128L * 1024 * 1024 + 1);
            Reject(folder.Save, "A working file larger than 128 MiB was accepted.");
            Check(folder.IsOpen && File.Exists(path), "Size rejection removed the working file.");
            File.Delete(path); folder.DiscardAndLock();
        });

        test("Generic file count limits are enforced before saved data is replaced", () =>
        {
            var path = NewPath("file-count"); using var session = WorkspaceStore.Create(carrier, path, "Bounds");
            var before = File.ReadAllBytes(path);
            for (var i = 0; i < 4097; i++) session.Data.Files.Add(new WorkspaceFile { Path = $"{i}.txt", Data = [] });
            Reject(session.Save, "More than 4096 files were accepted.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Count rejection altered the MP4.");
            session.Data.Files.Clear();
        });

        test("Legacy photos and generic files share the aggregate 256 MiB storage limit", () =>
        {
            var path = NewPath("shared-size-limit"); using var session = WorkspaceStore.Create(carrier, path, "Shared bounds");
            var before = File.ReadAllBytes(path);
            // Reuse one buffer so the validation check does not allocate a full 256 MiB fixture.
            var bytes = new byte[WorkspaceStore.MaxFileBytes];
            session.Data.Files.AddRange([new WorkspaceFile { Path = "first.bin", Data = bytes }, new WorkspaceFile { Path = "second.bin", Data = bytes }]);
            session.Data.Photos.Add(new PhotoItem { Name = "legacy.png", Data = [1] });
            Reject(session.Save, "A combined photo and generic-file budget larger than 256 MiB was accepted.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Aggregate size rejection altered the saved MP4.");
            session.Data.Files.Clear();
        });

        test("Real version1 workspaces migrate into normal files without hidden legacy copies", () =>
        {
            var path = NewPath("legacy-v1");
            WriteFixture(path, sourceBytes, LegacyData(), 1);
            var before = File.ReadAllBytes(path);
            using var session = WorkspaceStore.Open(path);
            Check(session.Data.Notes.Count == 3 && session.Data.Files.Count == 0, "Version1 fixture did not load as a legacy workspace.");
            var folder = Open(session);
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Opening the folder modified the legacy MP4 before saving.");
            var restored = Directory.GetFiles(folder.WorkPath, "*", SearchOption.AllDirectories);
            Check(restored.Length == 5, "Legacy content was lost or duplicate titles overwrote each other.");
            Check(restored.Any(f => IsChild(f, Path.Combine(folder.WorkPath, "Gallery"))) && restored.Any(f => IsChild(f, Path.Combine(folder.WorkPath, "Notes"))) && restored.Any(f => IsChild(f, Path.Combine(folder.WorkPath, "Contacts"))), "Legacy collections were not exposed in Explorer folders.");
            folder.SaveAndLock();
            using var reopened = WorkspaceStore.Open(path);
            Check(reopened.Data.Files.Count == 5 && reopened.Data.Photos.Count == 0 && reopened.Data.Notes.Count == 0 && reopened.Data.Contacts.Count == 0, "Migrated MP4 retained hidden legacy payload copies.");
            Check(BinaryPrimitives.ReadUInt32BigEndian(File.ReadAllBytes(path).AsSpan(sourceBytes.Length + 24 + 8, 4)) == 2, "Migrated workspace did not write version2.");
        });

        test("A failed migration save retains the original legacy model and all extracted files", () =>
        {
            var path = NewPath("failed-legacy"); WriteFixture(path, sourceBytes, LegacyData(), 1);
            var before = File.ReadAllBytes(path);
            using var session = WorkspaceStore.Open(path); var folder = Open(session);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(folder.SaveAndLock, "A locked legacy MP4 unexpectedly saved.");
            Check(folder.IsOpen && Directory.GetFiles(folder.WorkPath, "*", SearchOption.AllDirectories).Length == 5, "Failed migration lost extracted files.");
            Check(session.Data.Photos.Count == 1 && session.Data.Notes.Count == 3 && session.Data.Contacts.Count == 1 && session.Data.Files.Count == 0, "Failed migration changed the previous legacy model.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Failed migration changed the legacy file.");
            folder.SaveAndLock();
        });

        test("Password-protected version1 workspaces migrate without changing the source or password", () =>
        {
            var path = NewPath("protected-legacy-v1");
            WriteFixture(path, sourceBytes, LegacyData(), 1, "legacy fixture password");
            var before = File.ReadAllBytes(path);
            Reject(() => WorkspaceStore.Open(path, "wrong password").Dispose(), "A protected version1 fixture accepted the wrong password.");
            using var session = WorkspaceStore.Open(path, "legacy fixture password");
            Check(session.IsProtected && session.Data.Notes.Count == 3 && session.Data.Files.Count == 0, "Protected version1 metadata was not decoded correctly.");
            var folder = Open(session);
            Check(Directory.GetFiles(folder.WorkPath, "*", SearchOption.AllDirectories).Length == 5, "Protected legacy entries were not extracted completely.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Opening a protected legacy workspace changed its MP4 before saving.");
            folder.SaveAndLock();
            using var reopened = WorkspaceStore.Open(path, "legacy fixture password");
            Check(reopened.IsProtected && reopened.Data.Files.Count == 5 && reopened.Data.Photos.Count == 0 && reopened.Data.Notes.Count == 0 && reopened.Data.Contacts.Count == 0, "Protected legacy migration lost files or retained hidden legacy records.");
            var bytes = File.ReadAllBytes(path);
            Check(bytes.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes), "Protected migration changed the carrier video bytes.");
            Check(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(sourceBytes.Length + 24 + 8, 4)) == 2, "Protected migration did not write version2.");
        });

        test("A missing working folder is an error, not an instruction to erase saved files", () =>
        {
            var path = NewPath("missing-folder"); using var session = WorkspaceStore.Create(carrier, path, "Missing folder");
            session.Data.Files.Add(new WorkspaceFile { Path = "saved.txt", Data = Encoding.UTF8.GetBytes("Keep saved copy") }); session.Save();
            var folder = Open(session); var before = File.ReadAllBytes(path);
            Directory.Delete(folder.WorkPath, true);
            Reject(folder.SaveAndLock, "A missing folder erased the saved file set.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Missing folder changed the MP4.");
            folder.DiscardAndLock();
        });

        test("Generic files survive adding, changing, and removing the workspace password", () =>
        {
            var path = NewPath("generic-password");
            using (var session = WorkspaceStore.Create(carrier, path, "Passwords"))
            {
                session.Data.Files.Add(new WorkspaceFile { Path = "secret.bin", Data = [0, 4, 8, 12, 255] }); session.Save(); session.ChangePassword("first password");
            }
            using (var session = WorkspaceStore.Open(path, "first password")) { Check(session.Data.Files.Single().Data[^1] == 255, "Adding password changed binary content."); session.ChangePassword("second password"); }
            Reject(() => WorkspaceStore.Open(path, "first password").Dispose(), "Old password still opened generic-file workspace.");
            using (var session = WorkspaceStore.Open(path, "second password")) session.ChangePassword(null);
            using var plain = WorkspaceStore.Open(path); Check(plain.Data.Files.Single().Data.SequenceEqual(new byte[] { 0, 4, 8, 12, 255 }), "Removing password changed binary data.");
        });

        test("Recovery lists exited sessions while excluding an active working copy", () =>
        {
            using var session = WorkspaceStore.Create(carrier, NewPath("active-recovery"), "Active recovery fixture");
            var active = Open(session);
            Check(!WorkspaceFolder.FindRetainedFolders(workingRoot).Contains(active.WorkPath, StringComparer.OrdinalIgnoreCase), "An active folder was incorrectly offered as abandoned recovery data.");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(FolderChecks).Assembly.Location);
            foreach (var argument in new[] { "--retained-folder-fixture", carrier, NewPath("exited-recovery"), workingRoot }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000)) { child.Kill(true); throw new IOException("Recovery fixture process did not exit promptly."); }
            Check(child.ExitCode == 0, "Recovery fixture failed: " + stderr.GetAwaiter().GetResult());
            var retainedPath = stdout.GetAwaiter().GetResult().Trim();
            Check(IsChild(retainedPath, workingRoot), "Recovery fixture returned an unexpected working-folder path.");
            Check(WorkspaceFolder.FindRetainedFolders(workingRoot).Contains(retainedPath, StringComparer.OrdinalIgnoreCase), "An exited session's plaintext folder was not discoverable for recovery.");
            Check(File.ReadAllText(Path.Combine(retainedPath, "recover-me.txt")) == "Unsaved work from an exited process", "Recovery discovery changed or deleted unsaved work.");
            var owner = Path.GetDirectoryName(retainedPath)!;
            Check(IsChild(owner, workingRoot) && Guid.TryParseExact(Path.GetFileName(owner), "N", out _), "Recovery fixture owner was outside the test root.");
            Directory.Delete(owner, true);
            active.DiscardAndLock();
        });
        foreach (var folder in openedFolders.Where(f => f.IsOpen))
            try { folder.DiscardAndLock(); }
            catch (Exception exception) { Console.Error.WriteLine("Test scratch cleanup: " + exception.Message); }
    }

    private static object LegacyData() => new
    {
        Name = "Version1 legacy archive", CreatedAt = DateTimeOffset.Parse("2026-10-25T10:00:00Z"),
        Photos = new[] { new PhotoItem { Name = "../../legacy.png", Album = "Trips: Summer", Data = [137, 80, 78, 71, 13, 10, 26, 10] } },
        Notes = new[] { new NoteItem { Title = "Same title", Body = "First note" }, new NoteItem { Title = "Same title", Body = "Second note" }, new NoteItem { Title = "../Invalid? title*", Body = "Sanitize this title" } },
        Contacts = new[] { new ContactItem { Name = "Ada: Lovelace", Phone = "+31 20 123 4567", Email = "ada@example.test", Notes = "Text contact details" } }
    };

    private static void WriteFixture(string path, byte[] carrier, object data, uint version, string? password = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        var envelope = new byte[64 + json.Length + (password is null ? 32 : 16)];
        "FRMVAULT"u8.CopyTo(envelope);
        BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(8, 4), version);
        BinaryPrimitives.WriteUInt64BigEndian(envelope.AsSpan(48, 8), (ulong)json.Length);
        if (password is null)
        {
            json.CopyTo(envelope.AsSpan(64));
            SHA256.HashData(envelope.AsSpan(0, 64 + json.Length), envelope.AsSpan(64 + json.Length));
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(12, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(envelope.AsSpan(16, 4), 600_000);
            RandomNumberGenerator.Fill(envelope.AsSpan(20, 16));
            RandomNumberGenerator.Fill(envelope.AsSpan(36, 12));
            var key = Rfc2898DeriveBytes.Pbkdf2(password, envelope.AsSpan(20, 16), 600_000, HashAlgorithmName.SHA256, 32);
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(envelope.AsSpan(36, 12), json, envelope.AsSpan(64, json.Length), envelope.AsSpan(64 + json.Length, 16), envelope.AsSpan(0, 64));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(carrier);
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(24 + envelope.Length));
        "uuid"u8.CopyTo(header[4..]);
        Convert.FromHexString("D72A1598E35B4AD5AA342D903E15AB91").CopyTo(header[8..]);
        output.Write(header); output.Write(envelope); output.Flush(true);
    }

    private static void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{path}\" \"{target}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Could not create the junction fixture: " + stdout + stderr);
        Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, "Junction fixture is not a reparse point.");
    }

    private static bool IsChild(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action operation, string message)
    {
        try { operation(); }
        catch (Exception e) when (e is IOException or InvalidDataException or CryptographicException or ArgumentException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException) { return; }
        throw new Exception(message);
    }
}
