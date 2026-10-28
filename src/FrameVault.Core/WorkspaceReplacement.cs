namespace FrameVault.Core;

internal static class WorkspaceReplacement
{
    private static readonly int[] RetryDelays = [25, 75, 150];

    internal static string? Commit(string replacement, string destination, string backup,
        Action<string> verifyOriginal, Action<string> verifyReplacement,
        Action<string, string, string>? replace = null)
    {
        replace ??= static (source, target, previous) => File.Replace(source, target, previous, true);
        var replacementInstalled = false;
        try
        {
            if (File.Exists(backup) || Directory.Exists(backup)) throw new IOException("The recovery filename already exists.");
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var originalGuard = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    verifyOriginal(destination);
                    verifyReplacement(replacement);
                    replace(replacement, destination, backup);
                    replacementInstalled = true;
                    break;
                }
                catch (IOException exception) when (Transient(exception) && attempt < RetryDelays.Length)
                {
                    // Retry only while the entire old file is still verified at its original name.
                    verifyOriginal(destination);
                    if (File.Exists(backup) || !File.Exists(replacement)) throw;
                    Thread.Sleep(RetryDelays[attempt]);
                }
            }
            verifyReplacement(destination);
            if (!TryDelete(backup))
                return "Saved successfully, but the previous MP4 copy could not be removed. It may use the old password or be unencrypted. Review and remove it when safe: " + backup;
            return null;
        }
        catch (Exception exception)
        {
            // ReplaceFile errors 1176/1177 can rename the original before reporting failure.
            Exception? restoreError = null;
            if (!File.Exists(destination) && File.Exists(backup))
            {
                try
                {
                    verifyOriginal(backup);
                    File.Move(backup, destination, false);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or InvalidDataException)
                { restoreError = restore; }
            }
            var ambiguous = replacementInstalled || (exception.HResult & 0xffff) is 1176 or 1177;
            var originalIntact = false;
            try { verifyOriginal(destination); originalIntact = true; }
            catch (Exception verify) when (verify is IOException or UnauthorizedAccessException or InvalidDataException) { }
            if (originalIntact && !ambiguous) TryDelete(replacement);
            var retained = new[] { destination, backup, replacement }.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var message = "The MP4 save could not finish. The working folder was kept.";
            if (retained.Length > 0) message += " Complete or recoverable MP4 copies remain at:\n" + string.Join("\n", retained);
            throw new WorkspaceReplacementException(message, originalIntact,
                restoreError is null ? exception : new AggregateException(exception, restoreError));
        }
    }

    private static bool Transient(IOException exception) => (exception.HResult & 0xffff) is 32 or 33 or 1175;

    private static bool TryDelete(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Delete(path); return true; }
            catch (IOException exception) when (Transient(exception) && attempt < RetryDelays.Length) { Thread.Sleep(RetryDelays[attempt]); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}

internal sealed class WorkspaceReplacementException : IOException
{
    internal bool OriginalVerified { get; }
    internal WorkspaceReplacementException(string message, bool originalVerified, Exception inner)
        : base(message, inner) => OriginalVerified = originalVerified;
}
