using System;

namespace OfflineMusicLibrary;

public readonly record struct PlaybackEngineProfile(
	string VisualizationMode,
	string AudioBackend,
	string SpatialAudioMode,
	string EqualizerPreset,
	string HardwareDecoding,
	string VideoOutput,
	int CacheMilliseconds,
	bool IsNetworkSource = false);

public static class PlaybackStabilityService
{
	public static int EffectiveWatchdogTimeoutSeconds(AppState state, int recoveryCount)
	{
		ArgumentNullException.ThrowIfNull(state);
		int baseTimeoutSeconds = Math.Clamp(state.PlaybackWatchdogTimeoutSeconds, 8, 30);
		int recoveryGraceSeconds = Math.Clamp(recoveryCount, 0, 3) * 5;
		return baseTimeoutSeconds + recoveryGraceSeconds;
	}

	public static bool HasStableProgressAfterRecovery(long currentMilliseconds, long recoveryResumeAtMilliseconds)
	{
		if (recoveryResumeAtMilliseconds < 0)
		{
			return false;
		}
		long current = Math.Max(0L, currentMilliseconds);
		long recoveryPoint = Math.Max(0L, recoveryResumeAtMilliseconds);
		return current - recoveryPoint >= 5000L;
	}

	public static PlaybackEngineProfile Resolve(AppState state, bool isVideo, string? path = null)
	{
		ArgumentNullException.ThrowIfNull(state);
		bool isNetworkSource = IsNetworkSource(state, path);
		int networkCacheMilliseconds = Math.Clamp(state.NasBufferSeconds, 5, 30) * 1000;
		if (state.SafePlaybackMode)
		{
			return new PlaybackEngineProfile(
				"Off",
				"DirectSound",
				"Off",
				"Off",
				"Disabled",
				"Auto",
				isNetworkSource ? Math.Max(10000, networkCacheMilliseconds) : 2500,
				isNetworkSource);
		}
		return new PlaybackEngineProfile(
			isVideo ? "Off" : state.VisualizationMode,
			state.AudioBackend,
			AudioEffectPresets.NormalizeSpatialAudio(state.SpatialAudioMode),
			AudioEffectPresets.NormalizeEqualizer(state.EqualizerPreset),
			state.HardwareDecoding,
			state.VideoOutput,
			isNetworkSource ? networkCacheMilliseconds : 1500,
			isNetworkSource);
	}

	private static bool IsNetworkSource(AppState state, string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		if (LibraryRootCatalog.IsUncPath(path))
		{
			return true;
		}
		LibraryRootState? root = LibraryRootCatalog.FindOwningRoot(state.LibraryRoots, path);
		return root != null && LibraryRootKinds.IsNetwork(root.RootKind);
	}
}
