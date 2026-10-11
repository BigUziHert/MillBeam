using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace GenmitsuControl.Desktop;

public partial class App : Application
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Give the renamed application its own stable taskbar identity.
        SetCurrentProcessExplicitAppUserModelID("MillBeam.Control");
        base.OnStartup(e);
        var shutdownTestIndex = Array.IndexOf(e.Args, "--shutdown-test");
        var updateUiTestIndex = Array.IndexOf(e.Args, "--update-ui-test");
        if (shutdownTestIndex >= 0 || updateUiTestIndex >= 0)
        {
            int testIndex = shutdownTestIndex >= 0 ? shutdownTestIndex : updateUiTestIndex;
            string folder = testIndex + 1 < e.Args.Length ? e.Args[testIndex + 1] : Path.GetTempPath();
            string errorName = shutdownTestIndex >= 0 ? "shutdown-smoke-error.txt" : "update-ui-smoke-error.txt";
            Directory.CreateDirectory(folder);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (_, args) =>
            {
                File.AppendAllText(Path.Combine(folder, errorName), args.Exception + Environment.NewLine);
                args.Handled = true;
                Shutdown(1);
            };
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (shutdownTestIndex >= 0) await GenmitsuControl.Desktop.MainWindow.RunShutdownTestsAsync(folder);
                    else await UpdateWindow.RunUpdateUiTestsAsync(folder);
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    File.AppendAllText(Path.Combine(folder, errorName), ex + Environment.NewLine);
                    Shutdown(1);
                }
            }));
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "MillBeam", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Contains("--smoke-test"))
        {
            var index = Array.IndexOf(e.Args, "--smoke-test");
            string folder = index + 1 < e.Args.Length ? e.Args[index + 1] : Path.GetTempPath();
            window.Loaded += async (_, _) =>
            {
                try { await window.RunSmokeTestAsync(folder); Shutdown(0); }
                catch (Exception ex) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "ui-smoke-error.txt"), ex.ToString()); Shutdown(1); }
            };
        }
    }
}
