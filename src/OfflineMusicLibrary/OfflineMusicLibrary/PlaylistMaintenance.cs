using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineMusicLibrary;

public sealed record PlaylistCleanupResult(
	int OriginalCount,
	int CurrentCount,
	int RemovedBlank,
	int RemovedDuplicate,
	int RemovedMissing)
{
	public bool Changed => OriginalCount != CurrentCount;

	public int RemovedCount => OriginalCount - CurrentCount;
}

public static class PlaylistMaintenance
{
	public static PlaylistCleanupResult Clean(PlaylistModel playlist, IEnumerable<TrackModel> library)
	{
		ArgumentNullException.ThrowIfNull(playlist);
		ArgumentNullException.ThrowIfNull(library);

		HashSet<string> validIds = library
			.Select(track => track.Id)
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return Clean(playlist, validIds);
	}

	public static List<string> BuildSynchronizedTrackIds(
		IEnumerable<string> existingTrackIds,
		IEnumerable<TrackModel> matchedTracks,
		IReadOnlyCollection<string> remoteTrackIds,
		bool canPruneMissingRemoteTracks,
		IEnumerable<TrackModel> library)
	{
		ArgumentNullException.ThrowIfNull(existingTrackIds);
		ArgumentNullException.ThrowIfNull(matchedTracks);
		ArgumentNullException.ThrowIfNull(remoteTrackIds);
		ArgumentNullException.ThrowIfNull(library);

		Dictionary<string, TrackModel> tracksById = library
			.Where(track => !string.IsNullOrWhiteSpace(track.Id))
			.GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
		List<string> synchronized = new List<string>();
		HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (TrackModel track in matchedTracks)
		{
			if (!string.IsNullOrWhiteSpace(track.Id) && tracksById.ContainsKey(track.Id) && added.Add(track.Id))
			{
				synchronized.Add(track.Id);
			}
		}

		HashSet<string>? currentRemoteIds = canPruneMissingRemoteTracks
			? remoteTrackIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase)
			: null;
		foreach (string existingId in existingTrackIds)
		{
			if (string.IsNullOrWhiteSpace(existingId) ||
				!tracksById.TryGetValue(existingId, out TrackModel? existingTrack) ||
				!added.Add(existingId))
			{
				continue;
			}
			if (currentRemoteIds == null || existingTrack.GetCloudIds().Any(currentRemoteIds.Contains))
			{
				synchronized.Add(existingId);
			}
			else
			{
				added.Remove(existingId);
			}
		}
		return synchronized;
	}

	private static PlaylistCleanupResult Clean(PlaylistModel playlist, HashSet<string> validIds)
	{
		List<string> original = playlist.TrackIds ?? new List<string>();
		List<string> cleaned = new List<string>(original.Count);
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int removedBlank = 0;
		int removedDuplicate = 0;
		int removedMissing = 0;
		foreach (string id in original)
		{
			if (string.IsNullOrWhiteSpace(id))
			{
				removedBlank++;
			}
			else if (!seen.Add(id))
			{
				removedDuplicate++;
			}
			else if (!validIds.Contains(id))
			{
				removedMissing++;
			}
			else
			{
				cleaned.Add(id);
			}
		}
		if (cleaned.Count != original.Count)
		{
			playlist.TrackIds = cleaned;
		}
		return new PlaylistCleanupResult(
			original.Count,
			cleaned.Count,
			removedBlank,
			removedDuplicate,
			removedMissing);
	}
}
