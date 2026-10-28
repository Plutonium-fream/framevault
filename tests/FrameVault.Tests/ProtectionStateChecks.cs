using System.Text;
using FrameVault.Core;

internal static class ProtectionStateChecks
{
    internal static void Run(string testRoot, string carrier, Action<string, Action> test)
    {
        var root = Path.Combine(testRoot, "protection-state-checks");
        Directory.CreateDirectory(root);
        var sequence = 0;
        void InterruptedPasswordChange(string? nextPassword)
        {
            var directory = Path.Combine(root, (++sequence).ToString()); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "workspace.mp4");
            using var session = WorkspaceStore.Create(carrier, path, "Protection state", "old password");
            session.Data.Files.Add(new WorkspaceFile { Path = "keep.txt", Data = Encoding.UTF8.GetBytes("Verified saved contents") });
            session.Save();
            var previous = File.ReadAllBytes(path);
            var workingRoot = Path.Combine(directory, "unlocked");
            var folder = WorkspaceFolder.Open(session, workingRoot);
            File.WriteAllText(Path.Combine(folder.WorkPath, "keep.txt"), "Latest unsaved working copy");
            FileStream? heldDestination = null;
            string? backupPath = null;
            try
            {
                session.ReplaceFileForTests = (source, destination, backup) =>
                {
                    File.Replace(source, destination, backup, true);
                    backupPath = backup;
                    heldDestination = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
                };
                Reject(() => session.ChangePassword(nextPassword), "A failed post-replacement readback was reported successful.");
                Check(session.RequiresReopen, "The session still claimed a verified protection state after installing an unverified replacement.");
                Check(folder.IsOpen && File.ReadAllText(Path.Combine(folder.WorkPath, "keep.txt")) == "Latest unsaved working copy", "An uncertain password change removed the working copy.");
            }
            finally
            {
                heldDestination?.Dispose(); session.ReplaceFileForTests = null;
            }
            try
            {
                Check(backupPath is not null && File.ReadAllBytes(backupPath).SequenceEqual(previous), "The previous verified MP4 was not retained for recovery.");
                Check(WorkspaceStore.IsProtected(path) == (nextPassword is not null), "The injected replacement did not reach the intended on-disk protection state.");
                using (var installed = WorkspaceStore.Open(path, nextPassword))
                    Check(Encoding.UTF8.GetString(installed.Data.Files.Single().Data) == "Verified saved contents", "The new on-disk copy was corrupted by failed verification.");
                var after = File.ReadAllBytes(path);
                Reject(session.Save, "An uncertain session allowed another save.");
                Reject(() => session.ChangePassword("another password"), "An uncertain session allowed another password change.");
                Reject(folder.SaveAndLock, "An uncertain session reported a successful Save and Lock.");
                Check(File.ReadAllBytes(path).SequenceEqual(after) && folder.IsOpen, "A blocked operation changed the uncertain MP4 or locked the working copy.");
                folder.LeaveForRecovery();
                Check(!folder.IsOpen && session.RequiresReopen, "Closing for recovery lost the uncertainty flag or left the session active.");
                Reject(() => { _ = session.Data; }, "Closing for recovery retained decrypted session data.");
                Check(WorkspaceFolder.FindRetainedFolders(workingRoot).Contains(folder.WorkPath), "The retained working copy could not be discovered after closing for recovery.");
                Check(File.ReadAllText(Path.Combine(folder.WorkPath, "keep.txt")) == "Latest unsaved working copy", "Closing for recovery changed the latest working file.");
            }
            finally { if (folder.IsOpen) folder.LeaveForRecovery(); }
        }

        test("Failed password removal readback marks protection unknown and retains both recovery copies", () => InterruptedPasswordChange(null));
        test("Failed new-password readback blocks further writes until the MP4 is reopened", () => InterruptedPasswordChange("new password"));
        test("Leaving for recovery releases the key and lease without deleting unsaved working files", () =>
        {
            var directory = Path.Combine(root, "leave-for-recovery"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "workspace.mp4");
            using var session = WorkspaceStore.Create(carrier, path, "Recovery exit", "password");
            var before = File.ReadAllBytes(path);
            var workingRoot = Path.Combine(directory, "unlocked");
            var folder = WorkspaceFolder.Open(session, workingRoot);
            var file = Path.Combine(folder.WorkPath, "unsaved.txt"); File.WriteAllText(file, "Keep every unsaved byte");
            Check(!WorkspaceFolder.FindRetainedFolders(workingRoot).Contains(folder.WorkPath), "An active leased folder was incorrectly listed as abandoned.");
            folder.LeaveForRecovery();
            Check(!folder.IsOpen && File.ReadAllText(file) == "Keep every unsaved byte", "Recovery exit deleted or changed unsaved files.");
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Recovery exit silently saved or modified the MP4.");
            Reject(() => { _ = session.IsProtected; }, "Recovery exit retained a live encryption session.");
            Check(WorkspaceFolder.FindRetainedFolders(workingRoot).Contains(folder.WorkPath), "The released recovery folder was not discoverable.");
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action operation, string message)
    {
        try { operation(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException) { return; }
        throw new Exception(message);
    }
}
