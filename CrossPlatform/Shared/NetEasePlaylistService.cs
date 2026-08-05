using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OfflineMusicLibrary;

public sealed partial class NetEasePlaylistService
{
    private const int TrackDetailBatchSize = 100;
    private const int SmallRetryBatchSize = 25;
    private const int PlaylistRequestAttempts = 3;
    private readonly HttpClient _httpClient;

    public NetEasePlaylistService() : this(CreateHttpClient())
    {
    }

    public NetEasePlaylistService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 OfflineMusicLibrary/1.2");
        _httpClient.DefaultRequestHeaders.Referrer ??= new Uri("https://music.163.com/");
    }

    public async Task<NetEaseImportResult> ImportAsync(
        string source,
        IReadOnlyList<TrackModel> localTracks,
        CancellationToken cancellationToken = default)
    {
        var playlistId = ExtractPlaylistId(source) ?? throw new InvalidOperationException("无法识别网易云歌单 ID。");
        using var document = await FetchJsonWithRetriesAsync(
            $"https://music.163.com/api/v6/playlist/detail?id={playlistId}&n=10000&s=0",
            cancellationToken);
        if (!document.RootElement.TryGetProperty("playlist", out var playlist) || playlist.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("网易云未返回可访问的公开歌单，请确认链接及歌单权限。");

        var playlistName = GetString(playlist, "name") ?? $"网易云歌单 {playlistId}";
        var declaredTrackCount = ReadInt(playlist, "trackCount");
        var trackIds = ReadTrackIds(playlist);
        var embeddedTracks = ReadTracks(playlist);
        var embeddedIds = embeddedTracks
            .Select(track => track.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingDetailIds = trackIds.Where(id => !embeddedIds.Contains(id)).ToList();
        var fetchedTracks = missingDetailIds.Count == 0
            ? []
            : await FetchTracksByIdsAsync(missingDetailIds, cancellationToken);
        var remoteTracks = MergeTracks(trackIds, embeddedTracks, fetchedTracks);
        var resolvedIds = embeddedTracks.Concat(fetchedTracks)
            .Select(track => track.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unresolvedTrackIds = trackIds.Where(id => !resolvedIds.Contains(id)).ToList();

        var matchResult = await Task.Run(() => MatchTracks(remoteTracks, localTracks), cancellationToken);
        var missing = remoteTracks
            .Where(track => !matchResult.MatchedRemoteIds.Contains(track.Id))
            .ToList();
        var declared = Math.Max(declaredTrackCount, Math.Max(trackIds.Count, remoteTracks.Count));

        DiagnosticLog.Write("NetEaseImport",
            $"歌单={playlistName}({playlistId})，声明={declared}，ID={trackIds.Count}，详情={resolvedIds.Count}，" +
            $"精确={matchResult.ExactCount}，模糊={matchResult.FuzzyCount}，未匹配={missing.Count}");

        return new NetEaseImportResult(
            playlistName,
            playlistId,
            declared,
            remoteTracks,
            matchResult.Tracks,
            missing)
        {
            TrackIdCount = trackIds.Count,
            ResolvedTrackCount = resolvedIds.Count,
            ExactMatchCount = matchResult.ExactCount,
            FuzzyMatchCount = matchResult.FuzzyCount,
            UnresolvedTrackIds = unresolvedTrackIds
        };
    }

    public static string? ExtractPlaylistId(string source)
    {
        var trimmed = source.Trim();
        if (trimmed.All(char.IsDigit) && trimmed.Length > 0)
            return trimmed;

        var match = PlaylistIdRegex().Match(trimmed);
        if (match.Success)
            return match.Groups[1].Value;

        match = StandaloneLongNumberRegex().Match(trimmed);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private async Task<JsonDocument> FetchJsonWithRetriesAsync(string url, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= PlaylistRequestAttempts; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;
                DiagnosticLog.Write("NetEaseImport", $"歌单请求失败（第 {attempt} 次）：{url}", exception);
                if (attempt < PlaylistRequestAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("网易云歌单请求连续失败，请稍后重试。", lastException);
    }

    private static List<string> ReadTrackIds(JsonElement playlist)
    {
        if (!playlist.TryGetProperty("trackIds", out var trackIds) || trackIds.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in trackIds.EnumerateArray())
        {
            var id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idElement)
                ? idElement.ToString()
                : item.ToString();
            if (!string.IsNullOrWhiteSpace(id) && known.Add(id))
                result.Add(id);
        }
        return result;
    }

    private async Task<List<NetEaseTrack>> FetchTracksByIdsAsync(
        IReadOnlyList<string> trackIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, NetEaseTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in trackIds.Chunk(TrackDetailBatchSize))
        {
            var fetched = await FetchTrackGroupAsync(batch, attempts: 3, cancellationToken);
            foreach (var track in fetched)
                result[track.Id] = track;

            var unresolved = batch.Where(id => !result.ContainsKey(id)).ToArray();
            if (fetched.Count == 0 || unresolved.Length == 0)
                continue;

            // A partially successful large request is commonly a gateway/query-length issue.
            // Retry only the missing IDs in shorter URLs instead of discarding the good response.
            foreach (var smallBatch in unresolved.Chunk(SmallRetryBatchSize))
            foreach (var track in await FetchTrackGroupAsync(smallBatch, attempts: 2, cancellationToken))
                result[track.Id] = track;
        }

        return trackIds.Where(result.ContainsKey).Select(id => result[id]).ToList();
    }

    private async Task<List<NetEaseTrack>> FetchTrackGroupAsync(
        IReadOnlyList<string> trackIds,
        int attempts,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, NetEaseTrack>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var missing = trackIds.Where(id => !result.ContainsKey(id)).ToArray();
            if (missing.Length == 0)
                break;

            var ids = Uri.EscapeDataString($"[{string.Join(",", missing)}]");
            AddTracks(result, await FetchTrackBatchOnceAsync(
                $"https://music.163.com/api/song/detail?ids={ids}", cancellationToken));

            missing = trackIds.Where(id => !result.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                var payload = Uri.EscapeDataString(
                    $"[{string.Join(",", missing.Select(id => $"{{\"id\":{id},\"v\":0}}"))}]");
                AddTracks(result, await FetchTrackBatchOnceAsync(
                    $"https://music.163.com/api/v3/song/detail?c={payload}", cancellationToken));
            }

            if (result.Count < trackIds.Count && attempt < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
        }

        return trackIds.Where(result.ContainsKey).Select(id => result[id]).ToList();
    }

    private static void AddTracks(IDictionary<string, NetEaseTrack> destination, IEnumerable<NetEaseTrack> tracks)
    {
        foreach (var track in tracks)
            if (!string.IsNullOrWhiteSpace(track.Id))
                destination[track.Id] = track;
    }

    private async Task<List<NetEaseTrack>> FetchTrackBatchOnceAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return [];

            return songs.EnumerateArray().Select(ReadTrack).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("NetEaseImport", $"歌曲详情请求失败：{url}", exception);
            return [];
        }
    }

    private static List<NetEaseTrack> MergeTracks(
        IReadOnlyList<string> trackIds,
        IReadOnlyList<NetEaseTrack> embeddedTracks,
        IReadOnlyList<NetEaseTrack> fetchedTracks)
    {
        if (trackIds.Count == 0)
            return embeddedTracks.ToList();

        var byId = embeddedTracks.Concat(fetchedTracks)
            .Where(track => !string.IsNullOrWhiteSpace(track.Id))
            .GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return trackIds
            .Select(id => byId.TryGetValue(id, out var track)
                ? track
                : new NetEaseTrack(id, "", "", ""))
            .ToList();
    }

    private static List<NetEaseTrack> ReadTracks(JsonElement playlist)
    {
        if (!playlist.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return [];

        return tracks.EnumerateArray().Select(ReadTrack).ToList();
    }

    private static NetEaseTrack ReadTrack(JsonElement track)
    {
        var id = track.TryGetProperty("id", out var idElement) ? idElement.ToString() : "";
        var title = GetString(track, "name") ?? "未知歌曲";
        var artists = ReadArtistNames(track);
        var album = ReadAlbumName(track);
        return new NetEaseTrack(id, title, artists, album);
    }

    private static MatchResult MatchTracks(IReadOnlyList<NetEaseTrack> remote, IReadOnlyList<TrackModel> local)
    {
        var candidates = local.Select(LocalMatchCandidate.Create).ToList();
        var byCloudId = local
            .SelectMany(track => track.GetCloudIds().Select(id => new { Id = id, Track = track }))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Track).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);
        var assignments = new TrackModel?[remote.Count];
        var exactAssignments = new bool[remote.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Reserve every trustworthy ID hit before fuzzy matching. Otherwise an early fuzzy
        // candidate can steal the file required by a later exact playlist entry.
        for (var index = 0; index < remote.Count; index++)
        {
            var remoteTrack = remote[index];
            if (!byCloudId.TryGetValue(remoteTrack.Id, out var idMatches))
                continue;

            var exact = idMatches
                .Where(track => !used.Contains(track.Id))
                .Select(track => new
                {
                    Track = track,
                    Score = string.IsNullOrWhiteSpace(remoteTrack.Title)
                        ? 1
                        : MatchScore(LocalMatchCandidate.Create(track), remoteTrack)
                })
                .Where(item => item.Score > 0)
                .OrderBy(item => item.Track.IsEncryptedNcm)
                .ThenByDescending(item => item.Score)
                .Select(item => item.Track)
                .FirstOrDefault();
            if (exact is null)
                continue;

            assignments[index] = exact;
            exactAssignments[index] = true;
            used.Add(exact.Id);
        }

        for (var index = 0; index < remote.Count; index++)
        {
            var existing = assignments[index];
            if (existing is not null && !existing.IsEncryptedNcm)
                continue;

            var ranked = candidates
                .Where(candidate => !used.Contains(candidate.Track.Id))
                .Select(candidate => new
                {
                    candidate.Track,
                    Score = MatchScore(candidate, remote[index])
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ToList();
            if (ranked.Count == 0)
                continue;

            var best = ranked[0];
            var playable = ranked.FirstOrDefault(item =>
                !item.Track.IsEncryptedNcm && item.Score >= best.Score - 140);
            var fuzzy = playable?.Track ?? best.Track;
            if (existing is not null && fuzzy.IsEncryptedNcm)
                continue;

            if (existing is not null)
                used.Remove(existing.Id);
            assignments[index] = fuzzy;
            exactAssignments[index] = false;
            used.Add(fuzzy.Id);
        }

        var tracks = new List<TrackModel>();
        var matchedRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactCount = 0;
        var fuzzyCount = 0;
        for (var index = 0; index < assignments.Length; index++)
        {
            var match = assignments[index];
            if (match is null)
                continue;

            match.RememberCloudId(remote[index].Id);
            tracks.Add(match);
            matchedRemoteIds.Add(remote[index].Id);
            if (exactAssignments[index])
                exactCount++;
            else
                fuzzyCount++;
        }

        return new MatchResult(tracks, matchedRemoteIds, exactCount, fuzzyCount);
    }

    private sealed record MatchResult(
        IReadOnlyList<TrackModel> Tracks,
        HashSet<string> MatchedRemoteIds,
        int ExactCount,
        int FuzzyCount);

    private sealed class LocalMatchCandidate
    {
        private LocalMatchCandidate(TrackModel track, IReadOnlyList<string> titleVariants)
        {
            Track = track;
            TitleVariants = titleVariants;
            Artist = $"{track.Artist} / {track.AlbumArtist}";
            Album = track.Album;
        }

        public TrackModel Track { get; }
        public IReadOnlyList<string> TitleVariants { get; }
        public string Artist { get; }
        public string Album { get; }

        public static LocalMatchCandidate Create(TrackModel track)
        {
            string?[] values = [track.Title, Path.GetFileNameWithoutExtension(track.FilePath)];
            return new LocalMatchCandidate(track, BuildTitleVariants(values));
        }
    }

    private static int MatchScore(LocalMatchCandidate candidate, NetEaseTrack remoteTrack)
    {
        var remoteTitles = BuildTitleVariants(new string?[] { remoteTrack.Title });
        var titleScore = candidate.TitleVariants
            .SelectMany(localTitle => remoteTitles.Select(remoteTitle => TitleSimilarity(localTitle, remoteTitle)))
            .DefaultIfEmpty(0)
            .Max();
        if (titleScore < 68)
            return 0;

        var artistScore = ArtistScore(candidate.Artist, remoteTrack.Artist);
        var albumScore = AlbumScore(candidate.Album, remoteTrack.Album);
        if (titleScore < 88 && artistScore == 0 && albumScore == 0)
            return 0;

        return titleScore * 10 + artistScore * 6 + albumScore * 3;
    }

    private static List<string> BuildTitleVariants(IEnumerable<string?> values)
    {
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                value!,
                LeadingTrackNumberRegex().Replace(value!, ""),
                BracketTextRegex().Replace(value!, ""),
                TitleNoiseWordsRegex().Replace(value!, ""),
                FeaturedArtistSuffixRegex().Replace(value!, "")
            };

            foreach (var form in forms.ToArray())
            {
                forms.Add(TitleNoiseWordsRegex().Replace(BracketTextRegex().Replace(form, ""), ""));
                forms.Add(FeaturedArtistSuffixRegex().Replace(form, ""));
            }

            foreach (var form in forms)
            {
                AddTitleVariant(variants, form);
                // Keep the leading title segment, but do not treat later slash/dash metadata
                // (original song, vocalist, artist, etc.) as an independent song title.
                // Doing so can falsely merge two arrangements that merely share the same source tune.
                var separated = TitleSeparatorRegex().Split(form);
                if (separated.Length > 1)
                {
                    AddTitleVariant(variants, separated[0]);
                    AddTitleVariant(variants, FeaturedArtistSuffixRegex().Replace(separated[0], ""));
                }

                separated = SlashTitleSeparatorRegex().Split(form);
                if (separated.Length > 1)
                {
                    AddTitleVariant(variants, separated[0]);
                    AddTitleVariant(variants, FeaturedArtistSuffixRegex().Replace(separated[0], ""));
                }
            }
        }
        return variants.Where(variant => variant.Length > 0).ToList();
    }

    private static void AddTitleVariant(HashSet<string> variants, string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length > 0)
            variants.Add(normalized);

        var loose = NormalizeLoose(value);
        if (loose.Length > 0)
            variants.Add(loose);
    }

    private static int TitleSimilarity(string local, string remote)
    {
        if (local.Length == 0 || remote.Length == 0)
            return 0;
        if (string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
            return 100;

        var shorter = Math.Min(local.Length, remote.Length);
        var longer = Math.Max(local.Length, remote.Length);
        var contains = local.Contains(remote, StringComparison.OrdinalIgnoreCase) ||
                       remote.Contains(local, StringComparison.OrdinalIgnoreCase);
        var containsCjk = ContainsCjk(local) || ContainsCjk(remote);
        if (contains && (shorter >= 4 || containsCjk && shorter >= 2))
        {
            var ratio = shorter / (double)longer;
            var minimumRatio = containsCjk ? 0.3 : 0.52;
            if (ratio >= minimumRatio)
                return (int)Math.Round(74 + ratio * 20);
        }

        if (longer < 5)
            return 0;

        var similarity = SimilarityPercent(local, remote);
        return similarity >= 74 ? similarity : 0;
    }

    private static int ArtistScore(string local, string remote)
    {
        var localNames = SplitArtistNames(local).ToArray();
        var remoteNames = SplitArtistNames(remote).ToArray();
        if (localNames.Length == 0 || remoteNames.Length == 0)
            return 0;

        foreach (var left in localNames)
        foreach (var right in remoteNames)
        {
            if (left == right)
                return 18;
            if (left.Length >= 2 && right.Length >= 2 && (left.Contains(right) || right.Contains(left)))
                return 14;
        }
        return 0;
    }

    private static int AlbumScore(string local, string remote)
    {
        var left = Normalize(local);
        var right = Normalize(remote);
        if (left.Length == 0 || right.Length == 0)
            return 0;
        if (left == right)
            return 8;
        return Math.Min(left.Length, right.Length) >= 4 && (left.Contains(right) || right.Contains(left)) ? 4 : 0;
    }

    private static IEnumerable<string> SplitArtistNames(string value) =>
        ArtistSeparatorRegex().Split(value)
            .Select(Normalize)
            .Where(name => name.Length > 0 && name != Normalize("未知艺术家"));

    private static int SimilarityPercent(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return 0;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var cost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        var distance = previous[right.Length];
        var maxLength = Math.Max(left.Length, right.Length);
        return (int)Math.Round((1 - distance / (double)maxLength) * 100);
    }

    private static bool ContainsCjk(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9FFF');

    private static string Normalize(string value) => NormalizeCore(value, preserveSymbols: true);

    private static string NormalizeLoose(string value) => NormalizeCore(value, preserveSymbols: false);

    private static string NormalizeCore(string value, bool preserveSymbols)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character) ||
                CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber)
            {
                builder.Append(character);
                continue;
            }

            if (preserveSymbols && IsMeaningfulSymbol(character))
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool IsMeaningfulSymbol(char character)
    {
        if (MeaningfulTitleSymbols.Contains(character))
            return true;

        return CharUnicodeInfo.GetUnicodeCategory(character) is
            UnicodeCategory.MathSymbol or
            UnicodeCategory.OtherSymbol or
            UnicodeCategory.ModifierSymbol;
    }

    private static string ReadArtistNames(JsonElement track)
    {
        foreach (var propertyName in new[] { "ar", "artists" })
        {
            if (!track.TryGetProperty(propertyName, out var artists) || artists.ValueKind != JsonValueKind.Array)
                continue;
            return string.Join(" / ", artists.EnumerateArray()
                .Select(artist => GetString(artist, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)));
        }
        return "未知艺术家";
    }

    private static string ReadAlbumName(JsonElement track)
    {
        foreach (var propertyName in new[] { "al", "album" })
            if (track.TryGetProperty(propertyName, out var album) && album.ValueKind == JsonValueKind.Object)
                return GetString(album, "name") ?? "未知专辑";
        return "未知专辑";
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value) ? value : 0;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    [GeneratedRegex(@"(?:playlist(?:\?id=|/)|[?&#]id=)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@"(?<!\d)(\d{5,})(?!\d)", RegexOptions.Compiled)]
    private static partial Regex StandaloneLongNumberRegex();

    [GeneratedRegex(@"[\(（\[【].*?[\)）\]】]", RegexOptions.Compiled)]
    private static partial Regex BracketTextRegex();

    [GeneratedRegex(@"^\s*\d{1,3}\s*[\.\-_、 ]+\s*", RegexOptions.Compiled)]
    private static partial Regex LeadingTrackNumberRegex();

    [GeneratedRegex(@"\s*[-–—－_·•|]\s*", RegexOptions.Compiled)]
    private static partial Regex TitleSeparatorRegex();

    [GeneratedRegex(@"\s+(?:/|／|\||｜)\s+", RegexOptions.Compiled)]
    private static partial Regex SlashTitleSeparatorRegex();

    [GeneratedRegex(@"\s+(?:feat(?:uring)?\.?|ft\.?|with|vo\.?)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FeaturedArtistSuffixRegex();

    [GeneratedRegex(@"[,，、/＆&;；|]|\s+(?:and|x|with|feat\.?|ft\.?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ArtistSeparatorRegex();

    [GeneratedRegex(@"(?i)\b(?:official|music|video|lyrics?|audio|remaster(?:ed)?|remix|version|live|mv|hd|hq|cover|explicit|instrumental)\b|伴奏|纯音乐|现场|高清|无损|歌词|完整版|版本|版")]
    private static partial Regex TitleNoiseWordsRegex();

    private static readonly HashSet<char> MeaningfulTitleSymbols = new()
    {
        '△', '▽', '▲', '▼', '○', '●', '◎', '◇', '◆', '□', '■', '☆', '★',
        '∞', '∴', '∵', '※', '＊', '♪', '♫', '♬', '♭', '♯', '＋', '−', '×',
        '÷', '＝', '≠', '≈', '≡', 'Ⅰ', 'Ⅱ', 'Ⅲ', 'Ⅳ', 'Ⅴ', 'Ⅵ', 'Ⅶ', 'Ⅷ',
        'Ⅸ', 'Ⅹ', 'Ⅺ', 'Ⅻ'
    };
}
