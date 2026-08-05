using System.Collections.Generic;

namespace OfflineMusicLibrary;

public sealed record MediaControlSnapshot(IReadOnlyList<MediaTrackOption> AudioTracks, int SelectedAudioTrack, IReadOnlyList<MediaTrackOption> VideoTracks, int SelectedVideoTrack, IReadOnlyList<MediaTrackOption> SubtitleTracks, int SelectedSubtitleTrack, IReadOnlyList<AudioDeviceOption> AudioDevices);
