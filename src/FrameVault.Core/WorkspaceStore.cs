using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameVault.Core;

public static class WorkspaceStore
{
    public const int MaxPhotoBytes = 40 * 1024 * 1024;
    public const long MaxTotalPhotoBytes = 256L * 1024 * 1024;
    public const int MaxFileBytes = 128 * 1024 * 1024;
    public const long MaxTotalFileBytes = 256L * 1024 * 1024;
    public const int MaxFiles = 4096;
    public const int MaxDirectories = 4096;
    public const int MaxPayloadBytes = 384 * 1024 * 1024;
    public const int PasswordIterations = 600_000;
    internal const int EnvelopeHeaderSize = 64;
    internal static readonly byte[] UserType = Convert.FromHexString("D72A1598E35B4AD5AA342D903E15AB91");
    private static readonly byte[] Magic = "FRMVAULT"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static WorkspaceSession Create(string sourcePath, string destinationPath, string name, string? password = null)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a new MP4 file. The original video cannot be overwritten.");
        if (!string.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The workspace filename must end with .mp4.", nameof(destinationPath));
        if (File.Exists(destination))
            throw new IOException("That file already exists. Choose a new filename.");
        var data = new WorkspaceData { Name = name?.Trim() ?? "" };
        ValidateData(data);
        ValidatePassword(password);
        byte[] salt = new byte[16];
        byte[]? key = null;
        string? temporary = null;
        byte[]? envelope = null;
        try
        {
            if (!string.IsNullOrEmpty(password))
            {
                RandomNumberGenerator.Fill(salt);
                key = DeriveKey(password, salt);
            }
            envelope = Encode(data, key, salt);
            using var input = OpenRead(source);
            var layout = Scan(input);
            if (layout.WorkspaceOffset is not null)
                throw new InvalidDataException("This video already contains a FrameVault workspace. Open it instead.");
            if (layout.ZeroSizeBoxOffset is not null && input.Length - layout.ZeroSizeBoxOffset > uint.MaxValue)
                throw new InvalidDataException("The final video box exceeds 4 GiB and has no declared size. Remux this carrier to MP4 before using it.");
            temporary = NewTemporaryPath(destination);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
            {
                input.Position = 0;
                input.CopyTo(output);
                if (layout.ZeroSizeBoxOffset is long zeroOffset)
                {
                    // A size-zero box consumes EOF; give the copy a real size before appending.
                    Span<byte> boxSize = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(boxSize, checked((uint)(input.Length - zeroOffset)));
                    output.Position = zeroOffset;
                    output.Write(boxSize);
                    output.Position = output.Length;
                }
                WriteWorkspaceBox(output, envelope);
                output.Flush(true);
            }
            File.Move(temporary, destination, false);
            temporary = null;
            return new WorkspaceSession(destination, data, key, salt, input.Length,
                input.Length + 24 + envelope.Length, File.GetLastWriteTimeUtc(destination), SHA256.HashData(envelope));
        }
        catch
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally
        {
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
            DeleteTemporary(temporary);
        }
    }

    public static bool IsProtected(string path)
    {
        using var input = OpenRead(Path.GetFullPath(path));
        var layout = Scan(input);
        var header = ReadHeader(input, layout);
        return header.IsProtected;
    }

    public static WorkspaceSession Open(string path, string? password = null)
    {
        var fullPath = Path.GetFullPath(path);
        ValidatePassword(password);
        using var input = OpenRead(fullPath);
        var layout = Scan(input);
        var header = ReadHeader(input, layout);
        if (header.IsProtected && string.IsNullOrEmpty(password))
            throw new CryptographicException("This workspace needs a password.");
        byte[]? key = null;
        byte[]? envelope = null;
        byte[]? json = null;
        WorkspaceData? data = null;
        try
        {
            envelope = new byte[checked((int)layout.WorkspacePayloadLength)];
            input.Position = layout.WorkspaceOffset!.Value + layout.WorkspaceBoxHeaderSize;
            input.ReadExactly(envelope);
            var payload = envelope.AsSpan(EnvelopeHeaderSize, header.PayloadLength);
            var trailer = envelope.AsSpan(EnvelopeHeaderSize + header.PayloadLength);
            if (header.IsProtected)
            {
                key = DeriveKey(password!, header.Salt);
                json = new byte[header.PayloadLength];
                using var aes = new AesGcm(key, 16);
                try
                {
                    aes.Decrypt(header.Nonce, payload, trailer, json, envelope.AsSpan(0, EnvelopeHeaderSize));
                }
                catch (AuthenticationTagMismatchException exception)
                {
                    throw new CryptographicException("The password is incorrect or the workspace has been changed.", exception);
                }
            }
            else
            {
                var digest = SHA256.HashData(envelope.AsSpan(0, EnvelopeHeaderSize + header.PayloadLength));
                if (!CryptographicOperations.FixedTimeEquals(digest, trailer))
                    throw new InvalidDataException("The workspace checksum failed. The MP4 may be damaged.");
                json = payload.ToArray();
            }
            try
            {
                data = JsonSerializer.Deserialize<WorkspaceData>(json, JsonOptions)
                    ?? throw new InvalidDataException("The workspace has no contents.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The workspace contents are damaged or unsupported.", exception);
            }
            ValidateData(data);
            return new WorkspaceSession(fullPath, data, key, header.Salt, layout.WorkspaceOffset!.Value,
                input.Length, File.GetLastWriteTimeUtc(fullPath), SHA256.HashData(envelope));
        }
        catch
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (data is not null) ClearData(data);
            throw;
        }
        finally
        {
            if (json is not null) CryptographicOperations.ZeroMemory(json);
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
        }
    }

    internal static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);

    internal static void ValidatePassword(string? password)
    {
        if (password?.Length > 1024)
            throw new ArgumentException("Passwords must contain no more than 1024 characters.", nameof(password));
    }

    internal static byte[] Encode(WorkspaceData data, byte[]? key, byte[] salt)
    {
        ValidateData(data);
        var json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        try
        {
            if (json.Length > MaxPayloadBytes)
                throw new InvalidDataException("The workspace exceeds the 384 MiB storage limit.");
            var envelope = new byte[EnvelopeHeaderSize + json.Length + (key is null ? 32 : 16)];
            var header = envelope.AsSpan(0, EnvelopeHeaderSize);
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(8, 4), 2);
            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(12, 4), key is null ? 0u : 1u);
            BinaryPrimitives.WriteUInt64BigEndian(header.Slice(48, 8), checked((ulong)json.Length));
            var payload = envelope.AsSpan(EnvelopeHeaderSize, json.Length);
            var trailer = envelope.AsSpan(EnvelopeHeaderSize + json.Length);
            if (key is not null)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header.Slice(16, 4), PasswordIterations);
                salt.CopyTo(header.Slice(20, 16));
                var nonce = header.Slice(36, 12);
                RandomNumberGenerator.Fill(nonce);
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(nonce, json, payload, trailer, header);
            }
            else
            {
                json.CopyTo(payload);
                SHA256.HashData(envelope.AsSpan(0, EnvelopeHeaderSize + json.Length), trailer);
            }
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    internal static void ValidateData(WorkspaceData data)
    {
        long textLength = 0;
        var identifiers = new HashSet<Guid>();
        void Text(string? value, string label, int limit, bool required = false)
        {
            if (value is null || value.Length > limit || (required && string.IsNullOrWhiteSpace(value)))
                throw new InvalidDataException($"{label} must {(required ? "contain text and " : "")}be at most {limit} characters.");
            textLength += value.Length;
            if (textLength > 4L * 1024 * 1024)
                throw new InvalidDataException("Workspace text exceeds the 4 MiB character limit.");
        }
        void Id(string? id)
        {
            if (!Guid.TryParse(id, out var parsed) || parsed == Guid.Empty || !identifiers.Add(parsed))
                throw new InvalidDataException("Workspace items must have unique, valid identifiers.");
        }
        Text(data.Name, "Workspace name", 128, true);
        if (data.Photos is null || data.Notes is null || data.Contacts is null || data.Files is null || data.Directories is null)
            throw new InvalidDataException("The workspace is missing a collection.");
        if (data.Photos.Count > 4096 || data.Notes.Count > 10_000 || data.Contacts.Count > 10_000)
            throw new InvalidDataException("A workspace supports up to 4096 photos, 10000 notes and 10000 contacts.");
        long photoBytes = 0;
        foreach (var photo in data.Photos)
        {
            if (photo is null) throw new InvalidDataException("The workspace contains an invalid photo.");
            Id(photo.Id);
            Text(photo.Name, "Photo name", 255, true);
            Text(photo.Album, "Album name", 128);
            if (photo.Data is null || photo.Data.Length == 0 || photo.Data.Length > MaxPhotoBytes)
                throw new InvalidDataException("Each photo must contain data and be no larger than 40 MiB.");
            photoBytes += photo.Data.LongLength;
            if (photoBytes > MaxTotalPhotoBytes)
                throw new InvalidDataException("Photos exceed the workspace's 256 MiB total limit.");
        }
        foreach (var note in data.Notes)
        {
            if (note is null) throw new InvalidDataException("The workspace contains an invalid note.");
            Id(note.Id);
            Text(note.Title, "Note title", 256, true);
            Text(note.Body, "Note body", 1024 * 1024);
        }
        foreach (var contact in data.Contacts)
        {
            if (contact is null) throw new InvalidDataException("The workspace contains an invalid contact.");
            Id(contact.Id);
            Text(contact.Name, "Contact name", 256, true);
            Text(contact.Phone, "Phone number", 128);
            Text(contact.Email, "Email address", 320);
            Text(contact.Notes, "Contact notes", 64 * 1024);
        }
        if (data.Files.Count > MaxFiles || data.Directories.Count > MaxDirectories)
            throw new InvalidDataException("A workspace supports up to 4096 files and 4096 folders.");
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in data.Directories)
        {
            Text(directory, "Folder path", 1024, true);
            WorkspacePaths.Validate(directory);
            if (!directoryPaths.Add(directory)) throw new InvalidDataException("Folder paths must be unique, including letter case.");
        }
        long fileBytes = photoBytes;
        foreach (var file in data.Files)
        {
            if (file is null) throw new InvalidDataException("The workspace contains an invalid file.");
            Text(file.Path, "File path", 1024, true);
            WorkspacePaths.Validate(file.Path);
            if (!filePaths.Add(file.Path)) throw new InvalidDataException("File paths must be unique, including letter case.");
            if (file.Data is null || file.Data.Length > MaxFileBytes)
                throw new InvalidDataException("Each file must be no larger than 128 MiB.");
            if (file.LastWriteTimeUtc.UtcDateTime < DateTime.FromFileTimeUtc(0))
                throw new InvalidDataException("File dates must be on or after 1 January 1601.");
            fileBytes += file.Data.LongLength;
            if (fileBytes > MaxTotalFileBytes) throw new InvalidDataException("Workspace files exceed the 256 MiB total limit.");
        }
        foreach (var path in filePaths.Concat(directoryPaths))
        {
            if (directoryPaths.Contains(path) && filePaths.Contains(path)) throw new InvalidDataException("A file and folder cannot use the same path.");
            foreach (var parent in WorkspacePaths.Parents(path))
                if (filePaths.Contains(parent)) throw new InvalidDataException("A file cannot be used as a parent folder.");
        }
    }

    internal static void ClearData(WorkspaceData data)
    {
        foreach (var photo in data.Photos ?? [])
        {
            if (photo is null) continue;
            if (photo.Data is not null) CryptographicOperations.ZeroMemory(photo.Data);
            photo.Name = photo.Album = "";
        }
        foreach (var note in data.Notes ?? [])
            if (note is not null) note.Title = note.Body = "";
        foreach (var contact in data.Contacts ?? [])
            if (contact is not null) contact.Name = contact.Phone = contact.Email = contact.Notes = "";
        data.Photos?.Clear();
        data.Notes?.Clear();
        data.Contacts?.Clear();
        foreach (var file in data.Files ?? [])
        {
            if (file is null) continue;
            if (file.Data is not null) CryptographicOperations.ZeroMemory(file.Data);
            file.Path = "";
        }
        data.Files?.Clear();
        data.Directories?.Clear();
        data.Name = "";
    }

    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);

    internal static string NewTemporaryPath(string destination) =>
        Path.Combine(Path.GetDirectoryName(destination)!, $".framevault-{Guid.NewGuid():N}.tmp");

    internal static void DeleteTemporary(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void WriteWorkspaceBox(Stream output, byte[] envelope)
    {
        Span<byte> boxHeader = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(boxHeader, checked((uint)(24 + envelope.Length)));
        "uuid"u8.CopyTo(boxHeader.Slice(4, 4));
        UserType.CopyTo(boxHeader.Slice(8, 16));
        output.Write(boxHeader);
        output.Write(envelope);
    }

    internal sealed record Layout(long? WorkspaceOffset, int WorkspaceBoxHeaderSize, long WorkspacePayloadLength, long? ZeroSizeBoxOffset);

    internal static Layout Scan(FileStream input)
    {
        long? workspaceOffset = null;
        long? zeroSizeBoxOffset = null;
        int workspaceHeaderSize = 0;
        long workspacePayloadLength = 0;
        var hasFtyp = false;
        var hasMoov = false;
        var hasMdat = false;
        long offset = 0;
        var boxCount = 0;
        Span<byte> header = stackalloc byte[32];
        while (offset < input.Length)
        {
            if (++boxCount > 100_000 || input.Length - offset < 8)
                throw new InvalidDataException("The MP4 has a damaged box structure.");
            input.Position = offset;
            input.ReadExactly(header.Slice(0, 8));
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header.Slice(4, 4));
            var headerSize = 8;
            ulong unsignedSize = size32;
            if (size32 == 1)
            {
                if (input.Length - offset < 16) throw new InvalidDataException("The MP4 has a truncated extended box header.");
                input.ReadExactly(header.Slice(8, 8));
                unsignedSize = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                unsignedSize = checked((ulong)(input.Length - offset));
                zeroSizeBoxOffset = offset;
            }
            if (unsignedSize > long.MaxValue || unsignedSize < (ulong)headerSize || unsignedSize > (ulong)(input.Length - offset))
                throw new InvalidDataException("An MP4 box has an invalid size.");
            var size = (long)unsignedSize;
            if (type == "uuid")
            {
                if (size < headerSize + 16) throw new InvalidDataException("The MP4 has a truncated UUID box.");
                input.ReadExactly(header.Slice(headerSize, 16));
                if (header.Slice(headerSize, 16).SequenceEqual(UserType))
                {
                    if (workspaceOffset is not null)
                        throw new InvalidDataException("The MP4 contains duplicate FrameVault workspace boxes.");
                    if (offset + size != input.Length)
                        throw new InvalidDataException("The FrameVault workspace must be the final MP4 box.");
                    workspaceOffset = offset;
                    workspaceHeaderSize = headerSize + 16;
                    workspacePayloadLength = size - workspaceHeaderSize;
                    if (workspacePayloadLength < EnvelopeHeaderSize + 16 || workspacePayloadLength > (long)EnvelopeHeaderSize + MaxPayloadBytes + 32)
                        throw new InvalidDataException("The workspace payload is truncated or exceeds the 384 MiB limit.");
                }
            }
            else if (type == "ftyp")
            {
                if (hasFtyp || size < headerSize + 8 || (size - headerSize - 8) % 4 != 0)
                    throw new InvalidDataException("The MP4 file-type box is invalid.");
                hasFtyp = true;
            }
            else if (type == "moov") hasMoov = true;
            else if (type == "mdat" && size > headerSize) hasMdat = true;
            offset += size;
        }
        if (!hasFtyp || !hasMoov || !hasMdat)
            throw new InvalidDataException("Choose an MP4 video containing ftyp, moov and nonempty mdat boxes.");
        return new Layout(workspaceOffset, workspaceHeaderSize, workspacePayloadLength, zeroSizeBoxOffset);
    }

    private sealed record EnvelopeHeader(bool IsProtected, byte[] Salt, byte[] Nonce, int PayloadLength);

    private static EnvelopeHeader ReadHeader(FileStream input, Layout layout)
    {
        if (layout.WorkspaceOffset is not long workspaceOffset)
            throw new InvalidDataException("This MP4 has no FrameVault workspace. Use Create Workspace to make one.");
        Span<byte> header = stackalloc byte[EnvelopeHeaderSize];
        input.Position = workspaceOffset + layout.WorkspaceBoxHeaderSize;
        input.ReadExactly(header);
        if (!header.Slice(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4)) is not (1 or 2))
            throw new InvalidDataException("This FrameVault workspace version is unsupported.");
        var flags = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(12, 4));
        if (flags > 1 || !header.Slice(56, 8).SequenceEqual(new byte[8]))
            throw new InvalidDataException("The workspace header is invalid or unsupported.");
        var isProtected = flags == 1;
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var length = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(48, 8));
        if (length == 0 || length > MaxPayloadBytes || (long)length + EnvelopeHeaderSize + (isProtected ? 16 : 32) != layout.WorkspacePayloadLength)
            throw new InvalidDataException("The workspace has an invalid payload length.");
        if ((isProtected && iterations != PasswordIterations) || (!isProtected && (iterations != 0 || !header.Slice(20, 28).SequenceEqual(new byte[28]))))
            throw new InvalidDataException("The workspace uses unsupported password parameters.");
        return new EnvelopeHeader(isProtected, header.Slice(20, 16).ToArray(), header.Slice(36, 12).ToArray(), checked((int)length));
    }
}
