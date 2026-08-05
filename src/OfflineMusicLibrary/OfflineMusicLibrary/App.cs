using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace OfflineMusicLibrary;

public partial class App : Application
{
	private int _recoverableErrorNoticePending;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs args)
		{
			DiagnosticLog.Write("PROCESS", "Unhandled process exception", args.ExceptionObject as Exception);
		};
		TaskScheduler.UnobservedTaskException += delegate(object? _, UnobservedTaskExceptionEventArgs args)
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
		{
			return;
		}
		args.Handled = true;
		if (Interlocked.Exchange(ref _recoverableErrorNoticePending, 1) != 0)
		{
			return;
		}
		try
		{
			base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
			{
				try
				{
					if (base.MainWindow is MainWindow mainWindow)
					{
						mainWindow.ReportRecoverableUiException();
					}
					else
					{
						MessageBox.Show("刚才的界面操作发生异常，程序已保持运行。详细信息已写入日志。", "操作未完成", MessageBoxButton.OK, MessageBoxImage.Exclamation);
					}
				}
				catch (Exception exception2)
				{
					DiagnosticLog.Write("UI", "Could not display recoverable exception notice", exception2);
				}
				finally
				{
					Interlocked.Exchange(ref _recoverableErrorNoticePending, 0);
				}
			});
		}
		catch (Exception exception)
		{
			Interlocked.Exchange(ref _recoverableErrorNoticePending, 0);
			DiagnosticLog.Write("UI", "Could not queue recoverable exception notice", exception);
		}
	}

	internal static bool IsFatalException(Exception exception)
	{
		if (exception is AggregateException aggregate)
		{
			return aggregate.Flatten().InnerExceptions.Any(IsFatalException);
		}
		for (Exception current = exception; current != null; current = current.InnerException)
		{
			if ((current is OutOfMemoryException || current is StackOverflowException || current is AccessViolationException || current is AppDomainUnloadedException || current is BadImageFormatException || current is CannotUnloadAppDomainException) ? true : false)
			{
				return true;
			}
		}
		return false;
	}
}
