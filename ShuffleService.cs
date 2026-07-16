namespace OfflineMusicLibrary;

public static class ShuffleService
{
    public static readonly string[] Modes = ["Off", "Uniform", "Smart", "Album", "Artist", "LeastPlayed"];

    public static TrackModel Choose(
        IReadOnlyList<TrackModel> queue,
        TrackModel? current,
        string mode,
        Random random,
        IReadOnlyCollection<string>? recentIds = null)
    {
        if (queue.Count == 0)
            throw new ArgumentException("播放队列不能为空。", nameof(queue));
        if (queue.Count == 1)
            return queue[0];

        var candidates = queue.Where(track => track.Id != current?.Id).ToList();
        return mode switch
        {
            "Album" => ChooseGrouped(candidates, current?.Album, track => track.Album, random),
            "Artist" => ChooseGrouped(candidates, current?.Artist, track => track.Artist, random),
            "LeastPlayed" => ChooseLeastPlayed(candidates, random),
            "Smart" => ChooseSmart(candidates, current, recentIds ?? [], random),
            _ => candidates[random.Next(candidates.Count)]
        };
    }

    private static TrackModel ChooseGrouped(
        IReadOnlyList<TrackModel> tracks,
        string? currentGroup,
        Func<TrackModel, string> selector,
        Random random)
    {
        var groups = tracks
            .GroupBy(selector, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => !string.Equals(group.Key, currentGroup, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        if (groups.Count == 0)
            groups = tracks.GroupBy(selector, StringComparer.CurrentCultureIgnoreCase).ToList();
        var group = groups[random.Next(groups.Count)].ToList();
        return group[random.Next(group.Count)];
    }

    private static TrackModel ChooseLeastPlayed(IReadOnlyList<TrackModel> tracks, Random random)
    {
        var minimum = tracks.Min(track => track.PlayCount);
        var leastPlayed = tracks.Where(track => track.PlayCount == minimum).ToList();
        return leastPlayed[random.Next(leastPlayed.Count)];
    }

    private static TrackModel ChooseSmart(
        IReadOnlyList<TrackModel> tracks,
        TrackModel? current,
        IReadOnlyCollection<string> recentIds,
        Random random)
    {
        var weighted = tracks.Select(track =>
        {
            var weight = 1d / (1 + track.PlayCount * 0.35);
            if (recentIds.Contains(track.Id))
                weight *= 0.08;
            if (string.Equals(track.Artist, current?.Artist, StringComparison.CurrentCultureIgnoreCase))
                weight *= 0.25;
            if (string.Equals(track.Album, current?.Album, StringComparison.CurrentCultureIgnoreCase))
                weight *= 0.4;
            if (track.LastPlayedAt is DateTime lastPlayed)
            {
                var ageHours = Math.Max(0, (DateTime.Now - lastPlayed).TotalHours);
                weight *= Math.Clamp(ageHours / 72d, 0.12, 1d);
            }
            return (Track: track, Weight: weight);
        }).ToList();

        var target = random.NextDouble() * weighted.Sum(item => item.Weight);
        foreach (var item in weighted)
        {
            target -= item.Weight;
            if (target <= 0)
                return item.Track;
        }
        return weighted[^1].Track;
    }
}
