using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FrameVault;

internal static class NativeWindowAppearance
{
    private static readonly SetWindowCompositionAttribute? SetCompositionAttribute = ResolveCompositionAttribute();

    internal static void Attach(Window window)
    {
        window.SourceInitialized += Apply;
        window.ContentRendered += Apply;
        window.Activated += Apply;
    }

    private static void Apply(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        if (window.Background is SolidColorBrush brush && HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = Color.FromArgb(255, brush.Color.R, brush.Color.G, brush.Color.B);

        // Windhawk's Translucent Windows can add acrylic before WPF initializes the HWND.
        // Reset only this window's blur and accent policy; desktop settings stay untouched.
        try
        {
            var blur = new BlurBehind { Flags = 1 }; // DWM_BB_ENABLE with Enable = false.
            DwmEnableBlurBehindWindow(handle, ref blur);
            if (SetCompositionAttribute is null) return;

            var policy = new AccentPolicy(); // ACCENT_DISABLED.
            var policySize = Marshal.SizeOf<AccentPolicy>();
            var policyPointer = Marshal.AllocHGlobal(policySize);
            try
            {
                Marshal.StructureToPtr(policy, policyPointer, false);
                var data = new CompositionAttributeData { Attribute = 19, Data = policyPointer, Size = (nuint)policySize };
                SetCompositionAttribute(handle, ref data);
            }
            finally { Marshal.FreeHGlobal(policyPointer); }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static SetWindowCompositionAttribute? ResolveCompositionAttribute()
    {
        try
        {
            var module = GetModuleHandleW("user32.dll");
            var address = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "SetWindowCompositionAttribute");
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<SetWindowCompositionAttribute>(address);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public uint Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Enable;
        public IntPtr Region;
        [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State, Flags, Color, Animation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData { public int Attribute; public IntPtr Data; public nuint Size; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetWindowCompositionAttribute(IntPtr window, ref CompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr window, ref BlurBehind blur);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);
}
