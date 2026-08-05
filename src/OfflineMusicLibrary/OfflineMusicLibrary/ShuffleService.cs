using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineMusicLibrary;

public static class ShuffleService
{
	public static readonly string[] Modes = new string[6] { "Off", "Uniform", "Smart", "Album", "Artist", "LeastPlayed" };

	public static TrackModel Choose(IReadOnlyList<TrackModel> queue, TrackModel? current, string mode, Random random, IReadOnlyCollection<string>? recentIds = null)
	{
		if (queue.Count == 0)
		{
			throw new ArgumentException("播放队列不能为空。", "queue");
		}
		if (queue.Count == 1)
		{
			return queue[0];
		}
		List<TrackModel> candidates = queue.Where((TrackModel track) => track.Id != current?.Id).ToList();
		return mode switch
		{
			"Album" => ChooseGrouped(candidates, current?.Album, (TrackModel track) => track.Album, random),
			"Artist" => ChooseGrouped(candidates, current?.Artist, (TrackModel track) => track.Artist, random),
			"LeastPlayed" => ChooseLeastPlayed(candidates, random),
			"Smart" => ChooseSmart(candidates, current, recentIds ?? Array.Empty<string>(), random),
			_ => candidates[random.Next(candidates.Count)],
		};
	}

	private static TrackModel ChooseGrouped(IReadOnlyList<TrackModel> tracks, string? currentGroup, Func<TrackModel, string> selector, Random random)
	{
		List<IGrouping<string, TrackModel>> groups = (from grouping in tracks.GroupBy<TrackModel, string>(selector, StringComparer.CurrentCultureIgnoreCase)
			where !string.Equals(grouping.Key, currentGroup, StringComparison.CurrentCultureIgnoreCase)
			select grouping).ToList();
		if (groups.Count == 0)
		{
			groups = tracks.GroupBy<TrackModel, string>(selector, StringComparer.CurrentCultureIgnoreCase).ToList();
		}
		List<TrackModel> group = groups[random.Next(groups.Count)].ToList();
		return group[random.Next(group.Count)];
	}

	private static TrackModel ChooseLeastPlayed(IReadOnlyList<TrackModel> tracks, Random random)
	{
		int minimum = tracks.Min((TrackModel track) => track.PlayCount);
		List<TrackModel> leastPlayed = tracks.Where((TrackModel track) => track.PlayCount == minimum).ToList();
		return leastPlayed[random.Next(leastPlayed.Count)];
	}

	private static TrackModel ChooseSmart(IReadOnlyList<TrackModel> tracks, TrackModel? current, IReadOnlyCollection<string> recentIds, Random random)
	{
		List<(TrackModel, double)> weighted = tracks.Select(delegate(TrackModel track)
		{
			double num = 1.0 / (1.0 + (double)track.PlayCount * 0.35);
			if (recentIds.Contains(track.Id))
			{
				num *= 0.08;
			}
			if (string.Equals(track.Artist, current?.Artist, StringComparison.CurrentCultureIgnoreCase))
			{
				num *= 0.25;
			}
			if (string.Equals(track.Album, current?.Album, StringComparison.CurrentCultureIgnoreCase))
			{
				num *= 0.4;
			}
			DateTime? lastPlayedAt = track.LastPlayedAt;
			if (lastPlayedAt.HasValue)
			{
				DateTime valueOrDefault = lastPlayedAt.GetValueOrDefault();
				double num2 = Math.Max(0.0, (DateTime.Now - valueOrDefault).TotalHours);
				num *= Math.Clamp(num2 / 72.0, 0.12, 1.0);
			}
			return (Track: track, Weight: num);
		}).ToList();
		double target = random.NextDouble() * weighted.Sum<(TrackModel, double)>(((TrackModel Track, double Weight) tuple) => tuple.Weight);
		foreach (var item in weighted)
		{
			target -= item.Item2;
			if (target <= 0.0)
			{
				return item.Item1;
			}
		}
		return weighted[weighted.Count - 1].Item1;
	}
}
