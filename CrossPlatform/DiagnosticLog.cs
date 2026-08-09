using System.Threading.Channels;

namespace OfflineMusicLibrary;

public static class DiagnosticLog
{
    private const long MaximumLogBytes = 2L * 1024L * 1024L;
    private const int MaximumEntryCharacters = 64 * 1024;
    private static readonly Channel<string> Entries = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false
    });

    public static string LogPath => Path.Combine(AppStore.DataDirectory, "cross-platform.log");

    static DiagnosticLog()
    {
        _ = Task.Run(WriteLoopAsync);
    }

    public static void Write(string category, string message, Exception? exception = null)
    {
        var safeCategory = Truncate(category ?? "", 96);
        var safeMessage = Truncate(message ?? "", 16 * 1024);
        var detail = exception is null
            ? ""
            : $" | {exception.GetType().Name}: {Truncate(exception.Message ?? "", 8 * 1024)}";
        var entry = $"{DateTimeOffset.Now:O} [{safeCategory}] {safeMessage}{detail}{Environment.NewLine}";
        Entries.Writer.TryWrite(Truncate(entry, MaximumEntryCharacters));
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (var entry in Entries.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(AppStore.DataDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumLogBytes)
                {
                    File.Move(
                        LogPath,
                        Path.Combine(AppStore.DataDirectory, "cross-platform.previous.log"),
                        overwrite: true);
                }
                await File.AppendAllTextAsync(LogPath, entry).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "…";
}
