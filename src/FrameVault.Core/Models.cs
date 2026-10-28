using System.Text.Json.Serialization;

namespace FrameVault.Core;

public sealed class WorkspaceData
{
    [JsonRequired] public string Name { get; set; } = "My workspace";
    [JsonRequired] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonRequired] public List<PhotoItem> Photos { get; set; } = [];
    [JsonRequired] public List<NoteItem> Notes { get; set; } = [];
    [JsonRequired] public List<ContactItem> Contacts { get; set; } = [];
    public List<WorkspaceFile> Files { get; set; } = [];
    public List<string> Directories { get; set; } = [];
}

public sealed class WorkspaceFile
{
    [JsonRequired] public string Path { get; set; } = "";
    [JsonRequired] public byte[] Data { get; set; } = [];
    [JsonRequired] public DateTimeOffset LastWriteTimeUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PhotoItem
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string Name { get; set; } = "Photo";
    [JsonRequired] public string Album { get; set; } = "";
    [JsonRequired] public byte[] Data { get; set; } = [];
    [JsonRequired] public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NoteItem
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string Title { get; set; } = "Untitled note";
    [JsonRequired] public string Body { get; set; } = "";
    [JsonRequired] public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ContactItem
{
    [JsonRequired] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonRequired] public string Name { get; set; } = "New contact";
    [JsonRequired] public string Phone { get; set; } = "";
    [JsonRequired] public string Email { get; set; } = "";
    [JsonRequired] public string Notes { get; set; } = "";
}
