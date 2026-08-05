using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineMusicLibrary;

public enum RecommendationPreset
{
	PersonalRadar,
	DailyRecommendation,
	PersonalRoam,
	RediscoverFavorites,
	UnplayedGems,
	FavoriteExpansion,
	ThirtyMinuteRadio
}

public sealed class RecommendationResult
{
	public RecommendationPreset Preset { get; init; }

	public string Title { get; init; } = "";

	public string Description { get; init; } = "";

	public string Insight { get; init; } = "";

	public IReadOnlyList<TrackModel> Tracks { get; init; } = Array.Empty<TrackModel>();

	public IReadOnlyDictionary<string, string> Reasons { get; init; } = new Dictionary<string, string>();

	public long TotalDurationMs => Tracks.Sum(track => Math.Max(0L, track.DurationMs));

	public string ReasonFor(TrackModel track)
	{
		return Reasons.GetValueOrDefault(track.Id, "来自你的本地聆听画像");
	}
}

public static class RecommendationService
{
	public const int DefaultTrackCount = 30;

	private const long RadioTargetMs = 30 * 60 * 1000;

	private const long UnknownTrackDurationMs = 3 * 60 * 1000 + 30 * 1000;

	public static RecommendationResult Create(
		IEnumerable<TrackModel> library,
		RecommendationPreset preset,
		DateTime now,
		int refreshSalt = 0,
		int count = DefaultTrackCount,
		IEnumerable<string>? implicitFavoriteTrackIds = null)
	{
		ArgumentNullException.ThrowIfNull(library);
		List<TrackModel> tracks = library
			.Where(track => track != null && !track.IsEncryptedNcm)
			.DistinctBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
			.ToList();
		HashSet<string> likedIds = tracks.Where(track => track.IsFavorite).Select(track => track.Id)
			.Concat(implicitFavoriteTrackIds ?? Array.Empty<string>())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		int take = Math.Clamp(count, 1, Math.Max(1, tracks.Count));
		TasteProfile profile = TasteProfile.Build(tracks, likedIds);
		int seed = ComputeSeed(tracks, preset, now, refreshSalt);
		Random random = new(seed);

		IReadOnlyList<TrackModel> selected = preset switch
		{
			RecommendationPreset.PersonalRadar => CreateRadar(tracks, profile, likedIds, now, random, take),
			RecommendationPreset.DailyRecommendation => CreateDaily(tracks, profile, likedIds, now, random, take),
			RecommendationPreset.PersonalRoam => CreateRoam(tracks, now, random, take),
			RecommendationPreset.RediscoverFavorites => CreateRediscoverFavorites(tracks, likedIds, now, random, take),
			RecommendationPreset.UnplayedGems => CreateUnplayedGems(tracks, profile, likedIds, now, random, take),
			RecommendationPreset.FavoriteExpansion => CreateFavoriteExpansion(tracks, profile, likedIds, now, random, take),
			RecommendationPreset.ThirtyMinuteRadio => CreateThirtyMinuteRadio(tracks, profile, likedIds, now, random),
			_ => Array.Empty<TrackModel>()
		};
		IReadOnlyDictionary<string, string> reasons = selected.ToDictionary(
			track => track.Id,
			track => Explain(track, preset, profile, likedIds, now),
			StringComparer.OrdinalIgnoreCase);

		return preset switch
		{
			RecommendationPreset.PersonalRadar => Result(preset, "私人雷达", "从你的收藏和常听作品出发，找回相近但被忽略的声音。",
				profile.HasSignals
					? $"锁定 {profile.AnchorArtistCount} 位常听艺术家与 {profile.AnchorTagCount} 个偏好标签；陌生歌曲必须命中至少一项偏好"
					: "播放画像还在形成中，暂时优先寻找曲库里较少听见的歌曲",
				selected, reasons),
			RecommendationPreset.DailyRecommendation => Result(preset, "每日推荐", "一份每天更新、熟悉与新鲜彼此穿插的本地歌单。",
				$"{now:MM 月 dd 日}专属结果 · 收藏、播放次数与最近播放共同参与计算",
				selected, reasons),
			RecommendationPreset.PersonalRoam => Result(preset, "私人漫游", "刻意跨过艺术家、专辑与分类边界，适合不知道听什么的时候。",
				$"已打散 {selected.Select(track => NormalizeKey(track.Artist, track.Id)).Distinct(StringComparer.CurrentCultureIgnoreCase).Count()} 位艺术家；这是七种预设里最大胆的一种",
				selected, reasons),
			RecommendationPreset.RediscoverFavorites => Result(preset, "很久没听", "从收藏和真正听过的歌里，找回已经沉底的熟悉声音。",
				selected.Count == 0
					? "还没有足够的收藏或播放记录；听过几首后这里会自动出现"
					: $"优先选择至少 30 天未播放的作品 · {selected.Count(track => likedIds.Contains(track.Id))} 首来自红心或‘喜欢的音乐’歌单",
				selected, reasons),
			RecommendationPreset.UnplayedGems => Result(preset, "从未播放", "只看零播放歌曲，并过滤掉无法说明推荐理由的陌生作品。",
				selected.Count == 0
					? "暂时没有足够可靠的零播放候选；这是刻意的，宁可少推也不乱推"
					: $"{selected.Count} 首均无播放记录，并且与收藏、常听艺术家、专辑、社团或标签相连",
				selected, reasons),
			RecommendationPreset.FavoriteExpansion => Result(preset, "收藏延伸", "离开收藏夹一小步：只推荐能和已收藏作品建立明确联系的歌曲。",
				selected.Count == 0
					? "收藏画像还不足以建立可靠连接；多收藏几首后再来看看"
					: $"全部为未收藏歌曲 · 已排除无法解释的随机候选，共 {selected.Count} 首",
				selected, reasons),
			RecommendationPreset.ThirtyMinuteRadio => Result(preset, "30 分钟电台", "用熟悉作品打底，少量穿插收藏附近的新歌，自动拼成半小时。",
				selected.Count == 0
					? "还没有足够的可靠信号来组成电台"
					: $"约 {FormatDuration(selected.Sum(EffectiveDurationMs))} · 每 5 首最多穿插 1 首未听过的安全候选",
				selected, reasons),
			_ => throw new ArgumentOutOfRangeException(nameof(preset))
		};
	}

	public static string DescribeTaste(IEnumerable<TrackModel> library, IEnumerable<string>? implicitFavoriteTrackIds = null)
	{
		List<TrackModel> tracks = library.Where(track => track != null).ToList();
		HashSet<string> likedIds = tracks.Where(track => track.IsFavorite).Select(track => track.Id)
			.Concat(implicitFavoriteTrackIds ?? Array.Empty<string>())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		int favorites = tracks.Count(track => likedIds.Contains(track.Id));
		long plays = tracks.Sum(track => (long)Math.Max(0, track.PlayCount));
		int tags = tracks.SelectMany(TrackTags).Distinct(StringComparer.CurrentCultureIgnoreCase).Count();
		if (favorites == 0 && plays == 0)
		{
			return $"先从 {tracks.Count:N0} 首本地歌曲的未听程度与多样性开始认识你";
		}
		return $"已在本机从 {plays:N0} 次播放、{favorites:N0} 首收藏与 {tags:N0} 个标签中学习 · 陌生推荐必须给得出理由";
	}

	private static RecommendationResult Result(
		RecommendationPreset preset,
		string title,
		string description,
		string insight,
		IReadOnlyList<TrackModel> tracks,
		IReadOnlyDictionary<string, string> reasons)
	{
		return new RecommendationResult
		{
			Preset = preset,
			Title = title,
			Description = description,
			Insight = insight,
			Tracks = tracks,
			Reasons = reasons
		};
	}

	private static IReadOnlyList<TrackModel> CreateRadar(
		IReadOnlyList<TrackModel> tracks,
		TasteProfile profile,
		IReadOnlySet<string> likedIds,
		DateTime now,
		Random random,
		int count)
	{
		IEnumerable<TrackModel> candidates = profile.HasSignals
			? tracks.Where(track => IsKnown(track, likedIds) || profile.Confidence(track) >= 0.34)
			: tracks;
		List<(TrackModel Track, double Weight)> weighted = candidates.Select(track =>
		{
			double affinity = profile.Confidence(track) * 6.0;
			double discovery = 3.2 / (1.0 + Math.Max(0, track.PlayCount) * 0.42);
			double staleness = Staleness(track, now, neverPlayed: 2.2, scaleDays: 75.0);
			double recentPenalty = RecentPenalty(track, now, 10.0);
			double favoriteBridge = likedIds.Contains(track.Id) ? 1.4 : 1.0;
			return (track, Math.Max(0.05, (0.5 + affinity + discovery) * staleness * recentPenalty * favoriteBridge));
		}).ToList();
		return Diversify(WeightedRank(weighted, random), count, maxPerArtist: 2, maxPerAlbum: 2);
	}

	private static IReadOnlyList<TrackModel> CreateDaily(
		IReadOnlyList<TrackModel> tracks,
		TasteProfile profile,
		IReadOnlySet<string> likedIds,
		DateTime now,
		Random random,
		int count)
	{
		IEnumerable<TrackModel> candidates = profile.HasSignals
			? tracks.Where(track => IsKnown(track, likedIds) || profile.Confidence(track) >= 0.30)
			: tracks;
		List<(TrackModel Track, double Weight)> weighted = candidates.Select(track =>
		{
			double affinity = 1.0 + profile.Confidence(track) * 4.0;
			double familiarity = likedIds.Contains(track.Id) ? 2.4 : 1.0 + Math.Log2(1.0 + Math.Max(0, track.PlayCount)) * 0.24;
			double discovery = track.PlayCount == 0 ? 1.55 : 1.0;
			double recentPenalty = RecentPenalty(track, now, 4.0);
			return (track, Math.Max(0.05, affinity * familiarity * discovery * recentPenalty));
		}).ToList();
		return Diversify(WeightedRank(weighted, random), count, maxPerArtist: 3, maxPerAlbum: 2);
	}

	private static IReadOnlyList<TrackModel> CreateRoam(
		IReadOnlyList<TrackModel> tracks,
		DateTime now,
		Random random,
		int count)
	{
		List<TrackModel> remaining = tracks.ToList();
		List<TrackModel> selected = new(Math.Min(count, tracks.Count));
		Dictionary<string, int> artistCounts = new(StringComparer.CurrentCultureIgnoreCase);
		Dictionary<string, int> albumCounts = new(StringComparer.CurrentCultureIgnoreCase);
		Dictionary<string, int> tagCounts = new(StringComparer.CurrentCultureIgnoreCase);

		while (selected.Count < count && remaining.Count > 0)
		{
			List<(TrackModel Track, double Weight)> candidates = remaining.Select(track =>
			{
				string artist = NormalizeKey(track.Artist, track.Id);
				string album = NormalizeKey(track.Album, track.Id);
				int artistSeen = artistCounts.GetValueOrDefault(artist);
				int albumSeen = albumCounts.GetValueOrDefault(album);
				int tagSeen = TrackTags(track).Select(tag => tagCounts.GetValueOrDefault(tag)).DefaultIfEmpty().Max();
				double novelty = track.PlayCount == 0 ? 3.0 : 1.0 / (1.0 + track.PlayCount * 0.16);
				double diversity = Math.Pow(0.13, artistSeen) * Math.Pow(0.24, albumSeen) * Math.Pow(0.58, tagSeen);
				double recentPenalty = RecentPenalty(track, now, 14.0);
				return (track, Math.Max(0.01, novelty * diversity * recentPenalty));
			}).ToList();

			TrackModel choice = WeightedChoice(candidates, random);
			selected.Add(choice);
			remaining.Remove(choice);
			Increment(artistCounts, NormalizeKey(choice.Artist, choice.Id));
			Increment(albumCounts, NormalizeKey(choice.Album, choice.Id));
			foreach (string tag in TrackTags(choice))
			{
				Increment(tagCounts, tag);
			}
		}
		return selected;
	}

	private static IReadOnlyList<TrackModel> CreateRediscoverFavorites(
		IReadOnlyList<TrackModel> tracks,
		IReadOnlySet<string> likedIds,
		DateTime now,
		Random random,
		int count)
	{
		List<TrackModel> anchors = tracks.Where(track => likedIds.Contains(track.Id)).ToList();
		if (anchors.Count == 0)
		{
			anchors = tracks.Where(track => track.PlayCount > 0).ToList();
		}
		List<(TrackModel Track, double Weight)> weighted = anchors.Select(track =>
		{
			double age = Staleness(track, now, neverPlayed: 2.4, scaleDays: 60.0);
			double history = 1.0 + Math.Log2(1.0 + Math.Max(0, track.PlayCount)) * 0.28;
			double oldEnough = !track.LastPlayedAt.HasValue || track.LastPlayedAt.Value <= now.AddDays(-30) ? 2.2 : 0.3;
			return (track, age * history * oldEnough * (likedIds.Contains(track.Id) ? 1.7 : 1.0));
		}).ToList();
		return Diversify(WeightedRank(weighted, random), count, maxPerArtist: 3, maxPerAlbum: 2);
	}

	private static IReadOnlyList<TrackModel> CreateUnplayedGems(
		IReadOnlyList<TrackModel> tracks,
		TasteProfile profile,
		IReadOnlySet<string> likedIds,
		DateTime now,
		Random random,
		int count)
	{
		if (!profile.HasSignals)
		{
			return Array.Empty<TrackModel>();
		}
		List<(TrackModel Track, double Weight)> weighted = tracks
			.Where(IsStrictlyUnplayed)
			.Where(track => likedIds.Contains(track.Id) || profile.Confidence(track) >= 0.36)
			.Select(track =>
			{
				double confidence = profile.Confidence(track);
				double age = Math.Clamp((now - track.AddedAt).TotalDays / 120.0, 0.25, 1.4);
				double favorite = likedIds.Contains(track.Id) ? 4.0 : 1.0;
				return (track, Math.Max(0.05, (0.8 + confidence * 7.5) * favorite * age));
			})
			.ToList();
		return Diversify(WeightedRank(weighted, random), count, maxPerArtist: 2, maxPerAlbum: 2);
	}

	private static IReadOnlyList<TrackModel> CreateFavoriteExpansion(
		IReadOnlyList<TrackModel> tracks,
		TasteProfile profile,
		IReadOnlySet<string> likedIds,
		DateTime now,
		Random random,
		int count)
	{
		if (!profile.HasSignals)
		{
			return Array.Empty<TrackModel>();
		}
		List<(TrackModel Track, double Weight)> weighted = tracks
			.Where(track => !likedIds.Contains(track.Id))
			.Where(track => profile.Confidence(track) >= 0.38)
			.Select(track =>
			{
				double confidence = profile.Confidence(track);
				double discovery = track.PlayCount == 0 ? 1.65 : 1.0 / (1.0 + track.PlayCount * 0.08);
				double recentPenalty = RecentPenalty(track, now, 7.0);
				return (track, Math.Max(0.05, (0.5 + confidence * 8.0) * discovery * recentPenalty));
			})
			.ToList();
		return Diversify(WeightedRank(weighted, random), count, maxPerArtist: 2, maxPerAlbum: 2);
	}

	private static IReadOnlyList<TrackModel> CreateThirtyMinuteRadio(
		IReadOnlyList<TrackModel> tracks,
		TasteProfile profile,
		IReadOnlySet<string> likedIds,
		DateTime now,
		Random random)
	{
		List<TrackModel> familiar = tracks
			.Where(track => likedIds.Contains(track.Id) || track.PlayCount > 0)
			.ToList();
		if (familiar.Count == 0)
		{
			return Array.Empty<TrackModel>();
		}
		IEnumerable<TrackModel> familiarRanked = WeightedRank(familiar.Select(track =>
		{
			double confidence = profile.Confidence(track);
			double affection = likedIds.Contains(track.Id) ? 3.2 : 1.0 + Math.Log2(1.0 + track.PlayCount) * 0.5;
			return (track, Math.Max(0.05, affection * (1.0 + confidence * 2.0) * RecentPenalty(track, now, 3.0)));
		}), random);
		List<TrackModel> discoveries = CreateFavoriteExpansion(tracks, profile, likedIds, now, random, DefaultTrackCount)
			.Where(IsStrictlyUnplayed)
			.ToList();

		List<TrackModel> route = new();
		Queue<TrackModel> familiarQueue = new(Diversify(familiarRanked, familiar.Count, maxPerArtist: 2, maxPerAlbum: 2));
		Queue<TrackModel> discoveryQueue = new(discoveries);
		while (familiarQueue.Count > 0 || discoveryQueue.Count > 0)
		{
			for (int index = 0; index < 4 && familiarQueue.Count > 0; index++)
			{
				route.Add(familiarQueue.Dequeue());
			}
			if (discoveryQueue.Count > 0)
			{
				route.Add(discoveryQueue.Dequeue());
			}
			if (familiarQueue.Count == 0 && route.Count > 0)
			{
				break;
			}
		}

		List<TrackModel> selected = new();
		long duration = 0;
		foreach (TrackModel track in route)
		{
			long trackDuration = EffectiveDurationMs(track);
			long currentDistance = Math.Abs(RadioTargetMs - duration);
			long nextDistance = Math.Abs(RadioTargetMs - duration - trackDuration);
			if (duration < RadioTargetMs - 3 * 60 * 1000 || nextDistance < currentDistance)
			{
				selected.Add(track);
				duration += trackDuration;
			}
			if (duration >= RadioTargetMs - 60 * 1000 || selected.Count >= 40)
			{
				break;
			}
		}
		return selected;
	}

	private static string Explain(TrackModel track, RecommendationPreset preset, TasteProfile profile, IReadOnlySet<string> likedIds, DateTime now)
	{
		if (preset == RecommendationPreset.PersonalRoam)
		{
			return track.PlayCount == 0 ? "曲库里从未播放 · 用来打破重复" : "较少听见 · 用来打散路线";
		}
		if (preset == RecommendationPreset.RediscoverFavorites)
		{
			return StaleReason(track, likedIds, now);
		}
		if (preset == RecommendationPreset.UnplayedGems && likedIds.Contains(track.Id))
		{
			return "已收藏 · 但还没有播放记录";
		}
		if (preset == RecommendationPreset.ThirtyMinuteRadio && (likedIds.Contains(track.Id) || track.PlayCount > 0))
		{
			return likedIds.Contains(track.Id) ? "红心或‘喜欢的音乐’曲目 · 给电台打底" : $"已经听过 {track.PlayCount} 次 · 安心穿插";
		}

		string connection = profile.StrongestConnection(track);
		if (preset == RecommendationPreset.UnplayedGems)
		{
			return $"从未播放 · {connection}";
		}
		if (preset == RecommendationPreset.FavoriteExpansion || preset == RecommendationPreset.ThirtyMinuteRadio)
		{
			return $"未收藏 · {connection}";
		}
		if (likedIds.Contains(track.Id))
		{
			return "来自收藏 · 与当前口味高度一致";
		}
		if (track.PlayCount > 0)
		{
			return $"听过 {track.PlayCount} 次 · 最近没有重复播放";
		}
		return connection;
	}

	private static string StaleReason(TrackModel track, IReadOnlySet<string> likedIds, DateTime now)
	{
		string prefix = likedIds.Contains(track.Id) ? "红心或‘喜欢的音乐’曲目" : $"曾听过 {track.PlayCount} 次";
		if (!track.LastPlayedAt.HasValue)
		{
			return $"{prefix} · 没有最近播放记录";
		}
		int days = Math.Max(0, (int)(now - track.LastPlayedAt.Value).TotalDays);
		return days >= 365
			? $"{prefix} · 已约 {Math.Max(1, days / 365)} 年没听"
			: $"{prefix} · 已 {days} 天没听";
	}

	private static IEnumerable<TrackModel> WeightedRank(
		IEnumerable<(TrackModel Track, double Weight)> weighted,
		Random random)
	{
		return weighted
			.Select(item => (item.Track, Key: -Math.Log(Math.Max(double.Epsilon, random.NextDouble())) / Math.Max(0.001, item.Weight)))
			.OrderBy(item => item.Key)
			.Select(item => item.Track);
	}

	private static TrackModel WeightedChoice(IReadOnlyList<(TrackModel Track, double Weight)> candidates, Random random)
	{
		double total = candidates.Sum(item => Math.Max(0.0, item.Weight));
		if (total <= 0.0)
		{
			return candidates[random.Next(candidates.Count)].Track;
		}
		double target = random.NextDouble() * total;
		foreach ((TrackModel track, double weight) in candidates)
		{
			target -= Math.Max(0.0, weight);
			if (target <= 0.0)
			{
				return track;
			}
		}
		return candidates[^1].Track;
	}

	private static IReadOnlyList<TrackModel> Diversify(
		IEnumerable<TrackModel> ranked,
		int count,
		int maxPerArtist,
		int maxPerAlbum)
	{
		List<TrackModel> source = ranked.ToList();
		List<TrackModel> selected = new(Math.Min(count, source.Count));
		HashSet<string> selectedIds = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> artists = new(StringComparer.CurrentCultureIgnoreCase);
		Dictionary<string, int> albums = new(StringComparer.CurrentCultureIgnoreCase);

		for (int relaxation = 0; selected.Count < count && relaxation < 4; relaxation++)
		{
			int artistLimit = maxPerArtist + relaxation;
			int albumLimit = maxPerAlbum + relaxation;
			foreach (TrackModel track in source)
			{
				if (selected.Count >= count || selectedIds.Contains(track.Id))
				{
					continue;
				}
				string artist = NormalizeKey(track.Artist, track.Id);
				string album = NormalizeKey(track.Album, track.Id);
				if (artists.GetValueOrDefault(artist) >= artistLimit || albums.GetValueOrDefault(album) >= albumLimit)
				{
					continue;
				}
				selected.Add(track);
				selectedIds.Add(track.Id);
				Increment(artists, artist);
				Increment(albums, album);
			}
		}
		return selected;
	}

	private static bool IsKnown(TrackModel track, IReadOnlySet<string> likedIds)
	{
		return likedIds.Contains(track.Id) || track.PlayCount > 0 || track.LastPlayedAt.HasValue;
	}

	private static bool IsStrictlyUnplayed(TrackModel track)
	{
		return track.PlayCount <= 0 && !track.LastPlayedAt.HasValue;
	}

	private static double Staleness(TrackModel track, DateTime now, double neverPlayed, double scaleDays)
	{
		if (!track.LastPlayedAt.HasValue)
		{
			return neverPlayed;
		}
		double days = Math.Max(0.0, (now - track.LastPlayedAt.Value).TotalDays);
		return Math.Clamp(0.2 + days / scaleDays, 0.2, 2.2);
	}

	private static double RecentPenalty(TrackModel track, DateTime now, double quietDays)
	{
		if (!track.LastPlayedAt.HasValue)
		{
			return 1.0;
		}
		double days = Math.Max(0.0, (now - track.LastPlayedAt.Value).TotalDays);
		return Math.Clamp(days / quietDays, 0.08, 1.0);
	}

	private static IEnumerable<string> TrackTags(TrackModel track)
	{
		if (!string.IsNullOrWhiteSpace(track.Genre))
		{
			yield return track.Genre.Trim();
		}
		foreach (string category in track.Categories ?? new List<string>())
		{
			if (!string.IsNullOrWhiteSpace(category))
			{
				yield return category.Trim();
			}
		}
	}

	private static string NormalizeKey(string? value, string fallback)
	{
		return string.IsNullOrWhiteSpace(value) ? "#" + fallback : value.Trim();
	}

	private static void Increment(Dictionary<string, int> counts, string key)
	{
		counts[key] = counts.GetValueOrDefault(key) + 1;
	}

	private static long EffectiveDurationMs(TrackModel track)
	{
		return track.DurationMs > 0 ? track.DurationMs : UnknownTrackDurationMs;
	}

	private static string FormatDuration(long durationMs)
	{
		TimeSpan duration = TimeSpan.FromMilliseconds(Math.Max(0, durationMs));
		return duration.TotalHours >= 1.0 ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟" : $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} 分钟";
	}

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
				foreach (char character in value)
				{
					hash ^= character;
					hash *= 16777619;
				}
			}
			Add(preset is RecommendationPreset.PersonalRadar or RecommendationPreset.RediscoverFavorites
				? $"{now.Year}-{now.DayOfYear / 7}"
				: now.ToString("yyyyMMdd"));
			Add(((int)preset).ToString());
			Add(refreshSalt.ToString());
			foreach (TrackModel track in tracks.OrderBy(track => track.Id, StringComparer.OrdinalIgnoreCase))
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

		public int AnchorArtistCount => _artists.Count(pair => pair.Value >= 0.35);

		public int AnchorTagCount => _tags.Count(pair => pair.Value >= 0.3);

		public double Confidence(TrackModel track)
		{
			double artist = Affinity(_artists, track.Artist);
			double album = Affinity(_albums, track.Album);
			double circle = Affinity(_circles, track.Circle);
			double tag = TrackTags(track).Select(value => Affinity(_tags, value)).DefaultIfEmpty().Max();
			double combined = artist * 0.42 + album * 0.26 + circle * 0.16 + tag * 0.16;
			double explicitConnection = Math.Max(Math.Max(artist * 0.80, album * 0.72), Math.Max(circle * 0.58, tag * 0.50));
			return Math.Clamp(Math.Max(combined, explicitConnection), 0.0, 1.0);
		}

		public string StrongestConnection(TrackModel track)
		{
			List<(double Score, string Text)> matches = new();
			AddMatch(matches, Affinity(_albums, track.Album), $"来自你偏好的专辑《{track.Album}》");
			AddMatch(matches, Affinity(_artists, track.Artist), $"来自你常听的艺术家 {track.Artist}");
			AddMatch(matches, Affinity(_circles, track.Circle), $"来自你偏好的社团 {track.Circle}");
			foreach (string tag in TrackTags(track))
			{
				AddMatch(matches, Affinity(_tags, tag), $"命中你的“{tag}”偏好");
			}
			return matches.OrderByDescending(match => match.Score).FirstOrDefault().Text ?? "与收藏附近的听歌口味相连";
		}

		public static TasteProfile Build(IEnumerable<TrackModel> tracks, IReadOnlySet<string> likedIds)
		{
			Dictionary<string, double> artists = new(StringComparer.CurrentCultureIgnoreCase);
			Dictionary<string, double> albums = new(StringComparer.CurrentCultureIgnoreCase);
			Dictionary<string, double> circles = new(StringComparer.CurrentCultureIgnoreCase);
			Dictionary<string, double> tags = new(StringComparer.CurrentCultureIgnoreCase);
			foreach (TrackModel track in tracks)
			{
				double signal = Math.Log2(1.0 + Math.Max(0, track.PlayCount)) + (likedIds.Contains(track.Id) ? 5.0 : 0.0);
				if (signal <= 0.0)
				{
					continue;
				}
				AddSignal(artists, track.Artist, signal);
				AddSignal(albums, track.Album, signal * 0.82);
				AddSignal(circles, track.Circle, signal * 0.72);
				foreach (string tag in TrackTags(track))
				{
					AddSignal(tags, tag, signal * 0.65);
				}
			}
			return new TasteProfile(
				KeepStrongest(artists, 18),
				KeepStrongest(albums, 24),
				KeepStrongest(circles, 18),
				KeepStrongest(tags, 24));
		}

		private static void AddMatch(List<(double Score, string Text)> matches, double score, string text)
		{
			if (score > 0.0 && !string.IsNullOrWhiteSpace(text))
			{
				matches.Add((score, text));
			}
		}

		private static double Affinity(Dictionary<string, double> values, string? key)
		{
			return string.IsNullOrWhiteSpace(key) ? 0.0 : values.GetValueOrDefault(key.Trim());
		}

		private static void AddSignal(Dictionary<string, double> values, string? key, double signal)
		{
			if (!string.IsNullOrWhiteSpace(key))
			{
				string normalized = key.Trim();
				values[normalized] = values.GetValueOrDefault(normalized) + signal;
			}
		}

		private static Dictionary<string, double> KeepStrongest(Dictionary<string, double> source, int count)
		{
			return source.OrderByDescending(pair => pair.Value).Take(count)
				.ToDictionary(pair => pair.Key, pair => pair.Value, source.Comparer);
		}

		private static Dictionary<string, double> Normalize(Dictionary<string, double> source)
		{
			double maximum = source.Values.DefaultIfEmpty().Max();
			if (maximum <= 0.0)
			{
				return source;
			}
			return source.ToDictionary(pair => pair.Key, pair => pair.Value / maximum, source.Comparer);
		}
	}
}
