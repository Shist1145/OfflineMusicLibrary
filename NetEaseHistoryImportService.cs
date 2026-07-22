using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineMusicLibrary;

public sealed record NetEaseHistoryEntry(
    string CloudId,
    string Title,
    string Artist,
    string Album,
    int PlayCount,
    DateTime? LastPlayedAt);

public sealed record NetEaseHistoryImportResult(
    int SourceRecordCount,
    int MatchedRecordCount,
    int MatchedTrackCount,
    int ExactMatchCount,
    int FuzzyMatchCount,
    int AmbiguousCount,
    int PlayCountIncrease,
    int LastPlayedUpdateCount,
    IReadOnlyList<NetEaseHistoryEntry> Unmatched);

public static class NetEaseHistoryImportService
{
    public static async Task<NetEaseHistoryImportResult> ImportAsync(
        string filePath,
        IReadOnlyList<TrackModel> localTracks,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("找不到要导入的网易云播放历史文件。", filePath);

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var entries = Parse(content, Path.GetExtension(filePath));
        if (entries.Count == 0)
            throw new FormatException("文件中没有识别到播放历史。支持网易云记录接口 JSON，以及含歌名、歌手和播放次数列的 CSV / TSV。");

        var exact = 0;
        var fuzzy = 0;
        var ambiguous = 0;
        var matchedRecords = 0;
        var playIncrease = 0;
        var lastPlayedUpdates = 0;
        var matchedTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<NetEaseHistoryEntry>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = Match(entry, localTracks);
            if (match.Track is null)
            {
                if (match.Ambiguous)
                    ambiguous++;
                unmatched.Add(entry);
                continue;
            }

            matchedRecords++;
            matchedTrackIds.Add(match.Track.Id);
            if (match.Exact)
                exact++;
            else
                fuzzy++;

            var importedPlayCount = Math.Max(1, entry.PlayCount);
            if (importedPlayCount > match.Track.PlayCount)
            {
                playIncrease += importedPlayCount - match.Track.PlayCount;
                match.Track.PlayCount = importedPlayCount;
            }

            if (entry.LastPlayedAt.HasValue &&
                (!match.Track.LastPlayedAt.HasValue || entry.LastPlayedAt.Value > match.Track.LastPlayedAt.Value))
            {
                match.Track.LastPlayedAt = entry.LastPlayedAt;
                lastPlayedUpdates++;
            }

            if (!string.IsNullOrWhiteSpace(entry.CloudId))
                match.Track.RememberCloudId(entry.CloudId);
        }

        return new NetEaseHistoryImportResult(
            entries.Count,
            matchedRecords,
            matchedTrackIds.Count,
            exact,
            fuzzy,
            ambiguous,
            playIncrease,
            lastPlayedUpdates,
            unmatched);
    }

    public static IReadOnlyList<NetEaseHistoryEntry> Parse(string content, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var trimmed = content.AsSpan().TrimStart();
        var looksLikeJson = trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
        var parsed = looksLikeJson || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
            ? ParseJson(content)
            : ParseDelimited(content);
        return MergeDuplicates(parsed);
    }

    private static List<NetEaseHistoryEntry> ParseJson(string content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var elements = SelectHistoryElements(document.RootElement);
        var result = new List<NetEaseHistoryEntry>();
        foreach (var element in elements)
        {
            if (TryParseJsonEntry(element, out var entry))
                result.Add(entry);
        }
        return result;
    }

    private static IReadOnlyList<JsonElement> SelectHistoryElements(JsonElement root)
    {
        foreach (var preferredName in new[] { "allData", "history", "records", "record", "weekData" })
        {
            if (TryFindNamedArray(root, preferredName, out var preferred))
                return preferred.EnumerateArray().ToArray();
        }

        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToArray();

        var found = new List<JsonElement>();
        CollectRecordObjects(root, found);
        return found;
    }

    private static bool TryFindNamedArray(JsonElement element, string name, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    found = property.Value;
                    return true;
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object &&
                    TryFindNamedArray(property.Value, name, out found))
                    return true;
            }
        }

        found = default;
        return false;
    }

    private static void CollectRecordObjects(JsonElement element, List<JsonElement> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectRecordObjects(item, found);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (HasProperty(element, "song") || HasProperty(element, "track") ||
            ((HasProperty(element, "name") || HasProperty(element, "title") || HasProperty(element, "songName")) &&
             (HasProperty(element, "playCount") || HasProperty(element, "plays") || HasProperty(element, "listenCount"))))
        {
            found.Add(element);
            return;
        }

        foreach (var property in element.EnumerateObject())
            CollectRecordObjects(property.Value, found);
    }

    private static bool TryParseJsonEntry(JsonElement record, out NetEaseHistoryEntry entry)
    {
        entry = default!;
        if (record.ValueKind != JsonValueKind.Object)
            return false;

        var song = GetObject(record, "song") ?? GetObject(record, "track") ?? record;
        var id = ReadString(song, "id", "songId", "trackId") ?? ReadString(record, "songId", "trackId", "id") ?? "";
        var title = ReadString(song, "name", "title", "songName") ?? ReadString(record, "name", "title", "songName") ?? "";
        var artist = ReadArtists(song);
        if (string.IsNullOrWhiteSpace(artist))
            artist = ReadArtists(record);
        var album = ReadAlbum(song);
        if (string.IsNullOrWhiteSpace(album))
            album = ReadAlbum(record);
        var playCount = ReadInt(record, "playCount", "plays", "listenCount", "count", "frequency") ?? 1;
        var lastPlayed = ReadDate(record, "lastPlayedAt", "lastPlayTime", "playTime", "timestamp", "time", "playedAt");

        if (string.IsNullOrWhiteSpace(title))
            return false;
        entry = new NetEaseHistoryEntry(id.Trim(), title.Trim(), artist.Trim(), album.Trim(), Math.Max(1, playCount), lastPlayed);
        return true;
    }

    private static List<NetEaseHistoryEntry> ParseDelimited(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return [];

        var separator = CountOutsideQuotes(lines[0], '\t') > CountOutsideQuotes(lines[0], ',') ? '\t' : ',';
        var headers = ParseDelimitedLine(lines[0], separator).Select(Normalize).ToArray();
        var result = new List<NetEaseHistoryEntry>();
        for (var row = 1; row < lines.Length; row++)
        {
            var values = ParseDelimitedLine(lines[row], separator);
            string Value(params string[] names)
            {
                foreach (var name in names)
                {
                    var index = Array.IndexOf(headers, Normalize(name));
                    if (index >= 0 && index < values.Count)
                        return values[index].Trim();
                }
                return "";
            }

            var title = Value("title", "name", "song", "songname", "歌名", "歌曲", "标题");
            if (string.IsNullOrWhiteSpace(title))
                continue;
            var countText = Value("playcount", "plays", "listencount", "count", "frequency", "播放次数", "次数");
            _ = int.TryParse(countText, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var count);
            var date = ParseDate(Value("lastplayedat", "lastplaytime", "playtime", "timestamp", "playedat", "最近播放", "最后播放时间", "播放时间"));
            result.Add(new NetEaseHistoryEntry(
                Value("id", "songid", "trackid", "歌曲id", "音乐id"),
                title,
                Value("artist", "artists", "singer", "artistname", "歌手", "艺人"),
                Value("album", "albumname", "专辑"),
                Math.Max(1, count),
                date));
        }
        return result;
    }

    private static IReadOnlyList<NetEaseHistoryEntry> MergeDuplicates(IEnumerable<NetEaseHistoryEntry> entries)
    {
        var merged = new Dictionary<string, NetEaseHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = !string.IsNullOrWhiteSpace(entry.CloudId)
                ? "id:" + entry.CloudId.Trim()
                : "name:" + Normalize(entry.Title) + "|" + Normalize(entry.Artist);
            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = entry;
                continue;
            }
            merged[key] = existing with
            {
                Artist = string.IsNullOrWhiteSpace(existing.Artist) ? entry.Artist : existing.Artist,
                Album = string.IsNullOrWhiteSpace(existing.Album) ? entry.Album : existing.Album,
                PlayCount = Math.Max(existing.PlayCount, entry.PlayCount),
                LastPlayedAt = Later(existing.LastPlayedAt, entry.LastPlayedAt)
            };
        }
        return merged.Values.ToArray();
    }

    private static (TrackModel? Track, bool Exact, bool Ambiguous) Match(
        NetEaseHistoryEntry entry,
        IReadOnlyList<TrackModel> localTracks)
    {
        if (!string.IsNullOrWhiteSpace(entry.CloudId))
        {
            var exact = localTracks.Where(track => track.HasCloudId(entry.CloudId)).ToList();
            if (exact.Count > 0)
                return (PreferPlayable(exact), true, false);
        }

        var title = Normalize(entry.Title);
        if (title.Length == 0)
            return (null, false, false);
        var candidates = localTracks.Where(track => Normalize(track.Title) == title).ToList();
        if (candidates.Count == 0)
            return (null, false, false);

        var remoteArtist = Normalize(entry.Artist);
        var remoteAlbum = Normalize(entry.Album);
        var scored = candidates.Select(track =>
        {
            var localArtist = Normalize(track.Artist);
            var artistExact = remoteArtist.Length > 0 && localArtist == remoteArtist;
            var artistOverlap = artistExact || ArtistTokens(entry.Artist).Overlaps(ArtistTokens(track.Artist));
            var albumExact = remoteAlbum.Length > 0 && Normalize(track.Album) == remoteAlbum;
            var safe = artistOverlap || (albumExact && candidates.Count == 1);
            var score = (artistExact ? 6 : artistOverlap ? 4 : 0) + (albumExact ? 2 : 0);
            return (Track: track, Safe: safe, Score: score);
        }).Where(item => item.Safe).OrderByDescending(item => item.Score).ToList();

        if (scored.Count == 0)
            return (null, false, candidates.Count > 1);
        var bestScore = scored[0].Score;
        var best = scored.Where(item => item.Score == bestScore).Select(item => item.Track).ToList();
        if (best.Count > 1)
            return (null, false, true);
        return (PreferPlayable(best), false, false);
    }

    private static TrackModel PreferPlayable(IEnumerable<TrackModel> tracks) => tracks
        .OrderBy(track => track.IsEncryptedNcm)
        .ThenByDescending(track => File.Exists(track.FilePath))
        .First();

    private static HashSet<string> ArtistTokens(string value)
    {
        var prepared = value;
        foreach (var separator in new[] { "/", "&", "、", "，", ",", ";", "；", " feat. ", " feat ", " ft. ", " ft " })
            prepared = prepared.Replace(separator, "|", StringComparison.OrdinalIgnoreCase);
        return prepared.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).Where(token => token.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static JsonElement? GetObject(JsonElement element, string name)
    {
        if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.Object)
            return value;
        return null;
    }

    private static string ReadArtists(JsonElement element)
    {
        foreach (var name in new[] { "ar", "artists", "artist", "singers", "singer" })
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Object)
                return ReadString(value, "name", "artistName") ?? "";
            if (value.ValueKind == JsonValueKind.Array)
            {
                var names = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object ? ReadString(item, "name", "artistName") : null)
                    .Where(item => !string.IsNullOrWhiteSpace(item));
                return string.Join(" / ", names!);
            }
        }
        return ReadString(element, "artistName", "singerName") ?? "";
    }

    private static string ReadAlbum(JsonElement element)
    {
        foreach (var name in new[] { "al", "album", "albumInfo" })
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
            if (value.ValueKind == JsonValueKind.Object)
                return ReadString(value, "name", "title", "albumName") ?? "";
        }
        return ReadString(element, "albumName") ?? "";
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }
        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private static DateTime? ReadDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value))
                return ParseDate(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
        return null;
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (long.TryParse(value.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            try
            {
                return unix > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix).LocalDateTime
                    : unix > 1_000_000_000 ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var date))
            return date.LocalDateTime;
        return null;
    }

    private static bool HasProperty(JsonElement element, string name) => TryGetProperty(element, name, out _);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static List<string> ParseDelimitedLine(string line, char separator)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == separator && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        values.Add(current.ToString());
        return values;
    }

    private static int CountOutsideQuotes(string line, char value)
    {
        var count = 0;
        var quoted = false;
        foreach (var character in line)
        {
            if (character == '"')
                quoted = !quoted;
            else if (character == value && !quoted)
                count++;
        }
        return count;
    }

    private static DateTime? Later(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;
        return left.Value >= right.Value ? left : right;
    }
}
