using System.Configuration;
using System.Data;
using System.Windows;

namespace OfflineMusicLibrary;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
            DiagnosticLog.Write("UI", "Unhandled dispatcher exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticLog.Write("PROCESS", "Unhandled process exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticLog.Write("TASK", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        DiagnosticLog.Write("APP", $"Starting version {GetType().Assembly.GetName().Version}");
        base.OnStartup(e);
    }
}

