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
    private const int PlayablePreferenceBonus = 140;
    private const int PlaylistRequestAttempts = 3;
    private readonly HttpClient _httpClient;

    public NetEasePlaylistService() : this(CreateHttpClient())
    {
    }

    public NetEasePlaylistService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 OfflineMusicLibrary/1.4.0");
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
            .Where(track => !string.IsNullOrWhiteSpace(track.Title) && !matchResult.MatchedRemoteIds.Contains(track.Id))
            .ToList();
        var declared = Math.Max(declaredTrackCount, Math.Max(trackIds.Count, remoteTracks.Count));

        DiagnosticLog.Write("NetEaseImport",
            $"歌单={playlistName}({playlistId})，声明={declared}，ID={trackIds.Count}，详情={resolvedIds.Count}，" +
            $"详情暂缺={unresolvedTrackIds.Count}，精确={matchResult.ExactCount}，模糊={matchResult.FuzzyCount}，" +
            $"修正旧云ID={matchResult.CorrectedCloudIdCount}，未匹配={missing.Count}");

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
            CorrectedCloudIdCount = matchResult.CorrectedCloudIdCount,
            UnresolvedTrackIds = unresolvedTrackIds,
            RemoteTrackIds = trackIds
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
            if (unresolved.Length == 0)
                continue;

            // A partially successful large request is commonly a gateway/query-length issue.
            // Retry only the missing IDs in shorter URLs instead of discarding the good response.
            foreach (var smallBatch in unresolved.Chunk(SmallRetryBatchSize))
            foreach (var track in await FetchTrackGroupAsync(smallBatch, attempts: 2, cancellationToken))
                result[track.Id] = track;

            unresolved = batch.Where(id => !result.ContainsKey(id)).ToArray();
            if (unresolved.Length > 0)
                DiagnosticLog.Write("NetEaseImport",
                    $"歌曲详情小批重试后仍缺少 {unresolved.Length}/{batch.Length} 首；保留歌曲 ID，等待下次导入继续补全。");
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
        var durationMs = ReadLong(track, "dt");
        if (durationMs <= 0)
            durationMs = ReadLong(track, "duration");
        return new NetEaseTrack(id, title, artists, album, durationMs);
    }

    private static MatchResult MatchTracks(IReadOnlyList<NetEaseTrack> remote, IReadOnlyList<TrackModel> local)
    {
        var uniqueLocal = local
            .Where(track => track is not null && !string.IsNullOrWhiteSpace(track.Id))
            .GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(track => track.IsEncryptedNcm).First())
            .ToList();
        var candidates = uniqueLocal.Select(LocalMatchCandidate.Create).ToList();
        var byCloudId = candidates
            .SelectMany((candidate, localIndex) =>
                candidate.Track.GetCloudIds().Select(id => new { id, localIndex }))
            .GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.localIndex).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);

        var assignments = Enumerable.Repeat(-1, remote.Count).ToArray();
        var localOwners = Enumerable.Repeat(-1, candidates.Count).ToArray();
        var exactAssignments = new bool[remote.Count];

        var exactOptions = new Dictionary<int, List<MatchOption>>();
        for (var remoteIndex = 0; remoteIndex < remote.Count; remoteIndex++)
        {
            var remoteTrack = remote[remoteIndex];
            if (string.IsNullOrWhiteSpace(remoteTrack.Id) ||
                !byCloudId.TryGetValue(remoteTrack.Id, out var knownLocalIndexes))
                continue;

            var options = knownLocalIndexes
                .Select(localIndex => new MatchOption(localIndex,
                    KnownIdMatchScore(candidates[localIndex], remoteTrack)))
                .Where(option => option.Score > 0)
                .OrderByDescending(option => option.Score)
                .ThenBy(option => option.LocalIndex)
                .ToList();
            if (options.Count > 0)
                exactOptions[remoteIndex] = options;
        }
        AssignOptions(exactOptions, assignments, localOwners, exactAssignments);
        foreach (var remoteIndex in exactOptions.Keys)
            exactAssignments[remoteIndex] = assignments[remoteIndex] >= 0;

        var fuzzyOptions = new Dictionary<int, List<MatchOption>>();
        for (var remoteIndex = 0; remoteIndex < remote.Count; remoteIndex++)
        {
            if (assignments[remoteIndex] >= 0)
                continue;

            var remoteTrack = remote[remoteIndex];
            var options = candidates
                .Select((candidate, localIndex) => new
                {
                    Candidate = candidate,
                    LocalIndex = localIndex,
                    RawScore = localOwners[localIndex] >= 0 ? 0 : MatchScore(candidate, remoteTrack)
                })
                .Where(item => item.RawScore > 0)
                .Select(item => new MatchOption(
                    item.LocalIndex,
                    item.RawScore + (item.Candidate.Track.IsEncryptedNcm ? 0 : PlayablePreferenceBonus) -
                    (item.Candidate.Track.HasCloudIds && !item.Candidate.Track.HasCloudId(remoteTrack.Id) ? 30 : 0)))
                .OrderByDescending(option => option.Score)
                .ThenBy(option => option.LocalIndex)
                .ToList();
            if (options.Count > 0)
                fuzzyOptions[remoteIndex] = options;
        }
        AssignOptions(fuzzyOptions, assignments, localOwners, exactAssignments);

        for (var remoteIndex = 0; remoteIndex < remote.Count; remoteIndex++)
        {
            var existingLocalIndex = assignments[remoteIndex];
            if (existingLocalIndex < 0 ||
                !candidates[existingLocalIndex].Track.IsEncryptedNcm ||
                string.IsNullOrWhiteSpace(remote[remoteIndex].Title))
                continue;

            var currentScore = MatchScore(candidates[existingLocalIndex], remote[remoteIndex]);
            var playable = candidates
                .Select((candidate, localIndex) => new MatchOption(
                    localIndex,
                    localOwners[localIndex] < 0 && !candidate.Track.IsEncryptedNcm
                        ? MatchScore(candidate, remote[remoteIndex])
                        : 0))
                .Where(option => option.Score > 0 && option.Score >= currentScore - PlayablePreferenceBonus)
                .OrderByDescending(option => option.Score)
                .ThenBy(option => option.LocalIndex)
                .FirstOrDefault();
            if (playable is null)
                continue;

            localOwners[existingLocalIndex] = -1;
            assignments[remoteIndex] = playable.LocalIndex;
            localOwners[playable.LocalIndex] = remoteIndex;
            exactAssignments[remoteIndex] = false;
        }

        var tracks = new List<TrackModel>();
        var matchedRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exactCount = 0;
        var fuzzyCount = 0;
        var correctedCloudIdCount = 0;
        for (var remoteIndex = 0; remoteIndex < assignments.Length; remoteIndex++)
        {
            var localIndex = assignments[remoteIndex];
            if (localIndex < 0)
                continue;

            var match = candidates[localIndex].Track;
            if (!string.IsNullOrWhiteSpace(remote[remoteIndex].Title))
            {
                foreach (var staleOwner in local.Where(track =>
                             !ReferenceEquals(track, match) && track.HasCloudId(remote[remoteIndex].Id)))
                {
                    if (staleOwner.ForgetCloudId(remote[remoteIndex].Id))
                        correctedCloudIdCount++;
                }
            }
            match.RememberCloudId(remote[remoteIndex].Id);
            tracks.Add(match);
            matchedRemoteIds.Add(remote[remoteIndex].Id);
            if (exactAssignments[remoteIndex])
                exactCount++;
            else
                fuzzyCount++;
        }

        return new MatchResult(tracks, matchedRemoteIds, exactCount, fuzzyCount, correctedCloudIdCount);
    }

    private static int KnownIdMatchScore(LocalMatchCandidate candidate, NetEaseTrack remoteTrack)
    {
        if (string.IsNullOrWhiteSpace(remoteTrack.Title))
            return 1_000_000 + (candidate.Track.IsEncryptedNcm ? 0 : PlayablePreferenceBonus);
        if (candidate.IsInstrumental != HasInstrumentalMarker([remoteTrack.Title]))
            return 0;
        var score = Math.Max(1, MatchScore(candidate, remoteTrack));
        return 1_000_000 + score + (candidate.Track.IsEncryptedNcm ? 0 : PlayablePreferenceBonus);
    }

    private static void AssignOptions(
        IReadOnlyDictionary<int, List<MatchOption>> optionsByRemote,
        int[] assignments,
        int[] localOwners,
        bool[] lockedRemoteAssignments)
    {
        foreach (var remoteIndex in optionsByRemote
                     .OrderBy(pair => pair.Value.Count)
                     .ThenByDescending(pair => pair.Value.Count == 0 ? 0 : pair.Value[0].Score)
                     .Select(pair => pair.Key))
        {
            var visitedLocal = new bool[localOwners.Length];
            TryAssign(remoteIndex, optionsByRemote, assignments, localOwners,
                lockedRemoteAssignments, visitedLocal, []);
        }
    }

    private static bool TryAssign(
        int remoteIndex,
        IReadOnlyDictionary<int, List<MatchOption>> optionsByRemote,
        int[] assignments,
        int[] localOwners,
        bool[] lockedRemoteAssignments,
        bool[] visitedLocal,
        HashSet<int> visitingRemote)
    {
        if (!visitingRemote.Add(remoteIndex) ||
            !optionsByRemote.TryGetValue(remoteIndex, out var options))
            return false;

        foreach (var option in options)
        {
            if (visitedLocal[option.LocalIndex])
                continue;
            visitedLocal[option.LocalIndex] = true;
            var owner = localOwners[option.LocalIndex];
            if (owner >= 0 &&
                (lockedRemoteAssignments[owner] ||
                 !TryAssign(owner, optionsByRemote, assignments, localOwners,
                     lockedRemoteAssignments, visitedLocal, visitingRemote)))
                continue;

            var oldLocalIndex = assignments[remoteIndex];
            assignments[remoteIndex] = option.LocalIndex;
            localOwners[option.LocalIndex] = remoteIndex;
            if (oldLocalIndex >= 0 && oldLocalIndex != option.LocalIndex &&
                localOwners[oldLocalIndex] == remoteIndex)
                localOwners[oldLocalIndex] = -1;
            visitingRemote.Remove(remoteIndex);
            return true;
        }

        visitingRemote.Remove(remoteIndex);
        return false;
    }

    private sealed record MatchResult(
        IReadOnlyList<TrackModel> Tracks,
        HashSet<string> MatchedRemoteIds,
        int ExactCount,
        int FuzzyCount,
        int CorrectedCloudIdCount);

    private sealed record MatchOption(int LocalIndex, int Score);

    private sealed class LocalMatchCandidate
    {
        private LocalMatchCandidate(TrackModel track, IReadOnlyList<string> titleVariants, bool isInstrumental)
        {
            Track = track;
            TitleVariants = titleVariants;
            Artist = $"{track.Artist} / {track.AlbumArtist}";
            Album = track.Album;
            IsInstrumental = isInstrumental;
        }

        public TrackModel Track { get; }
        public IReadOnlyList<string> TitleVariants { get; }
        public string Artist { get; }
        public string Album { get; }
        public bool IsInstrumental { get; }
        public long DurationMs => Track.DurationMs;

        public static LocalMatchCandidate Create(TrackModel track)
        {
            string?[] values = [track.Title, Path.GetFileNameWithoutExtension(track.FilePath)];
            return new LocalMatchCandidate(track, BuildTitleVariants(values), HasInstrumentalMarker(values));
        }
    }

    private static int MatchScore(LocalMatchCandidate candidate, NetEaseTrack remoteTrack)
    {
        if (candidate.IsInstrumental != HasInstrumentalMarker([remoteTrack.Title]))
            return 0;

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

        var durationScore = DurationScore(candidate.DurationMs, remoteTrack.DurationMs);
        return titleScore * 10 + artistScore * 6 + albumScore * 3 + durationScore * 4;
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
                InstrumentalMarkerRegex().Replace(value!, ""),
                FeaturedArtistSuffixRegex().Replace(value!, "")
            };

            foreach (var form in forms.ToArray())
            {
                forms.Add(TitleNoiseWordsRegex().Replace(BracketTextRegex().Replace(form, ""), ""));
                forms.Add(InstrumentalMarkerRegex().Replace(form, ""));
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

    private static int DurationScore(long localDurationMs, long remoteDurationMs)
    {
        if (localDurationMs <= 0 || remoteDurationMs <= 0)
            return 0;

        var difference = Math.Abs(localDurationMs - remoteDurationMs);
        var closeTolerance = Math.Max(2_000L, Math.Min(localDurationMs, remoteDurationMs) / 100L);
        if (difference <= closeTolerance)
            return 10;
        if (difference <= 5_000L)
            return 6;
        return difference <= 10_000L ? 2 : 0;
    }

    private static bool HasInstrumentalMarker(IEnumerable<string?> values) =>
        values.Any(value => !string.IsNullOrWhiteSpace(value) && InstrumentalMarkerRegex().IsMatch(value));

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

    private static long ReadLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
            return value;
        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value) ? value : 0;
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

    [GeneratedRegex(@"\s+(?:feat(?:uring)?\.?|ft\.?|with|vo\.?)\s*.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FeaturedArtistSuffixRegex();

    [GeneratedRegex(@"[,，、/＆&;；|]|\s+(?:and|x|with|feat\.?|ft\.?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ArtistSeparatorRegex();

    [GeneratedRegex(@"(?i)\b(?:official|music|video|lyrics?|audio|remaster(?:ed)?|remix|version|live|mv|hd|hq|cover|explicit|instrumental)\b|伴奏|纯音乐|现场|高清|无损|歌词|完整版|版本|版")]
    private static partial Regex TitleNoiseWordsRegex();

    [GeneratedRegex(@"(?ix)(?:\boff[\s._-]*(?:vocals?|vo)\b|\bvocals?[\s._-]*less\b|\binstrumental(?:\s+(?:mix|version|ver\.?))?\b|\binst(?:\.|rumental)?\b|\bkaraoke\b|\baccompaniment\b|\bbacking[\s._-]*track\b|\bminus[\s._-]*(?:one|vocals?)\b|\b(?:without|no)[\s._-]*(?:lead[\s._-]*)?(?:voice|vocals?)\b|伴奏(?:版)?|纯音乐|純音樂|純音楽|无人声|無人聲|无主唱|無主唱|去人声|去人聲|オフ[\s・._-]*(?:ボ|ヴォ)ーカル|(?:ボ|ヴォ)ーカル[\s・._-]*(?:なし|無し)|歌(?:なし|無し)|インスト(?:ゥルメンタル)?|カラオケ)", RegexOptions.Compiled)]
    private static partial Regex InstrumentalMarkerRegex();

    private static readonly HashSet<char> MeaningfulTitleSymbols = new()
    {
        '△', '▽', '▲', '▼', '○', '●', '◎', '◇', '◆', '□', '■', '☆', '★',
        '∞', '∴', '∵', '※', '＊', '♪', '♫', '♬', '♭', '♯', '＋', '−', '×',
        '÷', '＝', '≠', '≈', '≡', 'Ⅰ', 'Ⅱ', 'Ⅲ', 'Ⅳ', 'Ⅴ', 'Ⅵ', 'Ⅶ', 'Ⅷ',
        'Ⅸ', 'Ⅹ', 'Ⅺ', 'Ⅻ'
    };
}
