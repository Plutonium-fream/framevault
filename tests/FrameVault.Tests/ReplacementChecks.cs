using System.Text;
using FrameVault.Core;

internal static class ReplacementChecks
{
    public static void Run(string testRoot, Action<string, Action> test)
    {
        var root = Path.Combine(testRoot, "replacement-checks"); Directory.CreateDirectory(root);
        var original = Encoding.UTF8.GetBytes("Verified original MP4 fixture");
        var replacement = Encoding.UTF8.GetBytes("Verified replacement MP4 fixture");
        var sequence = 0;
        (string Source, string Destination, string Backup) Fixture(string name)
        {
            var directory = Path.Combine(root, $"{++sequence:D2}-{name}"); Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "replacement.tmp"); var destination = Path.Combine(directory, "workspace.mp4");
            File.WriteAllBytes(source, replacement); File.WriteAllBytes(destination, original);
            return (source, destination, Path.Combine(directory, "previous.backup.mp4"));
        }
        void VerifyOld(string path) => Verify(path, original);
        void VerifyNew(string path) => Verify(path, replacement);

        test("Verified atomic replacement removes its obsolete backup after success", () =>
        {
            var files = Fixture("success");
            var warning = WorkspaceReplacement.Commit(files.Source, files.Destination, files.Backup, VerifyOld, VerifyNew);
            VerifyNew(files.Destination);
            Check(string.IsNullOrEmpty(warning) && !File.Exists(files.Source) && !File.Exists(files.Backup), "Clean replacement left an obsolete source or backup.");
        });

        test("Transient atomic replacement failures retry with source and destination verification", () =>
        {
            var files = Fixture("transient"); var attempts = 0; var oldChecks = 0; var newChecks = 0;
            WorkspaceReplacement.Commit(files.Source, files.Destination, files.Backup,
                path => { oldChecks++; VerifyOld(path); }, path => { newChecks++; VerifyNew(path); },
                (source, destination, backup) =>
                {
                    if (++attempts < 4) throw Error(1175);
                    File.Replace(source, destination, backup, true);
                });
            Check(attempts == 4 && oldChecks >= 4 && newChecks >= 4, "Transient retries skipped verification or exceeded their bound.");
            VerifyNew(files.Destination);
        });

        test("Partial Windows 1176 and 1177 replacement failures restore the verified original", () =>
        {
            foreach (var code in new[] { 1176, 1177 })
            {
                var files = Fixture("partial-" + code);
                Reject(() => WorkspaceReplacement.Commit(files.Source, files.Destination, files.Backup, VerifyOld, VerifyNew,
                    (source, destination, backup) => { File.Move(destination, backup); throw Error(code); }), "Partial replacement was incorrectly reported successful.");
                VerifyOld(files.Destination);
                VerifyNew(files.Source);
            }
        });

        test("Uncertain replacement failures preserve an external destination and both recoverable files", () =>
        {
            var files = Fixture("uncertain");
            Reject(() => WorkspaceReplacement.Commit(files.Source, files.Destination, files.Backup, VerifyOld, VerifyNew,
                (source, destination, backup) =>
                {
                    File.Move(destination, backup);
                    File.WriteAllText(destination, "An unexpected external writer");
                    throw Error(1177);
                }), "Uncertain replacement was incorrectly reported successful.");
            Check(File.ReadAllText(files.Destination) == "An unexpected external writer", "Recovery overwrote an unverified external destination.");
            VerifyOld(files.Backup); VerifyNew(files.Source);
        });

        test("Failed replacement readback retains the verified original backup", () =>
        {
            var files = Fixture("readback");
            Reject(() => WorkspaceReplacement.Commit(files.Source, files.Destination, files.Backup, VerifyOld, VerifyNew,
                (source, destination, backup) =>
                {
                    File.Replace(source, destination, backup, true);
                    File.WriteAllText(destination, "Unexpected changed contents");
                }), "Unverified new output was accepted.");
            VerifyOld(files.Backup);
            Check(File.Exists(files.Destination), "Readback failure silently deleted an uncertain destination.");
        });

        test("Backup cleanup failure warns while retaining the committed replacement and previous copy", () =>
        {
            var files = Fixture("cleanup-warning"); FileStream? heldBackup = null;
            try
            {
                var warning = WorkspaceReplacement.Commit(files.Source, files.Destination, files.Backup, VerifyOld, VerifyNew,
                    (source, destination, backup) =>
                    {
                        File.Replace(source, destination, backup, true);
                        heldBackup = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
                    });
                VerifyNew(files.Destination); VerifyOld(files.Backup);
                Check(!string.IsNullOrWhiteSpace(warning) && warning.Contains(files.Backup, StringComparison.Ordinal), "Cleanup failure did not identify the retained backup to the caller.");
            }
            finally { heldBackup?.Dispose(); }
        });
    }

    private static IOException Error(int code) => new("Simulated Windows replacement failure", unchecked((int)(0x80070000u | (uint)code)));
    private static void Verify(string path, byte[] expected)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(expected)) throw new InvalidDataException("The expected file could not be verified: " + path);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new Exception(message);
    }
}
