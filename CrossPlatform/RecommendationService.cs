namespace OfflineMusicLibrary;

public enum RecommendationPreset
{
    RediscoverFavorites,
    UnplayedGems,
    FavoriteExpansion,
    ThirtyMinuteRadio
}

public sealed class RecommendationResult
{
    public required RecommendationPreset Preset { get; init; }
    public required string Title { get; init; }
    public required string Insight { get; init; }
    public required IReadOnlyList<TrackModel> Tracks { get; init; }
    public required IReadOnlyDictionary<string, string> Reasons { get; init; }
    public long EstimatedDurationMs => Tracks.Sum(RecommendationService.EffectiveDurationMs);
    public string ReasonFor(TrackModel track) =>
        Reasons.GetValueOrDefault(track.Id, "来自你的本地聆听画像");
}

public static class RecommendationService
{
    public const int DefaultTrackCount = 30;
    private const long TargetRadioMs = 30 * 60 * 1000;
    private const long UnknownDurationMs = 3 * 60 * 1000 + 30 * 1000;

    public static RecommendationResult Create(
        IEnumerable<TrackModel> library,
        RecommendationPreset preset,
        DateTime now,
        int refreshSalt = 0,
        int count = DefaultTrackCount,
        IEnumerable<string>? implicitFavoriteTrackIds = null)
    {
        var tracks = library
            .Where(track => track is not null && !track.IsEncryptedNcm)
            .DistinctBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var likedIds = tracks.Where(track => track.IsFavorite).Select(track => track.Id)
            .Concat(implicitFavoriteTrackIds ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profile = TasteProfile.Build(tracks, likedIds);
        var random = new Random(ComputeSeed(tracks, preset, now, refreshSalt));
        var take = Math.Clamp(count, 1, Math.Max(1, tracks.Count));

        IReadOnlyList<TrackModel> selected = preset switch
        {
            RecommendationPreset.RediscoverFavorites => Rediscover(tracks, likedIds, now, random, take),
            RecommendationPreset.UnplayedGems => Unplayed(tracks, profile, likedIds, now, random, take),
            RecommendationPreset.FavoriteExpansion => Expand(tracks, profile, likedIds, now, random, take),
            RecommendationPreset.ThirtyMinuteRadio => Radio(tracks, profile, likedIds, now, random),
            _ => []
        };
        var reasons = selected.ToDictionary(
            track => track.Id,
            track => Explain(track, preset, profile, likedIds, now),
            StringComparer.OrdinalIgnoreCase);

        return new RecommendationResult
        {
            Preset = preset,
            Title = PresetTitle(preset),
            Insight = BuildInsight(preset, selected, likedIds),
            Tracks = selected,
            Reasons = reasons
        };
    }

    public static string DescribeTaste(
        IEnumerable<TrackModel> library,
        IEnumerable<string>? implicitFavoriteTrackIds = null)
    {
        var tracks = library.ToList();
        var likedIds = tracks.Where(track => track.IsFavorite).Select(track => track.Id)
            .Concat(implicitFavoriteTrackIds ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var likes = tracks.Count(track => likedIds.Contains(track.Id));
        var plays = tracks.Sum(track => (long)Math.Max(0, track.PlayCount));
        return likes == 0 && plays == 0
            ? $"先从 {tracks.Count:N0} 首本地歌曲开始认识你的口味"
            : $"已从 {plays:N0} 次播放与 {likes:N0} 首红心或“喜欢的音乐”歌曲中学习；陌生推荐必须给得出理由";
    }

    internal static long EffectiveDurationMs(TrackModel track) =>
        track.DurationMs > 0 ? track.DurationMs : UnknownDurationMs;

    private static IReadOnlyList<TrackModel> Rediscover(
        IReadOnlyList<TrackModel> tracks,
        IReadOnlySet<string> likedIds,
        DateTime now,
        Random random,
        int count)
    {
        var anchors = tracks.Where(track => likedIds.Contains(track.Id)).ToList();
        if (anchors.Count == 0)
            anchors = tracks.Where(track => track.PlayCount > 0).ToList();
        return Diversify(Rank(anchors.Select(track =>
        {
            var days = track.LastPlayedAt.HasValue
                ? Math.Max(0, (now - track.LastPlayedAt.Value).TotalDays)
                : 240;
            var stale = 0.4 + Math.Min(3.0, days / 60.0);
            var oldEnough = !track.LastPlayedAt.HasValue || days >= 30 ? 2.0 : 0.3;
            var history = 1.0 + Math.Log2(1.0 + Math.Max(0, track.PlayCount)) * 0.25;
            return (track, stale * oldEnough * history);
        }), random), count, 3, 2);
    }

    private static IReadOnlyList<TrackModel> Unplayed(
        IReadOnlyList<TrackModel> tracks,
        TasteProfile profile,
        IReadOnlySet<string> likedIds,
        DateTime now,
        Random random,
        int count)
    {
        if (!profile.HasSignals)
            return [];
        var candidates = tracks
            .Where(IsStrictlyUnplayed)
            .Where(track => likedIds.Contains(track.Id) || profile.Confidence(track) >= 0.36)
            .Select(track =>
            {
                var age = Math.Clamp((now - track.AddedAt).TotalDays / 120.0, 0.25, 1.4);
                var liked = likedIds.Contains(track.Id) ? 4.0 : 1.0;
                return (track, Math.Max(0.05, (0.8 + profile.Confidence(track) * 7.5) * liked * age));
            });
        return Diversify(Rank(candidates, random), count, 2, 2);
    }

    private static IReadOnlyList<TrackModel> Expand(
        IReadOnlyList<TrackModel> tracks,
        TasteProfile profile,
        IReadOnlySet<string> likedIds,
        DateTime now,
        Random random,
        int count)
    {
        if (!profile.HasSignals)
            return [];
        var candidates = tracks
            .Where(track => !likedIds.Contains(track.Id))
            .Where(track => profile.Confidence(track) >= 0.38)
            .Select(track =>
            {
                var discovery = track.PlayCount == 0 ? 1.65 : 1.0 / (1.0 + track.PlayCount * 0.08);
                var recent = RecentPenalty(track, now, 7.0);
                return (track, Math.Max(0.05, (0.5 + profile.Confidence(track) * 8.0) * discovery * recent));
            });
        return Diversify(Rank(candidates, random), count, 2, 2);
    }

    private static IReadOnlyList<TrackModel> Radio(
        IReadOnlyList<TrackModel> tracks,
        TasteProfile profile,
        IReadOnlySet<string> likedIds,
        DateTime now,
        Random random)
    {
        var familiar = tracks.Where(track => likedIds.Contains(track.Id) || track.PlayCount > 0).ToList();
        if (familiar.Count == 0)
            return [];
        var familiarRanked = Diversify(Rank(familiar.Select(track =>
        {
            var affection = likedIds.Contains(track.Id)
                ? 3.2
                : 1.0 + Math.Log2(1.0 + track.PlayCount) * 0.5;
            return (track, affection * (1.0 + profile.Confidence(track) * 2.0) * RecentPenalty(track, now, 3.0));
        }), random), familiar.Count, 2, 2);
        var discoveries = Expand(tracks, profile, likedIds, now, random, DefaultTrackCount)
            .Where(IsStrictlyUnplayed)
            .ToList();

        var familiarQueue = new Queue<TrackModel>(familiarRanked);
        var discoveryQueue = new Queue<TrackModel>(discoveries);
        var route = new List<TrackModel>();
        while (familiarQueue.Count > 0)
        {
            for (var index = 0; index < 4 && familiarQueue.Count > 0; index++)
                route.Add(familiarQueue.Dequeue());
            if (discoveryQueue.Count > 0)
                route.Add(discoveryQueue.Dequeue());
        }

        var selected = new List<TrackModel>();
        long duration = 0;
        foreach (var track in route)
        {
            var trackDuration = EffectiveDurationMs(track);
            var currentDistance = Math.Abs(TargetRadioMs - duration);
            var nextDistance = Math.Abs(TargetRadioMs - duration - trackDuration);
            if (duration < TargetRadioMs - 3 * 60 * 1000 || nextDistance < currentDistance)
            {
                selected.Add(track);
                duration += trackDuration;
            }
            if (duration >= TargetRadioMs - 60 * 1000 || selected.Count >= 40)
                break;
        }
        return selected;
    }

    private static IEnumerable<TrackModel> Rank(
        IEnumerable<(TrackModel Track, double Weight)> weighted,
        Random random) =>
        weighted
            .Select(item => (item.Track,
                Key: -Math.Log(Math.Max(double.Epsilon, random.NextDouble())) / Math.Max(0.001, item.Weight)))
            .OrderBy(item => item.Key)
            .Select(item => item.Track);

    private static IReadOnlyList<TrackModel> Diversify(
        IEnumerable<TrackModel> ranked,
        int count,
        int maxPerArtist,
        int maxPerAlbum)
    {
        var source = ranked.ToList();
        var result = new List<TrackModel>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var artists = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var albums = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        for (var relaxation = 0; result.Count < count && relaxation < 5; relaxation++)
        {
            foreach (var track in source)
            {
                if (result.Count >= count || ids.Contains(track.Id))
                    continue;
                var artist = Key(track.Artist, track.Id);
                var album = Key(track.Album, track.Id);
                if (artists.GetValueOrDefault(artist) >= maxPerArtist + relaxation ||
                    albums.GetValueOrDefault(album) >= maxPerAlbum + relaxation)
                    continue;
                result.Add(track);
                ids.Add(track.Id);
                artists[artist] = artists.GetValueOrDefault(artist) + 1;
                albums[album] = albums.GetValueOrDefault(album) + 1;
            }
        }
        return result;
    }

    private static string Explain(
        TrackModel track,
        RecommendationPreset preset,
        TasteProfile profile,
        IReadOnlySet<string> likedIds,
        DateTime now)
    {
        if (preset == RecommendationPreset.RediscoverFavorites)
        {
            var prefix = likedIds.Contains(track.Id) ? "红心或“喜欢的音乐”曲目" : $"曾听过 {track.PlayCount} 次";
            if (!track.LastPlayedAt.HasValue)
                return $"{prefix} · 没有最近播放记录";
            var days = Math.Max(0, (int)(now - track.LastPlayedAt.Value).TotalDays);
            return days >= 365 ? $"{prefix} · 已约 {Math.Max(1, days / 365)} 年没听" : $"{prefix} · 已 {days} 天没听";
        }
        if (preset == RecommendationPreset.UnplayedGems && likedIds.Contains(track.Id))
            return "已喜欢 · 但还没有播放记录";
        if (preset == RecommendationPreset.ThirtyMinuteRadio &&
            (likedIds.Contains(track.Id) || track.PlayCount > 0))
            return likedIds.Contains(track.Id)
                ? "红心或“喜欢的音乐”曲目 · 给电台打底"
                : $"已经听过 {track.PlayCount} 次 · 安心穿插";
        var connection = profile.StrongestConnection(track);
        return preset switch
        {
            RecommendationPreset.UnplayedGems => $"从未播放 · {connection}",
            RecommendationPreset.FavoriteExpansion or RecommendationPreset.ThirtyMinuteRadio => $"未收藏 · {connection}",
            _ => connection
        };
    }

    private static string PresetTitle(RecommendationPreset preset) => preset switch
    {
        RecommendationPreset.RediscoverFavorites => "很久没听",
        RecommendationPreset.UnplayedGems => "从未播放",
        RecommendationPreset.FavoriteExpansion => "收藏延伸",
        RecommendationPreset.ThirtyMinuteRadio => "30 分钟电台",
        _ => "安心发现"
    };

    private static string BuildInsight(
        RecommendationPreset preset,
        IReadOnlyList<TrackModel> selected,
        IReadOnlySet<string> likedIds)
    {
        if (selected.Count == 0)
            return "暂时没有足够可靠的候选；宁可少推，也不乱推。";
        return preset switch
        {
            RecommendationPreset.RediscoverFavorites =>
                $"{selected.Count} 首 · {selected.Count(track => likedIds.Contains(track.Id))} 首来自红心或“喜欢的音乐”",
            RecommendationPreset.UnplayedGems => $"{selected.Count} 首均无播放记录，并且能说明与口味的联系",
            RecommendationPreset.FavoriteExpansion => $"{selected.Count} 首均未收藏；无法解释的随机候选已经排除",
            RecommendationPreset.ThirtyMinuteRadio =>
                $"{selected.Count} 首 · 约 {Math.Max(1, (int)Math.Round(TimeSpan.FromMilliseconds(selected.Sum(EffectiveDurationMs)).TotalMinutes))} 分钟",
            _ => $"{selected.Count} 首"
        };
    }

    private static bool IsStrictlyUnplayed(TrackModel track) =>
        track.PlayCount <= 0 && !track.LastPlayedAt.HasValue;

    private static double RecentPenalty(TrackModel track, DateTime now, double quietDays)
    {
        if (!track.LastPlayedAt.HasValue)
            return 1.0;
        var days = Math.Max(0.0, (now - track.LastPlayedAt.Value).TotalDays);
        return Math.Clamp(days / quietDays, 0.08, 1.0);
    }

    private static IEnumerable<string> Tags(TrackModel track)
    {
        if (!string.IsNullOrWhiteSpace(track.Genre))
            yield return track.Genre.Trim();
        foreach (var category in track.Categories ?? [])
            if (!string.IsNullOrWhiteSpace(category))
                yield return category.Trim();
    }

    private static string Key(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? "#" + fallback : value.Trim();

    private static int ComputeSeed(
        IReadOnlyList<TrackModel> tracks,
        RecommendationPreset preset,
        DateTime now,
        int refreshSalt)
    {
        unchecked
        {
            uint hash = 2166136261;
            void Add(string value)
            {
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
            }
            Add(now.ToString("yyyyMMdd"));
            Add(((int)preset).ToString());
            Add(refreshSalt.ToString());
            foreach (var track in tracks.OrderBy(track => track.Id, StringComparer.OrdinalIgnoreCase))
            {
                Add(track.Id);
                Add(track.PlayCount.ToString());
                Add(track.IsFavorite ? "1" : "0");
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private sealed class TasteProfile
    {
        private readonly Dictionary<string, double> _artists;
        private readonly Dictionary<string, double> _albums;
        private readonly Dictionary<string, double> _circles;
        private readonly Dictionary<string, double> _tags;

        private TasteProfile(
            Dictionary<string, double> artists,
            Dictionary<string, double> albums,
            Dictionary<string, double> circles,
            Dictionary<string, double> tags)
        {
            _artists = Normalize(artists);
            _albums = Normalize(albums);
            _circles = Normalize(circles);
            _tags = Normalize(tags);
        }

        public bool HasSignals => _artists.Count > 0 || _albums.Count > 0 || _circles.Count > 0 || _tags.Count > 0;

        public double Confidence(TrackModel track)
        {
            var artist = Affinity(_artists, track.Artist);
            var album = Affinity(_albums, track.Album);
            var circle = Affinity(_circles, track.Circle);
            var tag = Tags(track).Select(value => Affinity(_tags, value)).DefaultIfEmpty().Max();
            var combined = artist * 0.42 + album * 0.26 + circle * 0.16 + tag * 0.16;
            var explicitConnection = Math.Max(Math.Max(artist * 0.80, album * 0.72),
                Math.Max(circle * 0.58, tag * 0.50));
            return Math.Clamp(Math.Max(combined, explicitConnection), 0.0, 1.0);
        }

        public string StrongestConnection(TrackModel track)
        {
            var matches = new List<(double Score, string Text)>();
            AddMatch(matches, Affinity(_albums, track.Album), $"来自你偏好的专辑《{track.Album}》");
            AddMatch(matches, Affinity(_artists, track.Artist), $"来自你常听的艺术家 {track.Artist}");
            AddMatch(matches, Affinity(_circles, track.Circle), $"来自你偏好的社团 {track.Circle}");
            foreach (var tag in Tags(track))
                AddMatch(matches, Affinity(_tags, tag), $"命中你的“{tag}”偏好");
            return matches.OrderByDescending(match => match.Score).FirstOrDefault().Text ?? "与收藏附近的口味相连";
        }

        public static TasteProfile Build(IEnumerable<TrackModel> tracks, IReadOnlySet<string> likedIds)
        {
            var artists = new Dictionary<string, double>(StringComparer.CurrentCultureIgnoreCase);
            var albums = new Dictionary<string, double>(StringComparer.CurrentCultureIgnoreCase);
            var circles = new Dictionary<string, double>(StringComparer.CurrentCultureIgnoreCase);
            var tags = new Dictionary<string, double>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var track in tracks)
            {
                var signal = Math.Log2(1.0 + Math.Max(0, track.PlayCount)) +
                             (likedIds.Contains(track.Id) ? 5.0 : 0.0);
                if (signal <= 0)
                    continue;
                AddSignal(artists, track.Artist, signal);
                AddSignal(albums, track.Album, signal * 0.82);
                AddSignal(circles, track.Circle, signal * 0.72);
                foreach (var tag in Tags(track))
                    AddSignal(tags, tag, signal * 0.65);
            }
            return new TasteProfile(
                Keep(artists, 18), Keep(albums, 24), Keep(circles, 18), Keep(tags, 24));
        }

        private static void AddMatch(List<(double Score, string Text)> matches, double score, string text)
        {
            if (score > 0 && !string.IsNullOrWhiteSpace(text))
                matches.Add((score, text));
        }

        private static double Affinity(Dictionary<string, double> values, string? key) =>
            string.IsNullOrWhiteSpace(key) ? 0 : values.GetValueOrDefault(key.Trim());

        private static void AddSignal(Dictionary<string, double> values, string? key, double signal)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            var normalized = key.Trim();
            values[normalized] = values.GetValueOrDefault(normalized) + signal;
        }

        private static Dictionary<string, double> Keep(Dictionary<string, double> source, int count) =>
            source.OrderByDescending(pair => pair.Value).Take(count)
                .ToDictionary(pair => pair.Key, pair => pair.Value, source.Comparer);

        private static Dictionary<string, double> Normalize(Dictionary<string, double> source)
        {
            var maximum = source.Values.DefaultIfEmpty().Max();
            return maximum <= 0
                ? source
                : source.ToDictionary(pair => pair.Key, pair => pair.Value / maximum, source.Comparer);
        }
    }
}
