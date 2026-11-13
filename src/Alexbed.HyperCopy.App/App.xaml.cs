using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Alexbed.HyperCopy.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string log = Path.Combine(Path.GetTempPath(), "AlexbedHyperCopy-crash.log");
        try { File.AppendAllText(log, $"[{DateTime.Now:u}] {e.Exception}\n\n"); } catch { }
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to:\n{log}",
            "Alexbed HyperCopy", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
