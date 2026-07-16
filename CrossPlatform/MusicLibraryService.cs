using System.Collections.Concurrent;
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

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "$RECYCLE.BIN", "System Volume Information", "_重复文件待确认"
    };

    private static readonly Regex FileNameMetadataRegex = new(
        @"^(?:\d{1,3}(?:\s*[.\-_、]\s*|\s+))?(?<title>.+?)\s+-\s+(?<artist>.+)$",
        RegexOptions.Compiled);

    public async Task<List<TrackModel>> ScanAsync(
        IReadOnlyCollection<string> roots,
        IReadOnlyCollection<TrackModel> existing,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = roots.Where(Directory.Exists)
            .SelectMany(EnumerateMediaFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existingById = existing
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var tracks = new ConcurrentBag<TrackModel>();
        var scanned = 0;
        var fallbacks = 0;

        Directory.CreateDirectory(AppStore.ArtworkCacheDirectory);
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (path, token) =>
            {
                var id = CreateTrackId(path);
                existingById.TryGetValue(id, out var old);
                try
                {
                    tracks.Add(ReadTrack(path, id, old));
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref fallbacks);
                    tracks.Add(ReadFallbackTrack(path, id, old));
                    DiagnosticLog.Write("LibraryScan", $"元数据读取失败，已按文件名收录：{path}", exception);
                }
                finally
                {
                    var current = Interlocked.Increment(ref scanned);
                    if (current == 1 || current % 25 == 0 || current == files.Count)
                        progress?.Report(new ScanProgress(current, files.Count, path, fallbacks));
                }
                return ValueTask.CompletedTask;
            });

        return tracks
            .OrderBy(track => track.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static TrackModel ReadTrack(string path, string id, TrackModel? old)
    {
        if (string.Equals(Path.GetExtension(path), ".ncm", StringComparison.OrdinalIgnoreCase))
            return ReadFallbackTrack(path, id, old);

        using var file = TagFile.Create(path);
        var tag = file.Tag;
        var artist = FirstNonEmpty(tag.FirstPerformer, tag.FirstAlbumArtist, old?.Artist, "未知艺术家");
        var albumArtist = FirstNonEmpty(tag.FirstAlbumArtist, old?.AlbumArtist, artist);
        var title = FirstNonEmpty(tag.Title, Path.GetFileNameWithoutExtension(path));
        var album = FirstNonEmpty(tag.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑");
        var artwork = ResolveArtwork(path, tag, old?.ArtworkPath);

        return new TrackModel
        {
            Id = id,
            FilePath = path,
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            Circle = InferCircle(tag, albumArtist, old?.Circle),
            Genre = FirstNonEmpty(tag.FirstGenre, old?.Genre, ""),
            Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            TrackNumber = tag.Track,
            DurationMs = (long)file.Properties.Duration.TotalMilliseconds,
            ArtworkPath = artwork,
            CloudId = old?.CloudId,
            CloudIds = old?.CloudIds?.ToList() ?? [],
            Categories = old?.Categories?.ToList() ?? [],
            AddedAt = old?.AddedAt ?? SafeCreationTime(path),
            IsFavorite = old?.IsFavorite ?? false,
            PlayCount = old?.PlayCount ?? 0
        };
    }

    private static TrackModel ReadFallbackTrack(string path, string id, TrackModel? old)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).Trim();
        var match = FileNameMetadataRegex.Match(fileName);
        var title = match.Success ? match.Groups["title"].Value.Trim() : fileName;
        var artist = match.Success ? match.Groups["artist"].Value.Trim() : FirstNonEmpty(old?.Artist, "未知艺术家");
        var album = FirstNonEmpty(old?.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑");

        return new TrackModel
        {
            Id = id,
            FilePath = path,
            Title = FirstNonEmpty(old?.Title, title),
            Artist = artist,
            AlbumArtist = FirstNonEmpty(old?.AlbumArtist, artist),
            Album = album,
            Circle = old?.Circle ?? "",
            Genre = old?.Genre ?? "",
            Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            TrackNumber = old?.TrackNumber ?? 0,
            DurationMs = old?.DurationMs ?? 0,
            ArtworkPath = FindSidecar(path) ?? old?.ArtworkPath ?? "",
            CloudId = old?.CloudId,
            CloudIds = old?.CloudIds?.ToList() ?? [],
            Categories = old?.Categories?.ToList() ?? [],
            AddedAt = old?.AddedAt ?? SafeCreationTime(path),
            IsFavorite = old?.IsFavorite ?? false,
            PlayCount = old?.PlayCount ?? 0
        };
    }

    private static string ResolveArtwork(string path, TagLib.Tag tag, string? oldArtwork)
    {
        var sidecar = FindSidecar(path);
        if (sidecar is not null)
            return sidecar;

        var picture = tag.Pictures.FirstOrDefault(item => item.Data?.Data is { Length: > 0 });
        if (picture?.Data?.Data is { Length: > 0 } bytes)
        {
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var cachePath = Path.Combine(AppStore.ArtworkCacheDirectory, $"{hash}.cover");
                if (!File.Exists(cachePath))
                    File.WriteAllBytes(cachePath, bytes);
                return cachePath;
            }
            catch
            {
            }
        }

        return !string.IsNullOrWhiteSpace(oldArtwork) && File.Exists(oldArtwork) ? oldArtwork : "";
    }

    private static string? FindSidecar(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var names = new[] { stem, "cover", "folder", "front", "album" };
        var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
        try
        {
            var files = Directory.GetFiles(directory);
            foreach (var name in names)
            foreach (var extension in extensions)
            {
                var expected = name + extension;
                var match = files.FirstOrDefault(file =>
                    string.Equals(Path.GetFileName(file), expected, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return match;
            }
        }
        catch
        {
        }
        return null;
    }

    private static string InferCircle(TagLib.Tag tag, string albumArtist, string? oldCircle)
    {
        if (!string.IsNullOrWhiteSpace(oldCircle))
            return oldCircle;
        var grouping = tag.Grouping?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(grouping))
            return grouping;
        return IsGenericArtist(albumArtist) ? "" : albumArtist;
    }

    private static bool IsGenericArtist(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return normalized is "" or "va" or "variousartists" or "unknownartist" or "未知艺术家";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private static DateTime SafeCreationTime(string path)
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

    private static IEnumerable<string> EnumerateMediaFiles(string root)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string fullDirectory;
            try
            {
                fullDirectory = Path.GetFullPath(directory);
            }
            catch
            {
                continue;
            }
            if (!visited.Add(fullDirectory))
                continue;

            string[] childDirectories;
            string[] files;
            try
            {
                childDirectories = Directory.GetDirectories(fullDirectory);
                files = Directory.GetFiles(fullDirectory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Write("LibraryScan", $"无法枚举目录：{fullDirectory}", exception);
                continue;
            }

            foreach (var child in childDirectories)
            {
                if (SkippedDirectories.Contains(Path.GetFileName(child)))
                    continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch
                {
                }
            }

            foreach (var file in files)
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
        }
    }

    public static string CreateTrackId(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }
}
