using System.Text.Json;
using System.IO;

namespace OfflineMusicLibrary;

public sealed class AppStore
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OfflineMusicLibrary");
    }

    public string DataDirectory { get; }

    public string StatePath => Path.Combine(DataDirectory, "library.json");

    public string PlaylistArtworkDirectory => Path.Combine(DataDirectory, "playlist-artwork");

    public async Task<AppState> LoadAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        if (!File.Exists(StatePath))
            return CreateDefaultState();

        try
        {
            await using var stream = File.OpenRead(StatePath);
            var state = await JsonSerializer.DeserializeAsync<AppState>(stream, _options) ?? CreateDefaultState();
            Normalize(state);
            return state;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            var backup = Path.Combine(DataDirectory, $"library.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(StatePath, backup, true);
            return CreateDefaultState();
        }
    }

    public async Task SaveAsync(AppState state)
    {
        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temporary = StatePath + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, state, _options);
            File.Move(temporary, StatePath, true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static AppState CreateDefaultState()
    {
        var preferred = @"G:\音乐\CloudMusic\_按专辑分类";
        var fallback = @"E:\CloudMusic";
        var state = new AppState();
        if (Directory.Exists(preferred))
            state.LibraryFolders.Add(preferred);
        else if (Directory.Exists(fallback))
            state.LibraryFolders.Add(fallback);
        return state;
    }

    private static void Normalize(AppState state)
    {
        state.LibraryFolders ??= [];
        state.Tracks ??= [];
        state.Playlists ??= [];
        state.FavoriteAlbumKeys ??= [];

        foreach (var track in state.Tracks)
        {
            track.Categories ??= [];
            track.CloudIds ??= [];
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
    }
}
