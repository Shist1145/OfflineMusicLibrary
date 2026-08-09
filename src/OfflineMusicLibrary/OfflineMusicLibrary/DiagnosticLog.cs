using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OfflineMusicLibrary;

public static class DiagnosticLog
{
	private const long MaximumLogBytes = 2097152L;
	private const int MaximumEntryCharacters = 64 * 1024;

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
		string safeCategory = Truncate(category ?? "", 96);
		string safeMessage = Truncate(message ?? "", 16 * 1024);
		string detail = exception == null
			? ""
			: $" | {exception.GetType().Name}: {Truncate(exception.Message ?? "", 8 * 1024)}" +
				$"{Environment.NewLine}{Truncate(exception.StackTrace ?? "", 32 * 1024)}";
		string entry = $"{DateTimeOffset.Now:O} [{safeCategory}] {safeMessage}{detail}{Environment.NewLine}";
		Entries.Writer.TryWrite(Truncate(entry, MaximumEntryCharacters));
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

	private static string Truncate(string value, int maximumCharacters)
	{
		return value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "…";
	}
}
