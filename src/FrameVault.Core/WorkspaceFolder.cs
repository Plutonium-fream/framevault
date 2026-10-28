using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FrameVault.Core;

public sealed class WorkspaceFolder
{
    private readonly object _gate = new();
    private readonly WorkspaceSession _session;
    private readonly string _ownerPath;
    private readonly string _identity;
    private FileStream? _lease;
    public static string DefaultWorkingRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameVault", "Unlocked");
    public string WorkPath { get; private set; }
    public bool IsOpen { get; private set; } = true;
    public int FileCount { get; private set; }
    public long TotalBytes { get; private set; }

    private WorkspaceFolder(WorkspaceSession session, string ownerPath, string identity, FileStream lease)
    {
        _session = session; _ownerPath = ownerPath; _identity = identity; _lease = lease;
        WorkPath = Path.Combine(ownerPath, "Files");
    }

    public static WorkspaceFolder Open(WorkspaceSession session, string? workingRoot = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var data = session.Data;
        WorkspaceStore.ValidateData(data);
        var root = Path.GetFullPath(workingRoot ?? DefaultWorkingRoot);
        WorkspaceFileSystem.CheckNoLinks(root);
        Directory.CreateDirectory(root);
        var identity = Guid.NewGuid().ToString("N");
        var owner = Path.Combine(root, identity);
        WorkspaceFileSystem.CreatePrivateDirectory(owner);
        FileStream? lease = null;
        WorkspaceFolder? folder = null;
        var generated = new List<byte[]>();
        try
        {
            lease = new FileStream(Path.Combine(owner, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            folder = new WorkspaceFolder(session, owner, identity, lease);
            File.WriteAllText(Path.Combine(owner, "workspace.json"), JsonSerializer.Serialize(new Marker(identity, session.FilePath, DateTimeOffset.UtcNow)));
            Directory.CreateDirectory(folder.WorkPath);
            var directories = new HashSet<string>(data.Directories, StringComparer.OrdinalIgnoreCase);
            var files = new List<WorkspaceFile>(data.Files);
            AddLegacy(data, files, directories, generated);
            foreach (var file in files) foreach (var parent in WorkspacePaths.Parents(file.Path)) directories.Add(parent);
            foreach (var directory in directories.OrderBy(p => p.Count(c => c == '/')))
                Directory.CreateDirectory(WorkspacePaths.Under(folder.WorkPath, directory));
            foreach (var file in files)
            {
                var path = WorkspacePaths.Under(folder.WorkPath, file.Path);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(file.Data); output.Flush(true); }
                File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc.UtcDateTime);
            }
            folder.FileCount = files.Count; folder.TotalBytes = files.Sum(f => f.Data.LongLength);
            return folder;
        }
        catch (Exception exception)
        {
            lease?.Dispose();
            try
            {
                if (folder is not null && Directory.Exists(folder.WorkPath)) DeleteTree(folder.WorkPath);
                File.Delete(Path.Combine(owner, "workspace.json")); File.Delete(Path.Combine(owner, "session.lock"));
                Directory.Delete(owner, false);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IOException("Opening failed and a partial plaintext folder could not be removed. Review it at " + owner, new AggregateException(exception, cleanup)); }
            throw;
        }
        finally { foreach (var bytes in generated) CryptographicOperations.ZeroMemory(bytes); }
    }

    public static IReadOnlyList<string> FindRetainedFolders(string? workingRoot = null)
    {
        var root = Path.GetFullPath(workingRoot ?? DefaultWorkingRoot);
        if (!Directory.Exists(root)) return [];
        WorkspaceFileSystem.CheckNoLinks(root);
        var results = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                WorkspaceFileSystem.CheckNoLinks(directory);
                var markerPath = Path.Combine(directory, "workspace.json");
                if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 16384) continue;
                WorkspaceFileSystem.CheckNoLinks(markerPath);
                var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath));
                if (marker?.Identity != Path.GetFileName(directory)) continue;
                var leasePath = Path.Combine(directory, "session.lock");
                WorkspaceFileSystem.CheckNoLinks(leasePath);
                using var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                foreach (var name in new[] { "Files", "Closing" })
                {
                    var path = Path.Combine(directory, name);
                    if (!Directory.Exists(path)) continue;
                    WorkspaceFileSystem.CheckNoLinks(path); results.Add(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return results;
    }

    public void Save()
    {
        lock (_gate)
        {
            EnsureOwned();
            using var snapshot = Capture(false);
            Persist(snapshot);
            snapshot.CheckUnchanged(WorkPath);
        }
    }

    public void SaveAndLock() => Lock(true);
    public void DiscardAndLock() => Lock(false);

    public void LeaveForRecovery()
    {
        lock (_gate)
        {
            EnsureOwned(requireFolder: false);
            _session.Dispose();
            _lease?.Dispose(); _lease = null;
            IsOpen = false;
        }
    }

    private void Lock(bool save)
    {
        lock (_gate)
        {
            EnsureOwned(requireFolder: save);
            if (!Directory.Exists(WorkPath)) { FinishLock(); return; }
            using var beforeMove = Capture(true);
            var original = WorkPath;
            var quarantine = Path.Combine(_ownerPath, "Closing");
            // Windows refuses the parent rename with open descendants; re-freeze and compare after the move.
            beforeMove.CloseHandles();
            if (!string.Equals(original, quarantine, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(quarantine) || File.Exists(quarantine)) throw new IOException("The closing folder already exists. The working folder was kept.");
                Directory.Move(original, quarantine); WorkPath = quarantine;
            }
            Snapshot? snapshot = null;
            try
            {
                snapshot = Capture(true);
                if (!beforeMove.Matches(snapshot)) throw new IOException("Files changed while preparing to lock. The working folder was kept; save again after closing other applications.");
                beforeMove.Dispose();
                snapshot.CheckUnchanged(WorkPath);
                if (save) Persist(snapshot);
                snapshot.CheckUnchanged(WorkPath);
                foreach (var file in snapshot.Files)
                {
                    var path = WorkspacePaths.Under(WorkPath, file.Path);
                    WorkspaceFileSystem.CheckNoLinks(path);
                    File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
                snapshot.CloseHandles();
                foreach (var directory in snapshot.Directories.OrderByDescending(p => p.Count(c => c == '/')))
                {
                    var path = WorkspacePaths.Under(WorkPath, directory);
                    WorkspaceFileSystem.CheckNoLinks(path); Directory.Delete(path, false);
                }
                WorkspaceFileSystem.CheckNoLinks(WorkPath); Directory.Delete(WorkPath, false);
                FinishLock();
            }
            catch (Exception exception)
            {
                snapshot?.CloseHandles();
                try { if (snapshot is not null) RestoreMissing(snapshot); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                { throw new IOException("Locking could not finish. Review the retained working folder at " + WorkPath + (save ? ". Saved content remains in the MP4." : ". Some discarded files could not be restored."), new AggregateException(exception, restore)); }
                if (!string.Equals(original, WorkPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Move(WorkPath, original); WorkPath = original; }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                    { throw new IOException("Locking could not finish. The working folder is retained at " + WorkPath, new AggregateException(exception, rollback)); }
                }
                throw;
            }
            finally { snapshot?.Dispose(); }
        }
    }

    private void Persist(Snapshot snapshot)
    {
        snapshot.CheckUnchanged(WorkPath);
        var data = _session.Data;
        var oldFiles = data.Files; var oldDirectories = data.Directories;
        var photos = data.Photos; var notes = data.Notes; var contacts = data.Contacts;
        data.Files = snapshot.Files; data.Directories = snapshot.Directories;
        data.Photos = []; data.Notes = []; data.Contacts = [];
        try { _session.Save(); _session.VerifySavedFile(); }
        catch
        {
            data.Files = oldFiles; data.Directories = oldDirectories;
            data.Photos = photos; data.Notes = notes; data.Contacts = contacts;
            throw;
        }
        snapshot.Committed = true; FileCount = snapshot.Files.Count; TotalBytes = snapshot.Files.Sum(f => f.Data.LongLength);
        foreach (var file in oldFiles) CryptographicOperations.ZeroMemory(file.Data);
        foreach (var photo in photos) CryptographicOperations.ZeroMemory(photo.Data);
        foreach (var note in notes) note.Title = note.Body = "";
        foreach (var contact in contacts) contact.Name = contact.Phone = contact.Email = contact.Notes = "";
        oldFiles.Clear(); oldDirectories.Clear(); photos.Clear(); notes.Clear(); contacts.Clear();
    }

    private Snapshot Capture(bool deleting)
    {
        var snapshot = new Snapshot();
        try
        {
            var tree = ReadTree(WorkPath);
            snapshot.Directories = tree.Directories;
            foreach (var directory in tree.Directories.Prepend(""))
            {
                var path = directory.Length == 0 ? WorkPath : WorkspacePaths.Under(WorkPath, directory);
                WorkspaceFileSystem.CheckStreams(path);
                if (deleting && WorkspaceFileSystem.CheckDeleteDirectory(path) is { } handle) snapshot.DirectoryHandles.Add(handle);
            }
            long total = 0;
            foreach (var relative in tree.Files)
            {
                var path = WorkspacePaths.Under(WorkPath, relative);
                WorkspaceFileSystem.CheckStreams(path);
                var stream = WorkspaceFileSystem.OpenSnapshotFile(path, deleting); snapshot.Handles.Add(stream);
                var length = stream.Length;
                if (length > WorkspaceStore.MaxFileBytes) throw new IOException("A file exceeds the 128 MiB limit: " + relative);
                total += length;
                if (total > WorkspaceStore.MaxTotalFileBytes) throw new IOException("Working-folder files exceed the 256 MiB total limit.");
                var bytes = new byte[checked((int)length)];
                var file = new WorkspaceFile { Path = relative, Data = bytes, LastWriteTimeUtc = File.GetLastWriteTimeUtc(path) };
                snapshot.Files.Add(file); stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) throw new IOException("A file changed while saving: " + relative);
            }
            snapshot.CheckUnchanged(WorkPath);
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    private static Tree ReadTree(string root)
    {
        if (!Directory.Exists(root)) throw new IOException("The working folder is missing. The MP4 was not changed.");
        WorkspaceFileSystem.CheckNoLinks(root);
        var files = new List<string>(); var directories = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(parent))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Links and junctions are not supported: " + path);
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                WorkspacePaths.Validate(relative);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relative);
                    if (directories.Count > WorkspaceStore.MaxDirectories) throw new IOException("The workspace exceeds the 4096-folder limit.");
                    pending.Push(path);
                }
                else
                {
                    files.Add(relative);
                    if (files.Count > WorkspaceStore.MaxFiles) throw new IOException("The workspace exceeds the 4096-file limit.");
                }
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase); directories.Sort(StringComparer.OrdinalIgnoreCase);
        return new Tree(files, directories);
    }

    private void EnsureOwned(bool requireFolder = true)
    {
        if (!IsOpen) throw new ObjectDisposedException(nameof(WorkspaceFolder));
        if (requireFolder && _session.RequiresReopen)
            throw new IOException("The MP4 protection and save state is uncertain. Keep this working folder for recovery and reopen a verified MP4 before saving or locking.");
        WorkspaceFileSystem.CheckNoLinks(_ownerPath);
        if (Path.GetFileName(_ownerPath) != _identity || Path.GetDirectoryName(WorkPath) != _ownerPath || Path.GetFileName(WorkPath) is not ("Files" or "Closing"))
            throw new IOException("The working-folder ownership check failed.");
        var markerPath = Path.Combine(_ownerPath, "workspace.json");
        WorkspaceFileSystem.CheckNoLinks(markerPath);
        if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 16384 || JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath))?.Identity != _identity)
            throw new IOException("The working-folder ownership marker is missing or invalid. No files were removed.");
        if (requireFolder && !Directory.Exists(WorkPath)) throw new IOException("The working folder is missing. The MP4 was not changed.");
    }

    private void RestoreMissing(Snapshot snapshot)
    {
        WorkspaceFileSystem.CheckNoLinks(_ownerPath);
        Directory.CreateDirectory(WorkPath);
        foreach (var directory in snapshot.Directories.OrderBy(p => p.Count(c => c == '/')))
        {
            var path = WorkspacePaths.Under(WorkPath, directory); WorkspaceFileSystem.CheckNoLinks(path); Directory.CreateDirectory(path);
        }
        foreach (var file in snapshot.Files)
        {
            var path = WorkspacePaths.Under(WorkPath, file.Path); WorkspaceFileSystem.CheckNoLinks(path);
            if (File.Exists(path)) continue;
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(file.Data); output.Flush(true); }
            File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc.UtcDateTime);
        }
    }

    private void FinishLock()
    {
        WorkspaceFileSystem.CheckNoLinks(_ownerPath);
        var markerPath = Path.Combine(_ownerPath, "workspace.json");
        var leasePath = Path.Combine(_ownerPath, "session.lock");
        if (Directory.EnumerateFileSystemEntries(_ownerPath).Any(path =>
                (path != markerPath && path != leasePath) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
            throw new IOException("Unexpected files remain in the working session. Close applications using it and review " + _ownerPath + ". The workspace is still unlocked.");
        var marker = File.ReadAllText(markerPath);
        _lease?.Dispose(); _lease = null;
        try
        {
            File.Delete(markerPath); File.Delete(leasePath);
            Directory.Delete(_ownerPath, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (Directory.Exists(_ownerPath))
                {
                    if (!File.Exists(markerPath)) File.WriteAllText(markerPath, marker);
                    _lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            { throw new IOException("Session cleanup failed. Review the retained plaintext folder at " + _ownerPath, new AggregateException(exception, restore)); }
            throw new IOException("Session cleanup failed. The workspace remains unlocked at " + _ownerPath, exception);
        }
        _session.Dispose(); IsOpen = false; FileCount = 0; TotalBytes = 0;
    }

    private static void DeleteTree(string root)
    {
        var tree = ReadTree(root);
        foreach (var relative in tree.Files)
        {
            var path = WorkspacePaths.Under(root, relative); WorkspaceFileSystem.CheckNoLinks(path); File.Delete(path);
        }
        foreach (var relative in tree.Directories.OrderByDescending(p => p.Count(c => c == '/')))
        {
            var path = WorkspacePaths.Under(root, relative); WorkspaceFileSystem.CheckNoLinks(path); Directory.Delete(path, false);
        }
        Directory.Delete(root, false);
    }

    private static void AddLegacy(WorkspaceData data, List<WorkspaceFile> files, HashSet<string> directories, List<byte[]> generated)
    {
        var used = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.ToArray()) foreach (var parent in WorkspacePaths.Parents(directory)) directories.Add(parent);
        foreach (var file in files) foreach (var parent in WorkspacePaths.Parents(file.Path)) directories.Add(parent);
        string Name(string name)
        {
            var safe = new string(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (safe.Length > 200)
            {
                var extension = Path.GetExtension(safe);
                if (extension.Length > 20) extension = "";
                safe = safe[..(200 - extension.Length)] + extension;
            }
            if (safe.Length == 0 || safe is "." or "..") safe = "Untitled";
            try { WorkspacePaths.Validate(safe); }
            catch (InvalidDataException) { safe = "_" + safe; }
            return safe;
        }
        string DirectoryName(string parent, string name)
        {
            var original = Name(name); var path = parent + original; var number = 2;
            while (used.Contains(path)) path = parent + original + $" ({number++})";
            directories.Add(path); return path + "/";
        }
        void Add(string parent, string name, byte[] bytes, DateTimeOffset timestamp)
        {
            var original = Name(name); var relative = parent + original; var number = 2;
            while (used.Contains(relative) || directories.Contains(relative))
                relative = parent + Path.GetFileNameWithoutExtension(original) + $" ({number++})" + Path.GetExtension(original);
            used.Add(relative); files.Add(new WorkspaceFile { Path = relative, Data = bytes, LastWriteTimeUtc = timestamp < DateTimeOffset.FromFileTime(0) ? DateTimeOffset.UtcNow : timestamp });
        }
        if (data.Photos.Count > 0)
        {
            var gallery = DirectoryName("", "Gallery");
            foreach (var photo in data.Photos)
            {
                var album = string.IsNullOrWhiteSpace(photo.Album) ? gallery : DirectoryName(gallery, photo.Album);
                Add(album, photo.Name, photo.Data, photo.ImportedAt);
            }
        }
        if (data.Notes.Count > 0)
        {
            var notes = DirectoryName("", "Notes");
            foreach (var note in data.Notes)
            {
                var bytes = Encoding.UTF8.GetBytes(note.Body); generated.Add(bytes); Add(notes, note.Title + ".txt", bytes, note.UpdatedAt);
            }
        }
        if (data.Contacts.Count > 0)
        {
            var contacts = DirectoryName("", "Contacts");
            foreach (var contact in data.Contacts)
            {
                var bytes = Encoding.UTF8.GetBytes($"Name: {contact.Name}\nPhone: {contact.Phone}\nEmail: {contact.Email}\n\n{contact.Notes}");
                generated.Add(bytes); Add(contacts, contact.Name + ".txt", bytes, data.CreatedAt);
            }
        }
        if (files.Count > WorkspaceStore.MaxFiles || directories.Count > WorkspaceStore.MaxDirectories || files.Sum(f => f.Data.LongLength) > WorkspaceStore.MaxTotalFileBytes)
            throw new InvalidDataException("The migrated workspace exceeds the file or folder storage limit.");
    }

    private sealed record Marker(string Identity, string FilePath, DateTimeOffset CreatedAt);
    private sealed record Tree(List<string> Files, List<string> Directories);
    private sealed class Snapshot : IDisposable
    {
        public List<WorkspaceFile> Files { get; set; } = [];
        public List<string> Directories { get; set; } = [];
        public List<FileStream> Handles { get; } = [];
        public List<SafeFileHandle> DirectoryHandles { get; } = [];
        public bool Committed { get; set; }
        public bool Matches(Snapshot other) => Directories.SequenceEqual(other.Directories, StringComparer.OrdinalIgnoreCase) &&
            Files.Count == other.Files.Count && Files.Zip(other.Files).All(pair =>
                string.Equals(pair.First.Path, pair.Second.Path, StringComparison.OrdinalIgnoreCase) &&
                pair.First.LastWriteTimeUtc == pair.Second.LastWriteTimeUtc && pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data));
        public void CheckUnchanged(string root)
        {
            var tree = ReadTree(root);
            if (!tree.Files.SequenceEqual(Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase) || !tree.Directories.SequenceEqual(Directories, StringComparer.OrdinalIgnoreCase))
                throw new IOException("The working folder changed while saving. Close applications using it and try again; the working folder was kept.");
            WorkspaceFileSystem.CheckStreams(root);
            foreach (var directory in Directories) WorkspaceFileSystem.CheckStreams(WorkspacePaths.Under(root, directory));
            foreach (var file in Files)
            {
                var path = WorkspacePaths.Under(root, file.Path);
                WorkspaceFileSystem.CheckStreams(path);
                var info = new FileInfo(path);
                if (info.Length != file.Data.LongLength || info.LastWriteTimeUtc != file.LastWriteTimeUtc.UtcDateTime)
                    throw new IOException("A file changed while saving. The working folder was kept: " + file.Path);
            }
        }
        public void CloseHandles()
        {
            foreach (var handle in Handles) handle.Dispose(); Handles.Clear();
            CloseDirectoryHandles();
        }
        public void CloseDirectoryHandles()
        {
            foreach (var handle in DirectoryHandles) handle.Dispose(); DirectoryHandles.Clear();
        }
        public void Dispose()
        {
            CloseHandles();
            if (!Committed)
            {
                foreach (var file in Files) CryptographicOperations.ZeroMemory(file.Data);
                Files.Clear(); Directories.Clear();
            }
        }
    }
}
