using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TagFile = TagLib.File;

namespace OfflineMusicLibrary;

public sealed class MusicLibraryService
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".mp4", ".ogg", ".opus", ".wav", ".wma", ".aac", ".ape", ".ncm",
        ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".mpeg", ".mpg"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".mpeg", ".mpg"
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".agents", "$RECYCLE.BIN", "System Volume Information", "_重复文件待确认"
    };

    public async Task<List<TrackModel>> ScanAsync(
        IReadOnlyCollection<string> roots,
        IReadOnlyCollection<TrackModel> existing,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = roots
            .Where(Directory.Exists)
            .SelectMany(EnumerateMediaFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingById = existing
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var tracks = new ConcurrentBag<TrackModel>();
        var scanned = 0;
        var errors = 0;

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (path, token) =>
            {
                try
                {
                    var id = CreateTrackId(path);
                    existingById.TryGetValue(id, out var old);
                    tracks.Add(ReadTrack(path, id, old));
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref errors);
                    try
                    {
                        var id = CreateTrackId(path);
                        existingById.TryGetValue(id, out var old);
                        tracks.Add(ReadFallbackTrack(path, id, old));
                        DiagnosticLog.Write("LibraryScan",
                            $"元数据读取失败，已按文件名收录：{path}", exception);
                    }
                    catch (Exception fallbackException)
                    {
                        DiagnosticLog.Write("LibraryScan",
                            $"文件无法收录：{path}", fallbackException);
                    }
                }
                finally
                {
                    var current = Interlocked.Increment(ref scanned);
                    if (current == 1 || current % 25 == 0 || current == files.Count)
                        progress?.Report(new ScanProgress(current, files.Count, path, errors));
                }
                return ValueTask.CompletedTask;
            });

        var scannedTracks = tracks.ToList();
        RestoreMovedTrackState(scannedTracks, existing);
        return scannedTracks
            .OrderBy(track => track.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void RestoreMovedTrackState(List<TrackModel> scanned, IReadOnlyCollection<TrackModel> existing)
    {
        var scannedIds = scanned.Select(track => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingIds = existing.Select(track => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldCandidates = existing
            .Where(track => !scannedIds.Contains(track.Id))
            .OrderByDescending(track => track.HasCloudIds)
            .ThenByDescending(track => track.IsFavorite || track.Categories.Count > 0)
            .ThenByDescending(track => track.PlayCount)
            .ToList();
        var newCandidates = scanned.Where(track => !existingIds.Contains(track.Id)).ToList();
        var claimed = new HashSet<TrackModel>();

        foreach (var oldTrack in oldCandidates)
        {
            var ranked = newCandidates
                .Where(track => !claimed.Contains(track))
                .Select(track => new { Track = track, Score = MovedTrackScore(oldTrack, track) })
                .Where(item => item.Score >= 100)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Track.IsEncryptedNcm)
                .ToList();
            if (ranked.Count == 0)
                continue;
            if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score)
                continue;

            var replacement = ranked[0].Track;
            claimed.Add(replacement);
            replacement.Id = oldTrack.Id;
            replacement.IsFavorite = oldTrack.IsFavorite;
            replacement.PlayCount = oldTrack.PlayCount;
            replacement.LastPlayedAt = oldTrack.LastPlayedAt;
            replacement.AddedAt = oldTrack.AddedAt;
            replacement.Categories = oldTrack.Categories.ToList();
            replacement.CloudId = oldTrack.CloudId;
            replacement.CloudIds = oldTrack.CloudIds?.ToList() ?? [];
            if (oldTrack.CircleIsManual)
            {
                replacement.Circle = oldTrack.Circle;
                replacement.CircleIsManual = true;
            }
        }
    }

    private static int MovedTrackScore(TrackModel oldTrack, TrackModel newTrack)
    {
        var oldTitle = NormalizeIdentityText(oldTrack.Title);
        var newTitle = NormalizeIdentityText(newTrack.Title);
        var oldFileName = NormalizeIdentityText(Path.GetFileNameWithoutExtension(oldTrack.FilePath));
        var newFileName = NormalizeIdentityText(Path.GetFileNameWithoutExtension(newTrack.FilePath));
        var titleMatches = oldTitle.Length > 0 && oldTitle == newTitle;
        var fileNameMatches = oldFileName.Length > 0 && oldFileName == newFileName;
        if (!titleMatches && !fileNameMatches)
            return 0;

        if (oldTrack.DurationMs > 0 && newTrack.DurationMs > 0 &&
            Math.Abs(oldTrack.DurationMs - newTrack.DurationMs) > 3000)
            return 0;

        var score = titleMatches ? 100 : 0;
        score += fileNameMatches ? 80 : 0;
        if (oldTrack.DurationMs > 0 && newTrack.DurationMs > 0)
            score += Math.Abs(oldTrack.DurationMs - newTrack.DurationMs) <= 1200 ? 35 : 20;
        if (NormalizeIdentityText(oldTrack.Artist) == NormalizeIdentityText(newTrack.Artist))
            score += 28;
        if (NormalizeIdentityText(oldTrack.Album) == NormalizeIdentityText(newTrack.Album))
            score += 18;
        if (oldTrack.TrackNumber > 0 && oldTrack.TrackNumber == newTrack.TrackNumber)
            score += 12;
        return score;
    }

    private static string NormalizeIdentityText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (char.IsLetterOrDigit(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.LetterNumber or UnicodeCategory.MathSymbol or UnicodeCategory.OtherSymbol)
                builder.Append(character);
        return builder.ToString();
    }

    public static TrackModel ReadTrack(string path, TrackModel? old = null) =>
        ReadTrack(path, CreateTrackId(path), old);

    private static TrackModel ReadTrack(string path, string id, TrackModel? old)
    {
        if (string.Equals(Path.GetExtension(path), ".ncm", StringComparison.OrdinalIgnoreCase))
            return ReadNcmTrack(path, id, old);

        using var file = TagFile.Create(path);
        var tag = file.Tag;
        var title = string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title.Trim();
        var artist = FirstNonEmpty(tag.FirstPerformer, tag.FirstAlbumArtist, "未知艺术家");
        var album = FirstNonEmpty(tag.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑");
        var circle = old?.CircleIsManual == true ? old.Circle : InferCircle(tag);
        var hasLyrics = LyricsService.FindMainLyricsPath(path) is not null;

        return new TrackModel
        {
            Id = id,
            FilePath = path,
            Title = title,
            Artist = artist,
            AlbumArtist = FirstNonEmpty(tag.FirstAlbumArtist, artist),
            Album = album,
            Circle = circle,
            CircleIsManual = old?.CircleIsManual ?? false,
            Genre = FirstNonEmpty(tag.FirstGenre, ""),
            Year = (int)tag.Year,
            TrackNumber = tag.Track,
            DurationMs = (long)file.Properties.Duration.TotalMilliseconds,
            Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            HasCover = tag.Pictures.Length > 0 || CoverService.FindSidecar(path) is not null,
            HasLyrics = hasLyrics,
            IsVideo = VideoExtensions.Contains(Path.GetExtension(path)),
            PlayCount = old?.PlayCount ?? 0,
            LastPlayedAt = old?.LastPlayedAt,
            AddedAt = old?.AddedAt ?? File.GetCreationTime(path),
            Categories = old?.Categories.ToList() ?? [],
            CloudId = old?.CloudId,
            CloudIds = old?.CloudIds?.ToList() ?? [],
            IsFavorite = old?.IsFavorite ?? false
        };
    }

    private static TrackModel ReadNcmTrack(string path, string id, TrackModel? old)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).Trim();
        var match = NcmFileNameRegex.Match(fileName);
        var title = match.Success ? match.Groups["title"].Value.Trim() : fileName;
        var artist = match.Success ? match.Groups["artist"].Value.Trim() : "未知艺术家";
        var album = FirstNonEmpty(old?.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑");

        return new TrackModel
        {
            Id = id,
            FilePath = path,
            Title = title,
            Artist = artist,
            AlbumArtist = FirstNonEmpty(old?.AlbumArtist, artist),
            Album = album,
            Circle = old?.Circle ?? "",
            CircleIsManual = old?.CircleIsManual ?? false,
            Genre = old?.Genre ?? "",
            Year = old?.Year ?? 0,
            TrackNumber = old?.TrackNumber ?? 0,
            DurationMs = old?.DurationMs ?? 0,
            Format = "NCM",
            HasCover = CoverService.FindSidecar(path) is not null,
            HasLyrics = LyricsService.FindMainLyricsPath(path) is not null,
            IsVideo = false,
            PlayCount = old?.PlayCount ?? 0,
            LastPlayedAt = old?.LastPlayedAt,
            AddedAt = old?.AddedAt ?? File.GetCreationTime(path),
            Categories = old?.Categories.ToList() ?? [],
            CloudId = old?.CloudId,
            CloudIds = old?.CloudIds?.ToList() ?? [],
            IsFavorite = old?.IsFavorite ?? false
        };
    }

    private static TrackModel ReadFallbackTrack(string path, string id, TrackModel? old)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).Trim();
        var match = NcmFileNameRegex.Match(fileName);
        var title = match.Success ? match.Groups["title"].Value.Trim() : fileName;
        var artist = match.Success ? match.Groups["artist"].Value.Trim() : FirstNonEmpty(old?.Artist, "未知艺术家");
        var extension = Path.GetExtension(path);

        return new TrackModel
        {
            Id = id,
            FilePath = path,
            Title = FirstNonEmpty(old?.Title, title, fileName),
            Artist = artist,
            AlbumArtist = FirstNonEmpty(old?.AlbumArtist, artist),
            Album = FirstNonEmpty(old?.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑"),
            Circle = old?.Circle ?? "",
            CircleIsManual = old?.CircleIsManual ?? false,
            Genre = old?.Genre ?? "",
            Year = old?.Year ?? 0,
            TrackNumber = old?.TrackNumber ?? 0,
            DurationMs = old?.DurationMs ?? 0,
            Format = extension.TrimStart('.').ToUpperInvariant(),
            HasCover = SafeHasCover(path),
            HasLyrics = SafeHasLyrics(path),
            IsVideo = VideoExtensions.Contains(extension),
            PlayCount = old?.PlayCount ?? 0,
            LastPlayedAt = old?.LastPlayedAt,
            AddedAt = old?.AddedAt ?? SafeGetCreationTime(path),
            Categories = old?.Categories.ToList() ?? [],
            CloudId = old?.CloudId,
            CloudIds = old?.CloudIds?.ToList() ?? [],
            IsFavorite = old?.IsFavorite ?? false
        };
    }

    private static bool SafeHasCover(string path)
    {
        try
        {
            return CoverService.FindSidecar(path) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeHasLyrics(string path)
    {
        try
        {
            return LyricsService.FindMainLyricsPath(path) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime SafeGetCreationTime(string path)
    {
        try
        {
            return File.GetCreationTime(path);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static string InferCircle(TagLib.Tag tag)
    {
        var labeled = ExtractLabeledCircle(tag.Comment);
        if (labeled.Length > 0)
            return labeled;

        labeled = ExtractLabeledCircle(tag.Grouping);
        if (labeled.Length > 0)
            return labeled;

        var albumArtists = tag.AlbumArtists
            .Where(value => !string.IsNullOrWhiteSpace(value) && !IsGenericArtist(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (albumArtists.Count > 0)
            return string.Join(" / ", albumArtists);

        var grouping = tag.Grouping?.Trim() ?? "";
        return IsGenericArtist(grouping) ? "" : grouping;
    }

    private static string ExtractLabeledCircle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var match = CircleLabelRegex.Match(value);
        return match.Success ? match.Groups["name"].Value.Trim() : "";
    }

    private static bool IsGenericArtist(string value)
    {
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
        return normalized.Length == 0 || normalized is "va" or "variousartists" or "unknownartist" or "未知艺术家";
    }

    private static readonly Regex CircleLabelRegex = new(
        @"(?:^|[;；|\r\n])\s*(?:circle|社团|社團|サークル)\s*[:：=]\s*(?<name>[^;；|\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NcmFileNameRegex = new(
        @"^(?:\d{1,3}(?:\s*[.\-_、]\s*|\s+))?(?<title>.+?)\s+-\s+(?<artist>.+)$",
        RegexOptions.Compiled);

    private static IEnumerable<string> EnumerateMediaFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] childDirectories;
            string[] files;
            try
            {
                childDirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                DiagnosticLog.Write("LibraryScan", $"无法枚举目录：{directory}", exception);
                continue;
            }

            foreach (var child in childDirectories)
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                    pending.Push(child);

            foreach (var file in files)
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
        }
    }

    public static string CreateTrackId(string path)
    {
        var normalized = Path.GetFullPath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }
}
