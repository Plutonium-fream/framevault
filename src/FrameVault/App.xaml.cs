using System.Windows;

namespace FrameVault;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length == 1 && System.IO.File.Exists(e.Args[0]))
            window.OpenPath(e.Args[0]);
    }
}
