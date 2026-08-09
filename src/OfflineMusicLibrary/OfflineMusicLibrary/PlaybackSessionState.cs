using System.Collections.Generic;

namespace OfflineMusicLibrary;

public sealed class PlaybackSessionState
{
	public List<string> QueueTrackIds { get; set; } = new();

	public int CurrentIndex { get; set; } = -1;

	public long PositionMs { get; set; }

	public string RepeatMode { get; set; } = "All";

	public string ShuffleMode { get; set; } = "Off";

	public List<string> RecentShuffleIds { get; set; } = new();

	public string? WaitingRootId { get; set; }

	public string SelectedOutputProfileId { get; set; } = "";
}
