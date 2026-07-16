using System.Threading.Channels;
using System.IO;

namespace OfflineMusicLibrary;

public static class DiagnosticLog
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private static readonly Channel<string> Entries = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2048)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

    static DiagnosticLog()
    {
        _ = Task.Run(WriteLoopAsync);
    }

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OfflineMusicLibrary",
        "logs");

    public static string LogPath => Path.Combine(LogDirectory, "player.log");

    public static void Write(string category, string message, Exception? exception = null)
    {
        var detail = exception is null
            ? ""
            : $" | {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
        Entries.Writer.TryWrite($"{DateTimeOffset.Now:O} [{category}] {message}{detail}{Environment.NewLine}");
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (var entry in Entries.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumLogBytes)
                    File.Move(LogPath, Path.Combine(LogDirectory, "player.previous.log"), true);
                await File.AppendAllTextAsync(LogPath, entry);
            }
            catch
            {
                // Diagnostics must never become another source of playback failures.
            }
        }
    }
}
