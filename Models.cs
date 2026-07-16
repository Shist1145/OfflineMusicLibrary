using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace OfflineMusicLibrary;

public sealed class TrackModel : INotifyPropertyChanged
{
    private bool _isFavorite;

    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "未知艺术家";
    public string Album { get; set; } = "未知专辑";
    public string AlbumArtist { get; set; } = "";
    public string Circle { get; set; } = "";
    public bool CircleIsManual { get; set; }
    public string Genre { get; set; } = "";
    public string Format { get; set; } = "";
    public int Year { get; set; }
    public uint TrackNumber { get; set; }
    public long DurationMs { get; set; }
    public bool HasCover { get; set; }
    public bool HasLyrics { get; set; }
    public bool IsVideo { get; set; }
    public int PlayCount { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public List<string> Categories { get; set; } = [];
    public string? CloudId { get; set; }
    public List<string> CloudIds { get; set; } = [];

    [JsonIgnore]
    public bool HasCloudIds => !string.IsNullOrWhiteSpace(CloudId) || CloudIds is { Count: > 0 };

    public IEnumerable<string> GetCloudIds()
    {
        if (!string.IsNullOrWhiteSpace(CloudId))
            yield return CloudId;

        foreach (var id in CloudIds ?? [])
            if (!string.IsNullOrWhiteSpace(id) &&
                !string.Equals(id, CloudId, StringComparison.OrdinalIgnoreCase))
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
            OnPropertyChanged(nameof(FavoriteToolTip));
        }
    }

    [JsonIgnore]
    public BitmapSource? CoverThumbnail => CoverService.LoadThumbnail(this);

    [JsonIgnore]
    public string FavoriteGlyph => IsFavorite ? "♥" : "♡";

    [JsonIgnore]
    public string FavoriteToolTip => IsFavorite ? "取消收藏这首歌" : "收藏这首歌";

    [JsonIgnore]
    public string AlbumKey => AlbumIdentity.Create(this);

    [JsonIgnore]
    public bool IsEncryptedNcm => string.Equals(Path.GetExtension(FilePath), ".ncm", StringComparison.OrdinalIgnoreCase);

    public string DurationText => TimeSpan.FromMilliseconds(DurationMs).ToString(DurationMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
    public string TrackText => TrackNumber == 0 ? "" : TrackNumber.ToString("00");
    public string CategoryText => string.Join(" / ", Categories);
    public string LastPlayedText => LastPlayedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string PlayCountText => $"{PlayCount:N0} 次";
    public string MediaTypeText => IsEncryptedNcm ? "NCM 加密文件" : IsVideo ? "视频" : "音频";
    public string CircleText => string.IsNullOrWhiteSpace(Circle) ? "未识别" : Circle;
    public string SearchText => $"{Title} {Artist} {Album} {AlbumArtist} {Circle} {Genre} {string.Join(' ', Categories)} {Path.GetFileNameWithoutExtension(FilePath)}".ToLowerInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class PlaylistModel : INotifyPropertyChanged
{
    private BitmapSource? _coverThumbnail;

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

    [JsonIgnore]
    public BitmapSource? CoverThumbnail
    {
        get => _coverThumbnail;
        set
        {
            if (ReferenceEquals(_coverThumbnail, value))
                return;
            _coverThumbnail = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string CountText => $"{TrackIds.Count:N0} 首";

    [JsonIgnore]
    public string DescriptionPreview => string.IsNullOrWhiteSpace(Description) ? "暂无简介" : Description.Trim();

    [JsonIgnore]
    public string TagsText => Tags.Count == 0 ? "未设置标签" : string.Join(" · ", Tags);

    [JsonIgnore]
    public string SourceText => string.Equals(Source, "netease", StringComparison.OrdinalIgnoreCase)
        ? "网易云导入"
        : "本地歌单";

    [JsonIgnore]
    public string UpdatedText => $"{SourceText} · 创建于 {CreatedAt:yyyy-MM-dd} · 更新于 {UpdatedAt:yyyy-MM-dd HH:mm}";

    public void InvalidateCover() => CoverThumbnail = null;

    public void NotifyMetadataChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(DescriptionPreview));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(UpdatedText));
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AlbumViewModel : INotifyPropertyChanged
{
    private BitmapSource? _coverThumbnail;

    public string Key { get; init; } = "";
    public string Title { get; init; } = "未知专辑";
    public string Artist { get; init; } = "未知艺术家";
    public string CircleNames { get; init; } = "";
    public int TrackCount { get; init; }
    public bool IsFavorite { get; init; }
    public TrackModel? RepresentativeTrack { get; init; }

    [JsonIgnore]
    public BitmapSource? CoverThumbnail
    {
        get => _coverThumbnail;
        set
        {
            if (ReferenceEquals(_coverThumbnail, value))
                return;
            _coverThumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverThumbnail)));
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Artist) || Artist == "未知艺术家"
        ? Title
        : $"{Title} — {Artist}";

    public string CountText => IsFavorite ? $"♥ {TrackCount:N0} 首" : $"{TrackCount:N0} 首";
    public string FavoriteGlyph => IsFavorite ? "♥" : "♡";
    public string FavoriteToolTip => IsFavorite ? "取消收藏这张专辑" : "收藏这张专辑";
    public string SearchText => $"{Title} {Artist} {CircleNames}".ToLowerInvariant();

    public override string ToString() => DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CircleViewModel : INotifyPropertyChanged
{
    private BitmapSource? _coverThumbnail;

    public string Key { get; init; } = "";
    public string Name { get; init; } = "未识别社团";
    public int AlbumCount { get; init; }
    public int TrackCount { get; init; }
    public TrackModel? RepresentativeTrack { get; init; }

    [JsonIgnore]
    public BitmapSource? CoverThumbnail
    {
        get => _coverThumbnail;
        set
        {
            if (ReferenceEquals(_coverThumbnail, value))
                return;
            _coverThumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverThumbnail)));
        }
    }

    public string CountText => $"{AlbumCount:N0} 张专辑 · {TrackCount:N0} 首";
    public string SearchText => Name.ToLowerInvariant();
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class CircleIdentity
{
    public const string UnknownKey = "circle::unknown";

    public static string Create(TrackModel track) => Create(track.Circle);

    public static string Create(string? circle)
    {
        if (string.IsNullOrWhiteSpace(circle))
            return UnknownKey;

        var normalized = circle.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber ||
                CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol)
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        var key = builder.ToString().Trim();
        return key.Length == 0 ? UnknownKey : $"circle::{key}";
    }
}

public static class AlbumIdentity
{
    public static string Create(TrackModel track)
    {
        var albumFolder = GetAlbumFolder(track.FilePath);
        var albumTitle = FirstNonEmpty(track.Album, Path.GetFileName(albumFolder), "未知专辑");
        var normalizedAlbum = NormalizeAlbum(albumTitle);
        if (!IsUsableAlbumTitle(albumTitle, normalizedAlbum))
        {
            if (!string.IsNullOrWhiteSpace(albumFolder))
                return $"folder::{NormalizePath(albumFolder)}";
            return $"unknown::{track.Id}";
        }

        return $"album::{normalizedAlbum}";
    }

    public static string Create(string? albumArtist, string? artist, string? album)
    {
        var title = FirstNonEmpty(album, "未知专辑");
        var normalizedAlbum = NormalizeAlbum(title);
        if (!IsUsableAlbumTitle(title, normalizedAlbum))
            return $"tag::{Normalize(FirstNonEmpty(albumArtist, artist, "未知艺术家"))}::{normalizedAlbum}";
        return $"album::{normalizedAlbum}";
    }

    public static string FolderScopedFallback(TrackModel track)
    {
        var albumFolder = GetAlbumFolder(track.FilePath);
        var albumTitle = FirstNonEmpty(track.Album, Path.GetFileName(albumFolder), "未知专辑");
        if (!string.IsNullOrWhiteSpace(albumFolder))
            return $"folder::{NormalizePath(albumFolder)}::{NormalizeAlbum(albumTitle)}";
        return Create(track.AlbumArtist, track.Artist, albumTitle);
    }

    public static string DisplayArtist(TrackModel track) =>
        FirstNonEmpty(track.AlbumArtist, track.Artist, "未知艺术家");

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string GetAlbumFolder(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory))
                return "";

            var folderName = Path.GetFileName(directory);
            if (DiscFolderRegex.IsMatch(folderName))
            {
                var parent = Directory.GetParent(directory)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    return parent;
            }

            return directory;
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
        catch
        {
            return path.Trim().ToUpperInvariant();
        }
    }

    private static string NormalizeAlbum(string value)
    {
        var withoutDecorations = BracketedSuffixRegex.Replace(value, "");
        withoutDecorations = AlbumNoiseRegex.Replace(withoutDecorations, "");
        return Normalize(withoutDecorations);
    }

    private static bool IsUsableAlbumTitle(string value, string normalized)
    {
        if (normalized.Length < 2 && !normalized.Any(IsMeaningfulSymbol))
            return false;
        return !GenericAlbumNames.Contains(normalized);
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber ||
                IsMeaningfulSymbol(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }

    private static bool IsMeaningfulSymbol(char character)
    {
        if (MeaningfulAlbumSymbols.Contains(character))
            return true;

        return CharUnicodeInfo.GetUnicodeCategory(character) is
            UnicodeCategory.MathSymbol or
            UnicodeCategory.OtherSymbol or
            UnicodeCategory.ModifierSymbol;
    }

    private static readonly Regex DiscFolderRegex = new(
        @"^(?:cd|disc|disk|vol(?:ume)?|第?\s*\d+\s*[枚卷碟]|disk)\s*[-_ ]?\s*\d+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BracketedSuffixRegex = new(
        @"[\(\[【（].*?(?:flac|mp3|wav|aac|m4a|hi[\s-]?res|lossless|无损|自抓|抓轨|分轨|整轨|bk|booklet|scan|scans|log|cue).*?[\)\]】）]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlbumNoiseRegex = new(
        @"(?i)\b(?:flac|mp3|wav|aac|m4a|hi[\s-]?res|lossless|remaster(?:ed)?|limited edition|bonus disc)\b|无损|自抓|抓轨|分轨|整轨|限定版|通常版|初回|扫图|歌词本",
        RegexOptions.Compiled);

    private static readonly HashSet<string> GenericAlbumNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "未知专辑",
        "unknown album",
        "unknown",
        "album",
        "albums",
        "single",
        "singles",
        "cd",
        "disc",
        "disk",
        "ost",
        "original soundtrack",
        "various artists",
        "v a",
        "va"
    };

    private static readonly HashSet<char> MeaningfulAlbumSymbols = new()
    {
        '△', '▽', '▲', '▼', '○', '●', '◎', '◇', '◆', '□', '■', '☆', '★',
        '∞', '∴', '∵', '※', '＊', '♪', '♫', '♬', '♭', '♯', '＋', '−', '×',
        '÷', '＝', '≠', '≈', '≡', 'Ⅰ', 'Ⅱ', 'Ⅲ', 'Ⅳ', 'Ⅴ', 'Ⅵ', 'Ⅶ', 'Ⅷ',
        'Ⅸ', 'Ⅹ', 'Ⅺ', 'Ⅻ'
    };
}

public sealed class AppState
{
    public List<string> LibraryFolders { get; set; } = [];
    public List<TrackModel> Tracks { get; set; } = [];
    public List<PlaylistModel> Playlists { get; set; } = [];
    public List<string> FavoriteAlbumKeys { get; set; } = [];
    public int Volume { get; set; } = 76;
    public bool DesktopLyricsEnabled { get; set; }
    public string RepeatMode { get; set; } = "All";
    public bool ShuffleEnabled { get; set; }
    public string ShuffleMode { get; set; } = "Off";
    public double PlaybackRate { get; set; } = 1.0;
    public string VisualizationMode { get; set; } = "Off";
    public string HardwareDecoding { get; set; } = "Auto";
    public string VideoOutput { get; set; } = "Auto";
    public string AudioBackend { get; set; } = "DirectSound";
    public string PreferredAudioDeviceId { get; set; } = "";
    public bool InPlayerBilingualSubtitles { get; set; } = true;
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public bool SystemMediaKeysEnabled { get; set; } = true;
    public bool ScanOnStartup { get; set; }
    public string UiFontFamily { get; set; } = "Microsoft YaHei UI";
    public bool RunAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public string CloseBehavior { get; set; } = "Exit";
    public bool AutoCloseEnabled { get; set; }
    public int AutoCloseMinutes { get; set; }
    public bool AutoPlayOnStartup { get; set; }
    public bool RememberPlaybackProgress { get; set; } = true;
    public string DoubleClickQueueMode { get; set; } = "Replace";
    public string? LastTrackId { get; set; }
    public long LastPlaybackPositionMs { get; set; }
    public int LyricOffsetMs { get; set; }
    public bool DesktopLyricsTopmost { get; set; } = true;
    public bool DesktopLyricsShowTranslation { get; set; } = true;
    public bool DesktopLyricsBold { get; set; } = true;
    public bool DesktopLyricsStroke { get; set; } = true;
    public bool DesktopLyricsLocked { get; set; }
    public bool DesktopLyricsClickToActivate { get; set; }
    public double DesktopLyricsFontSize { get; set; } = 25;
    public double DesktopLyricsTranslationFontSize { get; set; } = 17;
    public double DesktopLyricsBackgroundOpacity { get; set; } = 0.5;
    public double DesktopLyricsWidth { get; set; } = 760;
    public string DesktopLyricsFontFamily { get; set; } = "Microsoft YaHei UI";
    public string DesktopLyricsFontWeight { get; set; } = "SemiBold";
    public string DesktopLyricsColorScheme { get; set; } = "MintPurple";
    public string DesktopLyricsPrimaryColor { get; set; } = "#C9B7FF";
    public string DesktopLyricsSecondaryColor { get; set; } = "#79D9A9";
    public string DesktopLyricsStrokeColor { get; set; } = "#000000";
    public bool DesktopLyricsUseGradient { get; set; } = true;
    public string DesktopLyricsLayout { get; set; } = "Stacked";
    public string DesktopLyricsAlignment { get; set; } = "Center";
    public double? DesktopLyricsLeft { get; set; }
    public double? DesktopLyricsTop { get; set; }
}

public sealed record ScanProgress(int Scanned, int Total, string CurrentFile, int Errors);

public sealed record MediaTrackOption(int Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record AudioDeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class MediaDetails
{
    public uint Width { get; init; }
    public uint Height { get; init; }
    public double FrameRate { get; init; }
    public uint SampleRate { get; init; }
    public uint Channels { get; init; }
    public int AudioBitrate { get; init; }
    public int VideoBitrate { get; init; }
    public string AudioCodec { get; init; } = "-";
    public string VideoCodec { get; init; } = "-";
    public bool HasAudio { get; init; }
    public bool HasVideo { get; init; }
}

public sealed class LyricLine : INotifyPropertyChanged
{
    private bool _isCurrent;

    public long TimeMs { get; init; }
    public string Original { get; init; } = "";
    public string Translation { get; set; } = "";

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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
