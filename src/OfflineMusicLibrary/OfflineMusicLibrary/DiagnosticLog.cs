using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OfflineMusicLibrary;

public static class DiagnosticLog
{
	private const long MaximumLogBytes = 2097152L;

	private static readonly Channel<string> Entries;

	public static string LogDirectory { get; }

	public static string LogPath => Path.Combine(LogDirectory, "player.log");

	static DiagnosticLog()
	{
		Entries = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
		{
			SingleReader = true,
			SingleWriter = false,
			FullMode = BoundedChannelFullMode.DropOldest,
			AllowSynchronousContinuations = false
		});
		LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfflineMusicLibrary", "logs");
		Task.Run((Func<Task?>)WriteLoopAsync);
	}

	public static void Write(string category, string message, Exception? exception = null)
	{
		string detail = ((exception == null) ? "" : $" | {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
		Entries.Writer.TryWrite($"{DateTimeOffset.Now:O} [{category}] {message}{detail}{Environment.NewLine}");
	}

	public static void Observe(Task task, string category, string message)
	{
		ArgumentNullException.ThrowIfNull(task);
		_ = task.ContinueWith(completed =>
		{
			Exception exception = completed.Exception?.GetBaseException() ?? new InvalidOperationException(message);
			Write(category, message, exception);
		}, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	private static async Task WriteLoopAsync()
	{
		await foreach (string entry in Entries.Reader.ReadAllAsync())
		{
			try
			{
				Directory.CreateDirectory(LogDirectory);
				if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2097152)
				{
					File.Move(LogPath, Path.Combine(LogDirectory, "player.previous.log"), overwrite: true);
				}
				await File.AppendAllTextAsync(LogPath, entry);
			}
			catch
			{
			}
		}
	}
}
