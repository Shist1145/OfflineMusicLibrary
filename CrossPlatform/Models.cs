using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace OfflineMusicLibrary;

public sealed class TrackModel : INotifyPropertyChanged
{
    private bool _isFavorite;
    private Bitmap? _cover;

    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "未知艺术家";
    public string AlbumArtist { get; set; } = "";
    public string Album { get; set; } = "未知专辑";
    public string Circle { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Format { get; set; } = "";
    public uint TrackNumber { get; set; }
    public long DurationMs { get; set; }
    public string ArtworkPath { get; set; } = "";
    public string? CloudId { get; set; }
    public List<string> CloudIds { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public int PlayCount { get; set; }
    public DateTime? LastPlayedAt { get; set; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
                return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteGlyph));
        }
    }

    [JsonIgnore]
    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
                return;
            _cover = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public bool IsEncryptedNcm => string.Equals(Path.GetExtension(FilePath), ".ncm", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasCloudIds => !string.IsNullOrWhiteSpace(CloudId) || CloudIds is { Count: > 0 };

    [JsonIgnore]
    public string FavoriteGlyph => IsFavorite ? "♥" : "♡";

    [JsonIgnore]
    public string DurationText => TimeSpan.FromMilliseconds(DurationMs)
        .ToString(DurationMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");

    [JsonIgnore]
    public string TrackText => TrackNumber == 0 ? "" : TrackNumber.ToString("00");

    [JsonIgnore]
    public string CategoryText => string.Join(" / ", Categories);

    [JsonIgnore]
    public string CircleText => string.IsNullOrWhiteSpace(Circle) ? "未识别" : Circle;

    [JsonIgnore]
    public string SearchText =>
        $"{Title} {Artist} {AlbumArtist} {Album} {Circle} {Genre} {CategoryText} {Path.GetFileNameWithoutExtension(FilePath)}"
            .ToLowerInvariant();

    [JsonIgnore]
    public string AlbumKey
    {
        get
        {
            var folder = Path.GetDirectoryName(FilePath) ?? "";
            var identityArtist = string.IsNullOrWhiteSpace(AlbumArtist) || IsGenericArtist(AlbumArtist)
                ? folder
                : AlbumArtist;
            return $"{Album.Trim()}\u001f{identityArtist.Trim()}".ToLowerInvariant();
        }
    }

    public IEnumerable<string> GetCloudIds()
    {
        if (!string.IsNullOrWhiteSpace(CloudId))
            yield return CloudId;
        foreach (var id in CloudIds ?? [])
            if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, CloudId, StringComparison.OrdinalIgnoreCase))
                yield return id;
    }

    public bool HasCloudId(string id) => GetCloudIds().Contains(id, StringComparer.OrdinalIgnoreCase);

    public void RememberCloudId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || HasCloudId(id))
            return;
        if (string.IsNullOrWhiteSpace(CloudId))
            CloudId = id;
        else
            (CloudIds ??= []).Add(id);
    }

    private static bool IsGenericArtist(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "" or "va" or "variousartists" or "unknownartist" or "未知艺术家";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PlaylistModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新歌单";
    public string Description { get; set; } = "";
    public string CoverPath { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> TrackIds { get; set; } = [];
    public string Source { get; set; } = "local";
    public string? CloudPlaylistId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public override string ToString() => $"{Name}  ·  {TrackIds.Count} 首";
}

public sealed class AppState
{
    public List<string> LibraryFolders { get; set; } = [];
    public List<TrackModel> Tracks { get; set; } = [];
    public List<PlaylistModel> Playlists { get; set; } = [];
    public int Volume { get; set; } = 76;
}

public sealed record ScanProgress(int Scanned, int Total, string CurrentFile, int MetadataFallbacks);

public sealed record AlbumCard(
    string Key,
    string Title,
    string Artist,
    string Circles,
    int TrackCount,
    TrackModel Representative)
{
    public string CountText => $"{TrackCount:N0} 首";
}

public sealed record CircleCard(string Name, int TrackCount)
{
    public string CountText => $"{TrackCount:N0} 首";
}

public sealed record NetEaseTrack(string Id, string Title, string Artist, string Album);

public sealed record NetEaseImportResult(
    string PlaylistName,
    string PlaylistId,
    int DeclaredTrackCount,
    IReadOnlyList<NetEaseTrack> Tracks,
    IReadOnlyList<TrackModel> Matched,
    IReadOnlyList<NetEaseTrack> Missing)
{
    public int TrackIdCount { get; init; }
    public int ResolvedTrackCount { get; init; }
    public int ExactMatchCount { get; init; }
    public int FuzzyMatchCount { get; init; }
    public IReadOnlyList<string> UnresolvedTrackIds { get; init; } = [];
    public bool HasCompleteTrackIds => TrackIdCount >= DeclaredTrackCount;
    public bool HasCompleteRemoteDetails => UnresolvedTrackIds.Count == 0 && HasCompleteTrackIds;
}
