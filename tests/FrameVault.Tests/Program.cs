using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FrameVault.Core;

if (args.Length == 4 && args[0] == "--retained-folder-fixture")
{
    using var fixtureSession = WorkspaceStore.Create(args[1], args[2], "Recovery fixture", "fixture password");
    var fixtureFolder = WorkspaceFolder.Open(fixtureSession, args[3]);
    File.WriteAllText(Path.Combine(fixtureFolder.WorkPath, "recover-me.txt"), "Unsaved work from an exited process");
    Console.WriteLine(fixtureFolder.WorkPath);
    Environment.Exit(0);
}

var testRoot = Path.Combine(AppContext.BaseDirectory, "FrameVault-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var carrier = Path.Combine(AppContext.BaseDirectory, "video-fixture.mp4");
if (!File.Exists(carrier)) throw new FileNotFoundException("The test carrier is missing.", carrier);
var sourceBytes = File.ReadAllBytes(carrier);
var photoBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6O8sAAAAASUVORK5CYII=");
var failures = new List<string>();
var passed = 0;
string NewPath(string name) => Path.Combine(testRoot, name + ".mp4");
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
void Reject(Action operation, string message)
{
    try { operation(); }
    catch (Exception e) when (e is IOException or InvalidDataException or CryptographicException or ArgumentException or ObjectDisposedException or InvalidOperationException) { return; }
    throw new Exception(message);
}
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures.Add(name + ": " + e); Console.Error.WriteLine("FAIL " + name + ": " + e); }
}
void Populate(WorkspaceSession session)
{
    session.Data.Photos.Add(new PhotoItem { Name = "pixel.png", Album = "Research", Data = photoBytes.ToArray(), ImportedAt = DateTimeOffset.Parse("2026-10-25T09:00:00Z") });
    session.Data.Notes.Add(new NoteItem { Title = "Rare secret title 89df3", Body = "A note with Unicode: 東京, café.\nA second line.", UpdatedAt = DateTimeOffset.Parse("2026-10-25T10:00:00Z") });
    session.Data.Contacts.Add(new ContactItem { Name = "Ada Lovelace", Phone = "+31 20 123 4567", Email = "ada@example.test", Notes = "Preferred contact: email" });
}
void CheckPopulated(WorkspaceSession session)
{
    Check(session.Data.Name == "Portable study", "Workspace name lost.");
    Check(session.Data.Photos.Count == 1 && session.Data.Photos[0].Data.SequenceEqual(photoBytes), "Photo bytes changed.");
    Check(session.Data.Photos[0].Album == "Research" && session.Data.Photos[0].Name == "pixel.png", "Photo organization lost.");
    Check(session.Data.Notes.Single().Body.Contains("東京, café."), "Note text changed.");
    Check(session.Data.Contacts.Single().Email == "ada@example.test", "Contact fields lost.");
}
string? FrameHash(string path)
{
    var start = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { "-v", "error", "-i", path, "-map", "0:v:0", "-f", "hash", "-hash", "sha256", "-" }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception("Video decode failed: " + stderr);
    return stdout.Trim();
}

try
{
    Test("Plain workspace round-trips every app from only the MP4", () =>
    {
        var path = NewPath("plain");
        using (var session = WorkspaceStore.Create(carrier, path, "Portable study", null)) { Populate(session); session.Save(); }
        var moved = NewPath("moved-to-another-folder");
        File.Move(path, moved);
        Check(!WorkspaceStore.IsProtected(moved), "Plain workspace marked protected.");
        using var opened = WorkspaceStore.Open(moved);
        CheckPopulated(opened);
        Check(opened.Data.CreatedAt != default, "Creation date missing.");
    });
    Test("Create and save preserve source and all carrier bytes", () =>
    {
        var path = NewPath("preservation");
        using var session = WorkspaceStore.Create(carrier, path, "Video", null);
        Populate(session); session.Save();
        Check(File.ReadAllBytes(carrier).SequenceEqual(sourceBytes), "Source was modified.");
        Check(File.ReadAllBytes(path).AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes), "Carrier prefix changed.");
    });
    Test("Create never overwrites an existing destination or the source", () =>
    {
        var path = NewPath("existing"); File.WriteAllText(path, "keep me");
        Reject(() => WorkspaceStore.Create(carrier, path, "Existing", null).Dispose(), "Existing destination overwritten.");
        Check(File.ReadAllText(path) == "keep me", "Existing bytes changed.");
        Reject(() => WorkspaceStore.Create(carrier, carrier, "Same", null).Dispose(), "Source overwrite accepted.");
        Check(File.ReadAllBytes(carrier).SequenceEqual(sourceBytes), "Source overwrite changed bytes.");
    });
    Test("Protected workspace authenticates password and hides content", () =>
    {
        var path = NewPath("protected");
        using (var session = WorkspaceStore.Create(carrier, path, "Portable study", "correct horse battery staple")) { Populate(session); session.Save(); }
        Check(WorkspaceStore.IsProtected(path), "Protected flag missing.");
        Reject(() => WorkspaceStore.Open(path, "wrong password").Dispose(), "Wrong password accepted.");
        Reject(() => WorkspaceStore.Open(path).Dispose(), "Protected workspace opened without password.");
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Check(!raw.Contains("Rare secret title 89df3") && !raw.Contains("ada@example.test") && !raw.Contains("Portable study"), "Protected metadata leaked.");
        using var opened = WorkspaceStore.Open(path, "correct horse battery staple"); CheckPopulated(opened);
    });
    Test("Ciphertext tampering fails authentication", () =>
    {
        var path = NewPath("tamper");
        using (var session = WorkspaceStore.Create(carrier, path, "Protected", "password")) { Populate(session); session.Save(); }
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 0x01; File.WriteAllBytes(path, bytes);
        Reject(() => WorkspaceStore.Open(path, "password").Dispose(), "Tampered ciphertext accepted.");
    });
    Test("Plain checksum and authenticated envelope header reject tampering", () =>
    {
        var plain = NewPath("plain-tamper"); using (var session = WorkspaceStore.Create(carrier, plain, "Plain", null)) { }
        var bytes = File.ReadAllBytes(plain); bytes[^1] ^= 0x80; File.WriteAllBytes(plain, bytes);
        Reject(() => WorkspaceStore.Open(plain).Dispose(), "Tampered plain checksum accepted.");
        var path = NewPath("header-tamper"); using (var session = WorkspaceStore.Create(carrier, path, "Header", "password")) { }
        var original = File.ReadAllBytes(path);
        foreach (var position in new[] { 8, 12, 16, 20, 36, 48, 56 })
        {
            var modified = original.ToArray(); modified[sourceBytes.Length + 24 + position] ^= 0x01; File.WriteAllBytes(path, modified);
            Reject(() => WorkspaceStore.Open(path, "password").Dispose(), "Modified envelope header accepted at " + position);
        }
    });
    Test("Each encrypted save uses fresh ciphertext", () =>
    {
        var path = NewPath("fresh-nonce");
        using var session = WorkspaceStore.Create(carrier, path, "Nonce", "password");
        var before = File.ReadAllBytes(path); session.Save(); var after = File.ReadAllBytes(path);
        Check(!before.SequenceEqual(after), "Repeated save reused the same ciphertext.");
    });
    Test("Password can be added, changed, and removed with data preserved", () =>
    {
        var path = NewPath("password-change");
        using (var session = WorkspaceStore.Create(carrier, path, "Portable study", null))
        {
            Populate(session); session.Save(); session.ChangePassword("first password"); Check(session.IsProtected, "Adding password failed.");
        }
        using (var session = WorkspaceStore.Open(path, "first password")) { CheckPopulated(session); session.ChangePassword("second password"); }
        Reject(() => WorkspaceStore.Open(path, "first password").Dispose(), "Old password still valid.");
        using (var session = WorkspaceStore.Open(path, "second password")) { CheckPopulated(session); session.ChangePassword(null); Check(!session.IsProtected, "Removing password failed."); }
        Check(!WorkspaceStore.IsProtected(path), "Removed password flag persisted.");
        using var plain = WorkspaceStore.Open(path); CheckPopulated(plain);
    });
    Test("Disposal clears live data and prevents further access", () =>
    {
        var session = WorkspaceStore.Create(carrier, NewPath("dispose"), "Lock", "password"); Populate(session);
        var data = session.Data; var bytes = data.Photos[0].Data;
        session.Dispose();
        Check(data.Photos.Count == 0 && data.Notes.Count == 0 && data.Contacts.Count == 0, "Data collections retained after lock.");
        Check(bytes.All(b => b == 0), "Photo bytes retained after lock.");
        Reject(() => { _ = session.Data; }, "Disposed session exposes data.");
        Reject(session.Save, "Disposed session allows save.");
    });
    Test("Failed password change preserves prior password and session key", () =>
    {
        var path = NewPath("failed-password-change"); using var session = WorkspaceStore.Create(carrier, path, "Protected", "old password");
        var before = File.ReadAllBytes(path);
        using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => session.ChangePassword("new password"), "Locked destination allowed password change.");
        Check(File.ReadAllBytes(path).SequenceEqual(before) && session.IsProtected, "Failed password change damaged prior state.");
        session.Save();
        using var opened = WorkspaceStore.Open(path, "old password");
        Reject(() => WorkspaceStore.Open(path, "new password").Dispose(), "Failed password became active.");
    });
    Test("Competing sessions reject lost-update writes", () =>
    {
        var path = NewPath("conflict");
        using var first = WorkspaceStore.Create(carrier, path, "Concurrent", null);
        using var second = WorkspaceStore.Open(path);
        first.Data.Notes.Add(new NoteItem { Title = "First writer", Body = "Keep this" }); first.Save();
        second.Data.Notes.Add(new NoteItem { Title = "Second writer", Body = "Must not overwrite" });
        Reject(second.Save, "Concurrent stale write accepted.");
        using var check = WorkspaceStore.Open(path); Check(check.Data.Notes.Single().Title == "First writer", "First write was lost.");
    });
    Test("Failed atomic replace leaves original MP4 intact", () =>
    {
        var path = NewPath("locked-output"); using var session = WorkspaceStore.Create(carrier, path, "Atomic", null);
        var before = File.ReadAllBytes(path);
        using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Data.Notes.Add(new NoteItem { Title = "Pending", Body = "Not saved" });
            Reject(session.Save, "A locked destination unexpectedly replaced.");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Failed save changed original.");
    });
    Test("Invalid model data is rejected before replacing saved file", () =>
    {
        var path = NewPath("invalid-data"); using var session = WorkspaceStore.Create(carrier, path, "Validation", null);
        var before = File.ReadAllBytes(path); session.Data.Notes.Add(new NoteItem { Title = "" });
        Reject(session.Save, "Empty note title accepted.");
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Invalid model changed saved file.");
    });
    Test("A normal video is not mistaken for a workspace", () => Reject(() => WorkspaceStore.Open(carrier).Dispose(), "Ordinary MP4 opened as workspace."));
    Test("Malformed and truncated MP4 boxes are rejected", () =>
    {
        var path = NewPath("malformed");
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[7], new byte[] { 0, 0, 0, 4, 102, 116, 121, 112 }, sourceBytes[..^1] })
        {
            File.WriteAllBytes(path, bytes); Reject(() => WorkspaceStore.Create(path, NewPath("bad-output-" + Guid.NewGuid().ToString("N")), "Bad", null).Dispose(), "Malformed MP4 accepted.");
        }
    });
    Test("Duplicate FrameVault boxes and oversized envelope lengths are rejected", () =>
    {
        var path = NewPath("duplicate"); using (var session = WorkspaceStore.Create(carrier, path, "Bounds", null)) { }
        var original = File.ReadAllBytes(path);
        var duplicate = original.Concat(original.Skip(sourceBytes.Length)).ToArray(); File.WriteAllBytes(path, duplicate);
        Reject(() => WorkspaceStore.Open(path).Dispose(), "Duplicate UUID box accepted.");
        var oversized = original.ToArray(); BinaryPrimitives.WriteUInt64BigEndian(oversized.AsSpan(sourceBytes.Length + 24 + 48, 8), ulong.MaxValue); File.WriteAllBytes(path, oversized);
        Reject(() => WorkspaceStore.Open(path).Dispose(), "Oversized payload length accepted.");
    });
    Test("Unrelated extended-size MP4 boxes survive workspace creation", () =>
    {
        var source = NewPath("extended-source"); var extendedBox = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(extendedBox.AsSpan(0, 4), 1); Encoding.ASCII.GetBytes("free").CopyTo(extendedBox, 4);
        BinaryPrimitives.WriteUInt64BigEndian(extendedBox.AsSpan(8, 8), 20); extendedBox[16] = 42;
        var input = sourceBytes.Concat(extendedBox).ToArray(); File.WriteAllBytes(source, input);
        var path = NewPath("extended-workspace"); using var session = WorkspaceStore.Create(source, path, "Extended", null); session.Save();
        Check(File.ReadAllBytes(path).AsSpan(0, input.Length).SequenceEqual(input), "Unrelated extended-size box changed.");
    });
    Test("Terminal zero-size box is safely normalized in the new copy", () =>
    {
        var bytes = sourceBytes.ToArray(); var offset = 0; var last = 0;
        while (offset < bytes.Length)
        {
            last = offset; var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            Check(size >= 8 && size <= int.MaxValue, "Unexpected fixture box."); offset += (int)size;
        }
        Check(offset == bytes.Length, "Fixture box boundaries invalid.");
        Array.Clear(bytes, last, 4); var source = NewPath("zero-source"); File.WriteAllBytes(source, bytes);
        var path = NewPath("zero-workspace"); using (var session = WorkspaceStore.Create(source, path, "Zero size", null)) { session.Save(); }
        Check(File.ReadAllBytes(source).SequenceEqual(bytes), "Zero-size source changed.");
        using var opened = WorkspaceStore.Open(path); Check(opened.Data.Name == "Zero size", "Zero-size workspace failed to reopen.");
        var output = File.ReadAllBytes(path); Check(BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(last, 4)) == bytes.Length - last, "Zero-size box not normalized.");
        Check(output.AsSpan(last + 4, bytes.Length - last - 4).SequenceEqual(bytes.AsSpan(last + 4)), "Video data changed on normalization.");
    });
    Test("Saved workspace remains fully playable with identical decoded frames", () =>
    {
        var path = NewPath("playable"); using (var session = WorkspaceStore.Create(carrier, path, "Playback", "password")) { Populate(session); session.Save(); }
        Check(FrameHash(path) == FrameHash(carrier), "MP4 decoded frames changed after storing workspace.");
    });
    FolderChecks.Run(testRoot, carrier, sourceBytes, Test);
    ReplacementChecks.Run(testRoot, Test);
    ProtectionStateChecks.Run(testRoot, carrier, Test);
    Check(File.ReadAllBytes(carrier).SequenceEqual(sourceBytes), "Test carrier was modified.");
}
finally
{
    Directory.Delete(testRoot, true);
}
Console.WriteLine($"{passed} passed; {failures.Count} failed.");
if (failures.Count > 0) { foreach (var failure in failures) Console.Error.WriteLine(failure); Environment.ExitCode = 1; }
