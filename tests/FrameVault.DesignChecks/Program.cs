using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using FrameVault;
using FrameVault.Core;
using FrameVault.Views;

internal static class Program
{
    private static readonly List<string> Failures = [];
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var destination = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("artifacts", "previews"));
        Directory.CreateDirectory(destination);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources = new ResourceDictionary { Source = new Uri("/FrameVault;component/Theme.xaml", UriKind.Relative) };
        var scratch = Path.Combine(AppContext.BaseDirectory, "design-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        MainWindow? window = null;
        WorkspaceFolder? folder = null;
        try
        {
            Check(Contrast(ResourceColor("Accent"), "#FFFFFF") >= 4.5, "Primary action contrast meets 4.5:1");
            Check(Contrast(ResourceColor("Muted"), ResourceColor("Background")) >= 4.5, "Muted copy contrast meets 4.5:1");
            Check(Contrast(ResourceColor("Text"), ResourceColor("Surface")) >= 7, "Main text contrast meets 7:1");

            window = new MainWindow();
            Check(window.WindowStyle == WindowStyle.SingleBorderWindow && WindowChrome.GetWindowChrome(window) is null, "Windows manages maximize, snapping, and taskbar bounds with a native frame");
            Check(!window.AllowsTransparency && window.Opacity == 1 && window.Background is SolidColorBrush { Color.A: 255 }, "Main window has an opaque background and full opacity");
            Check(window.MinHeight <= SystemParameters.WorkArea.Height && window.MinWidth <= SystemParameters.WorkArea.Width, "Minimum window size fits the DPI-scaled working area");
            var closing = new CancelEventArgs();
            Invoke(window, "OnClosing", window, closing);
            Check(!closing.Cancel, "Closing with no working folder completes synchronously without recursive Close");
            var root = (FrameworkElement)window.Content;
            Check(root is Panel { Background: SolidColorBrush { Color.A: 255 } }, "Client content paints its own opaque surface behind native transparency effects");
            window.Content = null;
            var host = Host(root);
            foreach (var size in new[] { new Size(1000, 690), new Size(1280, 820) })
            {
                Layout(host, size);
                Save(host, destination, $"start-{size.Width:0}", size);
                CheckAction(host, "Create Workspace", size);
                CheckAction(host, "Open Workspace", size);
            }

            var mp4 = Path.Combine(scratch, "sample.mp4");
            using var session = WorkspaceStore.Create(Path.Combine(AppContext.BaseDirectory, "video-fixture.mp4"), mp4, "Weekend collection", "design-check-password");
            Populate(session);
            session.Save();
            folder = WorkspaceFolder.Open(session, Path.Combine(scratch, "unlocked"));
            Invoke(window, "AttachSession", session, folder);
            foreach (var size in new[] { new Size(1000, 690), new Size(1280, 820) })
            {
                Layout(host, size);
                Save(host, destination, $"folder-{size.Width:0}", size);
                CheckAction(host, "Open folder", size); CheckAction(host, "Save changes", size); CheckAction(host, "Save & Lock", size);
                Check(Elements<TextBox>(host).Any(t => t.IsReadOnly && t.Text == folder.WorkPath && AutomationProperties.GetName(t).Contains("Working folder", StringComparison.OrdinalIgnoreCase)), $"Working folder path is accessible and copyable at {size.Width:0}px");
                Check(AllText(host).Contains("readable files while open", StringComparison.OrdinalIgnoreCase), $"Plaintext working-folder notice appears at {size.Width:0}px");
                Check(!AllText(host).Contains("Private document.txt", StringComparison.Ordinal), "Manager does not display workspace file contents or filenames");
                CheckAction(host, "Password settings", size); CheckAction(host, "Open MP4", size);
            }
            session.Data.Name = "A long workspace title for a portable collection of documents, images, and other everyday files";
            Invoke(window, "ShowFolder");
            var small = new Size(1000, 690);
            Layout(host, small); Save(host, destination, "protected-long-title-1000", small);
            CheckAction(host, "Password settings", small); CheckAction(host, "Open MP4", small); CheckAction(host, "Open folder", small); CheckAction(host, "Save & Lock", small);
            folder.SaveAndLock(); Invoke(window, "ClearReferences"); Invoke(window, "ShowLocked", mp4);
            foreach (var size in new[] { small, new Size(1280, 820) })
            {
                Layout(host, size); Save(host, destination, $"locked-{size.Width:0}", size);
                CheckAction(host, "Unlock workspace", size);
                Check(!AllText(host).Contains("Private document.txt", StringComparison.Ordinal), "Locked view exposes no file names");
            }
            using var plain = WorkspaceStore.Create(Path.Combine(AppContext.BaseDirectory, "video-fixture.mp4"), Path.Combine(scratch, "plain.mp4"), "Unprotected example");
            folder = WorkspaceFolder.Open(plain, Path.Combine(scratch, "unlocked"));
            Invoke(window, "AttachSession", plain, folder);
            Layout(host, small); Save(host, destination, "folder-unprotected-1000", small);
            Check(AllText(host).Contains("no password", StringComparison.OrdinalIgnoreCase), "Unprotected workspace shows its protection status honestly");
            CheckAction(host, "Add password", small);
            folder.SaveAndLock(); Invoke(window, "ClearReferences");
            using var legacy = WorkspaceStore.Create(Path.Combine(AppContext.BaseDirectory, "video-fixture.mp4"), Path.Combine(scratch, "legacy.mp4"), "Legacy workspace");
            legacy.Data.Notes.Add(new NoteItem { Title = "Legacy note", Body = "A note from the former built-in app." }); legacy.Save();
            folder = WorkspaceFolder.Open(legacy, Path.Combine(scratch, "unlocked"));
            Invoke(window, "AttachSession", legacy, folder);
            Layout(host, small); Save(host, destination, "folder-legacy-1000", small);
            Check(AllText(host).Contains("Earlier versions cannot open", StringComparison.OrdinalIgnoreCase), "Legacy conversion warning explains the compatibility change before saving");
            folder.Save();
            ((FolderView)window.GetType().GetField("_folderView", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Refresh();
            Layout(host, small);
            Check(!AllText(host).Contains("Earlier versions cannot open", StringComparison.OrdinalIgnoreCase), "Legacy conversion notice disappears after a successful migration save");
            folder.SaveAndLock(); Invoke(window, "ClearReferences");
            using var uncertain = WorkspaceStore.Create(Path.Combine(AppContext.BaseDirectory, "video-fixture.mp4"), Path.Combine(scratch, "uncertain.mp4"), "Recovery required", "design-check-password");
            folder = WorkspaceFolder.Open(uncertain, Path.Combine(scratch, "unlocked"));
            // Core fault tests create this state through real replacement failures; here the UI is tested in isolation.
            typeof(WorkspaceSession).GetProperty(nameof(WorkspaceSession.RequiresReopen))!.SetValue(uncertain, true);
            Invoke(window, "AttachSession", uncertain, folder);
            foreach (var size in new[] { small, new Size(1280, 820) })
            {
                Layout(host, size); Save(host, destination, $"folder-protection-unknown-{size.Width:0}", size);
                var text = AllText(host);
                Check(text.Contains("MP4 PROTECTION NOT VERIFIED", StringComparison.Ordinal), $"Uncertain disk protection is explicitly shown at {size.Width:0}px");
                Check(!text.Contains("MP4 ENCRYPTED", StringComparison.Ordinal) && !text.Contains("PASSWORD PROTECTED", StringComparison.Ordinal) && !text.Contains("Your MP4 is encrypted", StringComparison.Ordinal), "Unknown protection never displays a stale encrypted claim");
                Check(Elements<Button>(host).Any(button => AutomationProperties.GetName(button) == "Save changes" && !button.IsEnabled) && Elements<Button>(host).Any(button => AutomationProperties.GetName(button) == "Save & Lock" && !button.IsEnabled), "Both save actions are disabled while disk protection is unknown");
                Check(Elements<Button>(host).Any(button => AutomationProperties.GetName(button) == "Protection not verified" && !button.IsEnabled), "Password changes are disabled until a verified MP4 is reopened");
                Check(Elements<TextBox>(host).Any(input => input.IsReadOnly && input.Text == folder.WorkPath), "The plaintext recovery path remains available");
                CheckAction(host, "Open folder", size);
            }
            folder.LeaveForRecovery(); Invoke(window, "ClearReferences");
            RenderDialog("CreateDialog", [null], "create", new Size(590, 690), "Create workspace");
            RenderDialog("PasswordDialog", [null, false, true, "Weekend collection.mp4"], "unlock", new Size(480, 390), "Unlock workspace");
            RenderDialog("PasswordDialog", [null, true, true, "Weekend collection.mp4"], "password", new Size(480, 550), "Save password");
            Console.WriteLine($"{_checks - Failures.Count}/{_checks} design checks passed. Previews: {destination}");
            foreach (var failure in Failures) Console.Error.WriteLine("FAIL " + failure);
            return Failures.Count == 0 ? 0 : 1;

            void RenderDialog(string typeName, object?[] arguments, string name, Size size, string action)
            {
                var type = typeof(MainWindow).Assembly.GetType("FrameVault." + typeName)!;
                var dialog = (Window)Activator.CreateInstance(type, arguments)!;
                Check(dialog.WindowStyle == WindowStyle.SingleBorderWindow && !dialog.AllowsTransparency && dialog.Opacity == 1 && dialog.Background is SolidColorBrush { Color.A: 255 }, $"{name} uses an opaque native-frame window");
                var content = (FrameworkElement)dialog.Content;
                dialog.Content = null;
                var dialogHost = Host(content);
                Layout(dialogHost, size);
                var dialogText = Elements<TextBlock>(dialogHost).Where(text => !string.IsNullOrWhiteSpace(text.Text)).ToList();
                Check(dialogText.All(text => text.Opacity == 1 && text.FontSize >= 11 && text.Foreground is SolidColorBrush { Color.A: 255 }), $"{name} labels have solid colors and readable text sizes");
                Save(dialogHost, destination, name, size);
                CheckAction(dialogHost, action, size, allowScrollable: true);
                Check(Elements<Control>(dialogHost).Where(control => control is TextBox or PasswordBox).All(control => control.IsTabStop), $"{name} inputs support keyboard tab navigation");
                if (typeName == "CreateDialog")
                {
                    var protect = Elements<CheckBox>(dialogHost).First();
                    Check(protect.IsChecked == true, "New workspace creation defaults to password protection");
                    protect.IsChecked = true;
                    Layout(dialogHost, size);
                    Elements<ScrollViewer>(dialogHost).OrderByDescending(view => view.ScrollableHeight).First().ScrollToBottom();
                    Layout(dialogHost, size);
                    Save(dialogHost, destination, "create-protected-bottom", size);
                    Check(Elements<PasswordBox>(dialogHost).Count() == 2, "Protected creation exposes password and confirmation inputs");
                    CheckAction(dialogHost, action, size);
                }
            }
        }
        finally
        {
            if (folder?.IsOpen == true) folder.DiscardAndLock();
            if (window is not null) Invoke(window, "ClearReferences");
            application.Shutdown();
            Directory.Delete(scratch, true);
        }
    }

    private static Border Host(FrameworkElement root)
    {
        var host = new Border { Background = Brush("Background"), Child = root, SnapsToDevicePixels = true, UseLayoutRounding = true };
        TextElement.SetFontFamily(host, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(host, 14);
        TextElement.SetForeground(host, Brush("Text"));
        return host;
    }
    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private static string ResourceColor(string key) => ((SolidColorBrush)Brush(key)).Color.ToString();
    private static void Layout(FrameworkElement host, Size size)
    {
        host.Width = size.Width; host.Height = size.Height;
        host.Measure(size); host.Arrange(new Rect(size)); host.UpdateLayout();
    }
    private static void Save(FrameworkElement element, string directory, string name, Size size)
    {
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var descendant in Elements<T>(VisualTreeHelper.GetChild(root, i))) yield return descendant;
    }
    private static void CheckAction(FrameworkElement host, string text, Size size, bool allowScrollable = false)
    {
        var buttons = Elements<Button>(host).Where(b => b.IsEnabled && b.Visibility == Visibility.Visible &&
            (AutomationProperties.GetName(b).Contains(text, StringComparison.OrdinalIgnoreCase) ||
             b.Content is string label && label.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        var visible = buttons.Any(button =>
        {
            var bounds = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize));
            return bounds.Width > 15 && bounds.Height >= 30 && bounds.Left >= 0 && bounds.Right <= size.Width + 1 &&
                   bounds.Top >= 0 && (bounds.Bottom <= size.Height + 1 || allowScrollable && HasScrollAncestor(button));
        });
        Check(visible, $"{text} action fits {size.Width:0}×{size.Height:0}");
    }
    private static bool HasScrollAncestor(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer { VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled }) return true;
        return false;
    }
    private static void Check(bool condition, string label) { _checks++; if (!condition) Failures.Add(label); else Console.WriteLine("PASS " + label); }
    private static string AllText(DependencyObject root) => string.Join("\n", Elements<TextBlock>(root).Where(IsDisplayed).Select(t => t.Text));
    private static bool IsDisplayed(DependencyObject child)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
    private static void Invoke(object instance, string method, params object?[] arguments) => instance.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single(m => m.Name == method && m.GetParameters().Length == arguments.Length).Invoke(instance, arguments);
    private static double Contrast(string first, string second)
    {
        static double Luminance(string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            static double Channel(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static void Populate(WorkspaceSession session)
    {
        session.Data.Files.Add(new WorkspaceFile { Path = "Documents/Private document.txt", Data = Encoding.UTF8.GetBytes("Synthetic document for offscreen layout checks.") });
        session.Data.Files.Add(new WorkspaceFile { Path = "Documents/Planning.txt", Data = Encoding.UTF8.GetBytes("A second example file.") });
        session.Data.Files.Add(new WorkspaceFile { Path = "binary.dat", Data = [0, 255, 1, 42] });
        session.Data.Directories.AddRange(["Documents", "Documents/Empty folder"]);
    }
}
