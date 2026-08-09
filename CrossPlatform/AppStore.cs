using System.Text.Json;

namespace OfflineMusicLibrary;

public sealed class AppStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OfflineMusicLibrary");

    public static string ArtworkCacheDirectory => Path.Combine(DataDirectory, "artwork");
    public string StatePath => Path.Combine(DataDirectory, "library-cross-platform.json");

    public AppStore()
    {
        _options.Converters.Add(new BoundedStringJsonConverter(ContentReadLimits.StateStringUtf8Bytes));
    }

    public async Task<AppState> LoadAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(StatePath))
            return new AppState();

        try
        {
            await using var stream = File.OpenRead(StatePath);
            if (stream.Length > ContentReadLimits.StateFileBytes)
                throw new InvalidDataException(
                    $"State file exceeds the {ContentReadLimits.StateFileBytes / 1024 / 1024} MiB safety limit.");
            var state = await JsonSerializer.DeserializeAsync<AppState>(stream, _options) ?? new AppState();
            state.LibraryFolders ??= [];
            state.Tracks ??= [];
            state.Playlists ??= [];
            foreach (var track in state.Tracks)
            {
                track.CloudIds ??= [];
                track.Categories ??= [];
            }
            foreach (var playlist in state.Playlists)
            {
                playlist.Name ??= "新歌单";
                playlist.Description ??= "";
                playlist.CoverPath ??= "";
                playlist.Source ??= "local";
                playlist.TrackIds ??= [];
                playlist.Tags ??= [];
                if (playlist.CreatedAt == default)
                    playlist.CreatedAt = playlist.UpdatedAt == default ? DateTime.Now : playlist.UpdatedAt;
                if (playlist.UpdatedAt == default)
                    playlist.UpdatedAt = playlist.CreatedAt;
            }
            return state;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("Store", "曲库状态读取失败，已使用空状态。", exception);
            return new AppState();
        }
    }

    public async Task SaveAsync(AppState state)
    {
        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temporary = StatePath + ".tmp";
            try
            {
                await using var stream = File.Create(temporary);
                await JsonSerializer.SerializeAsync(stream, state, _options);
                if (stream.Length > ContentReadLimits.StateFileBytes)
                    throw new InvalidDataException(
                        $"State file exceeds the {ContentReadLimits.StateFileBytes / 1024 / 1024} MiB safety limit.");
                await stream.FlushAsync();
                stream.Close();
                File.Move(temporary, StatePath, true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
