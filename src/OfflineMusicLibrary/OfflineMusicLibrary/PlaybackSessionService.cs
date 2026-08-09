using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineMusicLibrary;

public readonly record struct RestoredPlaybackSession(
	IReadOnlyList<TrackModel> Queue,
	int CurrentIndex,
	long PositionMs,
	IReadOnlyList<string> RecentShuffleIds,
	string? WaitingRootId);

public static class PlaybackSessionService
{
	public static void Normalize(AppState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		state.PlaybackSession ??= new PlaybackSessionState();
		PlaybackSessionState session = state.PlaybackSession;
		session.QueueTrackIds ??= new List<string>();
		session.RecentShuffleIds ??= new List<string>();
		session.QueueTrackIds = session.QueueTrackIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Select(id => id.Trim())
			.ToList();
		session.RecentShuffleIds = session.RecentShuffleIds
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.TakeLast(25)
			.ToList();
		if (session.QueueTrackIds.Count == 0 && !string.IsNullOrWhiteSpace(state.LastTrackId))
		{
			session.QueueTrackIds.Add(state.LastTrackId);
			session.CurrentIndex = 0;
			session.PositionMs = Math.Max(0, state.LastPlaybackPositionMs);
		}
		session.CurrentIndex = session.QueueTrackIds.Count == 0
			? -1
			: Math.Clamp(session.CurrentIndex, 0, session.QueueTrackIds.Count - 1);
		session.PositionMs = Math.Max(0, session.PositionMs);
		session.RepeatMode = NormalizeRepeatMode(string.IsNullOrWhiteSpace(session.RepeatMode) ? state.RepeatMode : session.RepeatMode);
		session.ShuffleMode = NormalizeShuffleMode(string.IsNullOrWhiteSpace(session.ShuffleMode) ? state.ShuffleMode : session.ShuffleMode);
		state.RepeatMode = session.RepeatMode;
		state.ShuffleMode = session.ShuffleMode;
		state.ShuffleEnabled = session.ShuffleMode != "Off";
		session.WaitingRootId = string.IsNullOrWhiteSpace(session.WaitingRootId) ? null : session.WaitingRootId.Trim();
		session.SelectedOutputProfileId ??= "";
	}

	public static void Capture(
		AppState state,
		IReadOnlyList<TrackModel> queue,
		int queueIndex,
		TrackModel? currentTrack,
		long positionMs,
		IEnumerable<string> recentShuffleIds,
		string? waitingRootId = null)
	{
		ArgumentNullException.ThrowIfNull(state);
		state.PlaybackSession ??= new PlaybackSessionState();
		PlaybackSessionState session = state.PlaybackSession;
		session.QueueTrackIds = (queue ?? Array.Empty<TrackModel>())
			.Where(track => track != null && !string.IsNullOrWhiteSpace(track.Id))
			.Select(track => track.Id)
			.ToList();
		int currentIndex = currentTrack == null
			? queueIndex
			: session.QueueTrackIds.FindIndex(id => string.Equals(id, currentTrack.Id, StringComparison.OrdinalIgnoreCase));
		session.CurrentIndex = session.QueueTrackIds.Count == 0 ? -1 : Math.Clamp(currentIndex < 0 ? queueIndex : currentIndex, 0, session.QueueTrackIds.Count - 1);
		session.PositionMs = state.RememberPlaybackProgress ? Math.Max(0, positionMs) : 0;
		session.RepeatMode = NormalizeRepeatMode(state.RepeatMode);
		session.ShuffleMode = NormalizeShuffleMode(state.ShuffleMode);
		session.RecentShuffleIds = (recentShuffleIds ?? Array.Empty<string>())
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.TakeLast(25)
			.ToList();
		session.WaitingRootId = string.IsNullOrWhiteSpace(waitingRootId) ? null : waitingRootId;
		session.SelectedOutputProfileId = state.PreferredAudioDeviceId ?? "";

		if (currentTrack != null)
		{
			state.LastTrackId = currentTrack.Id;
			state.LastPlaybackPositionMs = session.PositionMs;
		}
	}

	public static RestoredPlaybackSession Restore(AppState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		Normalize(state);
		Dictionary<string, TrackModel> tracksById = state.Tracks
			.Where(track => track != null && !string.IsNullOrWhiteSpace(track.Id))
			.GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
		string? currentTrackId = state.PlaybackSession.CurrentIndex >= 0 && state.PlaybackSession.CurrentIndex < state.PlaybackSession.QueueTrackIds.Count
			? state.PlaybackSession.QueueTrackIds[state.PlaybackSession.CurrentIndex]
			: null;
		List<TrackModel> queue = state.PlaybackSession.QueueTrackIds
			.Select(id => tracksById.GetValueOrDefault(id))
			.Where(track => track != null)
			.Cast<TrackModel>()
			.ToList();
		int restoredCurrentIndex = currentTrackId == null
			? -1
			: queue.FindIndex(track => string.Equals(track.Id, currentTrackId, StringComparison.OrdinalIgnoreCase));
		int index = queue.Count == 0
			? -1
			: restoredCurrentIndex >= 0
				? restoredCurrentIndex
				: Math.Clamp(state.PlaybackSession.CurrentIndex, 0, queue.Count - 1);
		return new RestoredPlaybackSession(
			queue,
			index,
			Math.Max(0, state.PlaybackSession.PositionMs),
			state.PlaybackSession.RecentShuffleIds.ToArray(),
			state.PlaybackSession.WaitingRootId);
	}

	private static string NormalizeRepeatMode(string? value)
	{
		return value is "Off" or "One" or "All" ? value : "All";
	}

	private static string NormalizeShuffleMode(string? value)
	{
		return value != null && ShuffleService.Modes.Contains(value, StringComparer.Ordinal) ? value : "Off";
	}
}
