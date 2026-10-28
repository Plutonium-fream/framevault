using System.Security.Cryptography;
using System.Text;

namespace FrameVault.Core;

public sealed class WorkspaceSession : IDisposable
{
    private readonly object _gate = new();
    private readonly WorkspaceData _data;
    private readonly long _carrierLength;
    private byte[]? _key;
    private byte[] _salt;
    private byte[] _expectedEnvelopeHash;
    private long _expectedLength;
    private DateTime _expectedWriteTime;
    private bool _disposed;

    internal WorkspaceSession(string path, WorkspaceData data, byte[]? key, byte[] salt, long carrierLength,
        long expectedLength, DateTime expectedWriteTime, byte[] expectedEnvelopeHash)
    {
        FilePath = path;
        _data = data;
        _key = key;
        _salt = salt;
        _carrierLength = carrierLength;
        _expectedLength = expectedLength;
        _expectedWriteTime = expectedWriteTime;
        _expectedEnvelopeHash = expectedEnvelopeHash;
    }

    public string FilePath { get; }
    public string? LastSaveWarning { get; private set; }
    public bool RequiresReopen { get; private set; }
    internal Action<string, string, string>? ReplaceFileForTests { get; set; }
    public WorkspaceData Data
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _data; }
    }
    public bool IsProtected
    {
        get { ObjectDisposedException.ThrowIf(_disposed, this); return _key is not null; }
    }

    public void Save()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWritable();
            Persist(_key, _salt);
        }
    }

    public void ChangePassword(string? password)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWritable();
            WorkspaceStore.ValidatePassword(password);
            var nextSalt = new byte[16];
            byte[]? nextKey = null;
            try
            {
                if (!string.IsNullOrEmpty(password))
                {
                    RandomNumberGenerator.Fill(nextSalt);
                    nextKey = WorkspaceStore.DeriveKey(password, nextSalt);
                }
                Persist(nextKey, nextSalt);
            }
            catch
            {
                if (nextKey is not null) CryptographicOperations.ZeroMemory(nextKey);
                throw;
            }
            if (_key is not null) CryptographicOperations.ZeroMemory(_key);
            _key = nextKey;
            _salt = nextSalt;
        }
    }

    private void Persist(byte[]? key, byte[] salt)
    {
        LastSaveWarning = null;
        byte[]? envelope = null;
        string? temporary = null;
        var mutexName = (OperatingSystem.IsWindows() ? "Local\\" : "") + "FrameVault-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FilePath.ToUpperInvariant())));
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            envelope = WorkspaceStore.Encode(_data, key, salt);
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another FrameVault window is saving this workspace. Try again shortly.");
            using (var input = WorkspaceStore.OpenRead(FilePath))
            {
                var layout = WorkspaceStore.Scan(input);
                if (input.Length != _expectedLength || File.GetLastWriteTimeUtc(FilePath) != _expectedWriteTime ||
                    layout.WorkspaceOffset != _carrierLength)
                    throw Conflict();
                input.Position = _carrierLength + layout.WorkspaceBoxHeaderSize;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                long remaining = layout.WorkspacePayloadLength;
                while (remaining > 0)
                {
                    var count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                    if (count == 0) throw new EndOfStreamException("The workspace was truncated while reading.");
                    hash.AppendData(buffer, 0, count);
                    remaining -= count;
                }
                CryptographicOperations.ZeroMemory(buffer);
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), _expectedEnvelopeHash))
                    throw Conflict();
                temporary = WorkspaceStore.NewTemporaryPath(FilePath);
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                {
                    input.Position = 0;
                    remaining = _carrierLength;
                    while (remaining > 0)
                    {
                        var count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                        if (count == 0) throw new EndOfStreamException("The carrier video was truncated while reading.");
                        output.Write(buffer, 0, count);
                        remaining -= count;
                    }
                    WorkspaceStore.WriteWorkspaceBox(output, envelope);
                    output.Flush(true);
                }
            }
            // Keep a named old copy until the replacement has passed a complete read-back check.
            var nextLength = _carrierLength + 24 + envelope.Length;
            var nextHash = SHA256.HashData(envelope);
            var nextWriteTime = default(DateTime);
            var backup = Path.Combine(Path.GetDirectoryName(FilePath)!, $".framevault-{Guid.NewGuid():N}.backup.mp4");
            var replacement = temporary;
            temporary = null;
            LastSaveWarning = WorkspaceReplacement.Commit(replacement, FilePath, backup,
                path => VerifyEnvelopePath(path, _expectedLength, _expectedEnvelopeHash,
                    string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase) ? _expectedWriteTime : null),
                path =>
                {
                    var written = VerifyEnvelopePath(path, nextLength, nextHash, null);
                    if (string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase)) nextWriteTime = written;
                }, ReplaceFileForTests);
            _expectedLength = nextLength;
            _expectedWriteTime = nextWriteTime;
            _expectedEnvelopeHash = nextHash;
        }
        catch (WorkspaceReplacementException exception)
        {
            if (!exception.OriginalVerified) RequiresReopen = true;
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try { VerifyEnvelopePath(FilePath, _expectedLength, _expectedEnvelopeHash, _expectedWriteTime); }
            catch (Exception verify) when (verify is IOException or UnauthorizedAccessException or InvalidDataException) { RequiresReopen = true; }
            throw;
        }
        finally
        {
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
            WorkspaceStore.DeleteTemporary(temporary);
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static IOException Conflict() => new("This MP4 changed outside this window. Close and reopen it before saving; the file was not overwritten.");

    private void EnsureWritable()
    {
        if (RequiresReopen) throw new IOException("The MP4 protection and save state could not be verified. Keep the working folder for recovery and reopen a verified MP4 before saving or changing its password.");
    }

    internal void VerifySavedFile()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { VerifyEnvelopePath(FilePath, _expectedLength, _expectedEnvelopeHash, _expectedWriteTime); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            { RequiresReopen = true; throw; }
        }
    }

    private DateTime VerifyEnvelopePath(string path, long expectedLength, byte[] expectedHash, DateTime? expectedWriteTime)
    {
        using var input = WorkspaceStore.OpenRead(path);
        var layout = WorkspaceStore.Scan(input);
        var writeTime = File.GetLastWriteTimeUtc(path);
        if (input.Length != expectedLength || layout.WorkspaceOffset != _carrierLength ||
            (expectedWriteTime is not null && writeTime != expectedWriteTime)) throw Conflict();
        input.Position = _carrierLength + layout.WorkspaceBoxHeaderSize;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        try
        {
            long remaining = layout.WorkspacePayloadLength;
            while (remaining > 0)
            {
                var count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (count == 0) throw new EndOfStreamException("The saved workspace is truncated.");
                hash.AppendData(buffer, 0, count); remaining -= count;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash)) throw Conflict();
            return writeTime;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_key is not null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
            WorkspaceStore.ClearData(_data);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
