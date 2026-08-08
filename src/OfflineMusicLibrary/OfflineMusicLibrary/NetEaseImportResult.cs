using System;
using System.Collections.Generic;

namespace OfflineMusicLibrary;

public sealed record NetEaseImportResult(string PlaylistName, string PlaylistId, int DeclaredTrackCount, IReadOnlyList<NetEaseTrack> Tracks, IReadOnlyList<TrackModel> Matched, IReadOnlyList<NetEaseTrack> Missing)
{
	public int TrackIdCount { get; init; }

	public int ResolvedTrackCount { get; init; }

	public int ExactMatchCount { get; init; }

	public int FuzzyMatchCount { get; init; }

	public int CorrectedCloudIdCount { get; init; }

	public IReadOnlyList<string> UnresolvedTrackIds { get; init; } = Array.Empty<string>();

	public IReadOnlyList<string> RemoteTrackIds { get; init; } = Array.Empty<string>();

	public bool HasCompleteTrackIds => TrackIdCount >= DeclaredTrackCount;

	public bool HasCompleteRemoteDetails
	{
		get
		{
			if (UnresolvedTrackIds.Count == 0)
			{
				return HasCompleteTrackIds;
			}
			return false;
		}
	}
}
