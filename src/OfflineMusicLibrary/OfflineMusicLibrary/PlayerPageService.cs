using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OfflineMusicLibrary;

public static class PlayerPageModes
{
	public const string Standard = "Standard";

	public const string Vinyl = "Vinyl";

	public const string Lyrics = "Lyrics";

	public static string Normalize(string? mode)
	{
		return mode switch
		{
			Vinyl => Vinyl,
			Lyrics => Lyrics,
			_ => Standard
		};
	}
}

public sealed class SimilarTrackSuggestion
{
	public required TrackModel Track { get; init; }

	public string Reason { get; init; } = "来自本地曲库的相近作品";

	public string Title => Track.Title;

	public string Artist => Track.Artist;

	public string Album => Track.Album;

	public System.Windows.Media.Imaging.BitmapSource? CoverThumbnail => Track.CoverThumbnail;
}

public static class PlayerPageService
{
	public static IReadOnlyList<SimilarTrackSuggestion> FindSimilarTracks(
		IEnumerable<TrackModel> library,
		TrackModel current,
		int count = 10)
	{
		ArgumentNullException.ThrowIfNull(library);
		ArgumentNullException.ThrowIfNull(current);
		if (count <= 0)
		{
			return Array.Empty<SimilarTrackSuggestion>();
		}

		return library
			.Where(track => track != null && !track.IsEncryptedNcm && !string.Equals(track.Id, current.Id, StringComparison.OrdinalIgnoreCase))
			.DistinctBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
			.Select(track => new
			{
				Track = track,
				Score = SimilarityScore(current, track),
				Reason = SimilarityReason(current, track)
			})
			.OrderByDescending(item => item.Score)
			.ThenByDescending(item => item.Track.IsFavorite)
			.ThenByDescending(item => item.Track.PlayCount)
			.ThenBy(item => item.Track.Title, StringComparer.CurrentCultureIgnoreCase)
			.Take(Math.Clamp(count, 1, 30))
			.Select(item => new SimilarTrackSuggestion
			{
				Track = item.Track,
				Reason = item.Reason
			})
			.ToList();
	}

	public static bool RequiresVideoSurface(AppState state, TrackModel? track)
	{
		ArgumentNullException.ThrowIfNull(state);
		return track?.IsVideo == true
			|| (track != null
				&& !state.SafePlaybackMode
				&& !string.Equals(state.VisualizationMode, "Off", StringComparison.OrdinalIgnoreCase));
	}

	public static double TonearmAngle(long currentMilliseconds, long durationMilliseconds)
	{
		double progress = durationMilliseconds > 0
			? Math.Clamp(currentMilliseconds / (double)durationMilliseconds, 0.0, 1.0)
			: 0.0;
		return -22.0 + progress * 15.0;
	}

	public static string BuildLocalEncyclopediaSummary(TrackModel track)
	{
		ArgumentNullException.ThrowIfNull(track);
		string fileTitle = DisplayOrFallback(Path.GetFileNameWithoutExtension(track.FilePath), "未知歌曲");
		string title = DisplayOrFallback(track.Title, fileTitle);
		string artist = DisplayOrFallback(track.Artist, "未知艺术家");
		string album = DisplayOrFallback(track.Album, "未知专辑");
		string circle = DisplayOrFallback(track.Circle, "未识别社团");
		return $"《{title}》由 {artist} 演唱，收录于《{album}》。本地资料将它归入 {circle}，所有信息均来自媒体标签与当前曲库，不依赖联网。";
	}

	private static double SimilarityScore(TrackModel current, TrackModel candidate)
	{
		double score = 0.0;
		if (SameMeaningful(current.Album, candidate.Album))
		{
			score += 9.0;
		}
		if (SameMeaningful(current.Artist, candidate.Artist))
		{
			score += 8.0;
		}
		if (SameMeaningful(current.AlbumArtist, candidate.AlbumArtist))
		{
			score += 5.0;
		}
		if (SameMeaningful(current.Circle, candidate.Circle))
		{
			score += 5.0;
		}
		if (SameMeaningful(current.Genre, candidate.Genre))
		{
			score += 3.0;
		}
		int sharedCategories = (current.Categories ?? new List<string>())
			.Intersect(candidate.Categories ?? new List<string>(), StringComparer.CurrentCultureIgnoreCase)
			.Count();
		score += Math.Min(6.0, sharedCategories * 2.0);
		if (candidate.IsFavorite)
		{
			score += 0.6;
		}
		score += Math.Min(1.4, Math.Log2(1.0 + Math.Max(0, candidate.PlayCount)) * 0.2);
		return score;
	}

	private static string SimilarityReason(TrackModel current, TrackModel candidate)
	{
		if (SameMeaningful(current.Album, candidate.Album))
		{
			return "同一专辑 · " + DisplayOrFallback(candidate.Album, "未知专辑");
		}
		if (SameMeaningful(current.Artist, candidate.Artist))
		{
			return "同一艺术家 · " + DisplayOrFallback(candidate.Artist, "未知艺术家");
		}
		if (SameMeaningful(current.Circle, candidate.Circle))
		{
			return "同一社团 · " + DisplayOrFallback(candidate.Circle, "未识别");
		}
		string? sharedCategory = (current.Categories ?? new List<string>())
			.Intersect(candidate.Categories ?? new List<string>(), StringComparer.CurrentCultureIgnoreCase)
			.FirstOrDefault();
		if (!string.IsNullOrWhiteSpace(sharedCategory))
		{
			return "相同分类 · " + sharedCategory;
		}
		if (SameMeaningful(current.Genre, candidate.Genre))
		{
			return "相同流派 · " + candidate.Genre;
		}
		return candidate.IsFavorite ? "你的收藏 · 从当前歌曲向外延伸" : "本地曲库探索";
	}

	private static bool SameMeaningful(string? left, string? right)
	{
		return !string.IsNullOrWhiteSpace(left)
			&& !string.IsNullOrWhiteSpace(right)
			&& !left.StartsWith("未知", StringComparison.CurrentCultureIgnoreCase)
			&& string.Equals(left.Trim(), right.Trim(), StringComparison.CurrentCultureIgnoreCase);
	}

	private static string DisplayOrFallback(string? value, string fallback)
	{
		return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
	}
}
