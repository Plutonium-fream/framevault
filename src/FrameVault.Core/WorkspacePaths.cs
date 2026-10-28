namespace FrameVault.Core;

internal static class WorkspacePaths
{
    public static void Validate(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/'))
            throw new InvalidDataException("Use a relative file path with forward slashes.");
        var parts = path.Split('/');
        if (parts.Length > 32) throw new InvalidDataException("Workspace folder nesting may not exceed 32 levels.");
        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException("A file or folder name contains characters Windows cannot safely use.");
            var device = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                ((device.StartsWith("COM") || device.StartsWith("LPT")) && device.Length == 4 && "123456789¹²³".Contains(device[3])))
                throw new InvalidDataException("Reserved Windows device names cannot be used in a workspace.");
        }
    }

    public static IEnumerable<string> Parents(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
            if (slash == 0) break;
        }
    }

    public static string Under(string root, string relative)
    {
        Validate(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A workspace path escapes its folder.");
        return full;
    }
}
