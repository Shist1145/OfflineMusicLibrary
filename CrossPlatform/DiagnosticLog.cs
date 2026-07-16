namespace OfflineMusicLibrary;

public static class DiagnosticLog
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    public static string LogPath => Path.Combine(AppStore.DataDirectory, "cross-platform.log");

    public static void Write(string category, string message, Exception? exception = null)
    {
        _ = WriteAsync(category, message, exception);
    }

    private static async Task WriteAsync(string category, string message, Exception? exception)
    {
        var acquired = false;
        try
        {
            await WriteLock.WaitAsync();
            acquired = true;
            Directory.CreateDirectory(AppStore.DataDirectory);
            var detail = exception is null ? "" : $" | {exception.GetType().Name}: {exception.Message}";
            await File.AppendAllTextAsync(LogPath,
                $"{DateTimeOffset.Now:O} [{category}] {message}{detail}{Environment.NewLine}");
        }
        catch
        {
        }
        finally
        {
            if (acquired)
                WriteLock.Release();
        }
    }
}
