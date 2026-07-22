using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace OfflineMusicLibrary;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private int _recoverableErrorNoticePending;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        DiagnosticLog.Write("UI", "Unhandled dispatcher exception", args.Exception);
        if (IsFatalException(args.Exception))
            return;

        // A failed menu, dialog, cover, or lyric update must not terminate playback.
        args.Handled = true;
        if (Interlocked.Exchange(ref _recoverableErrorNoticePending, 1) != 0)
            return;

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    if (MainWindow is MainWindow mainWindow)
                        mainWindow.ReportRecoverableUiException();
                    else
                        MessageBox.Show("刚才的界面操作发生异常，程序已保持运行。详细信息已写入日志。",
                            "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (Exception notificationException)
                {
                    DiagnosticLog.Write("UI", "Could not display recoverable exception notice", notificationException);
                }
                finally
                {
                    Interlocked.Exchange(ref _recoverableErrorNoticePending, 0);
                }
            }));
        }
        catch (Exception notificationException)
        {
            Interlocked.Exchange(ref _recoverableErrorNoticePending, 0);
            DiagnosticLog.Write("UI", "Could not queue recoverable exception notice", notificationException);
        }
    }

    internal static bool IsFatalException(Exception exception)
    {
        if (exception is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.Any(IsFatalException);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OutOfMemoryException or StackOverflowException or AccessViolationException
                or AppDomainUnloadedException or BadImageFormatException or CannotUnloadAppDomainException)
                return true;
        }

        return false;
    }
}

