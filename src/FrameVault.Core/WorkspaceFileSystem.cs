using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FrameVault.Core;

internal static class WorkspaceFileSystem
{
    public static void CheckNoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links, junctions, and other reparse points are not supported in working folders.");
    }

    public static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) CreateWindowsDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsDirectory(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("The current Windows user could not be identified.");
        var access = new DirectorySecurity();
        access.SetAccessRuleProtection(true, false);
        access.SetOwner(user);
        access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(access);
    }

    public static void CheckStreams(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = FindFirstStreamW(LongPath(path), 0, out var stream, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 38 or 50 or 1) return;
            throw new IOException("Cannot check file streams: " + path, new Win32Exception(error));
        }
        try
        {
            do
            {
                if (!string.Equals(stream.Name, "::$DATA", StringComparison.Ordinal))
                    throw new IOException("Alternate data streams are not supported. Remove the extra stream before saving: " + path);
            } while (FindNextStreamW(handle, out stream));
            var error = Marshal.GetLastWin32Error();
            if (error != 38) throw new IOException("Cannot finish checking file streams: " + path, new Win32Exception(error));
        }
        finally { FindClose(handle); }
    }

    public static FileStream OpenSnapshotFile(string path, bool deleting)
    {
        if (!OperatingSystem.IsWindows()) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var access = 0x80000000u | (deleting ? 0x10000u : 0u);
        var handle = CreateFileW(LongPath(path), access, deleting ? 4u : 0u, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("Close this file in other applications before saving or locking: " + path, new Win32Exception(error));
        }
        return new FileStream(handle, FileAccess.Read, 65536, false);
    }

    public static SafeFileHandle? CheckDeleteDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateFileW(LongPath(path), 0x10080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new IOException("This folder cannot be safely removed. Close applications using it and check its permissions: " + path, new Win32Exception(error));
        }
        return handle;
    }

    private static string LongPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\?\\", StringComparison.Ordinal)) return full;
        return full.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + full[2..] : "\\\\?\\" + full;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
