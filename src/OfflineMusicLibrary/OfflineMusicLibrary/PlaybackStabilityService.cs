using System;

namespace OfflineMusicLibrary;

public readonly record struct PlaybackEngineProfile(
	string VisualizationMode,
	string AudioBackend,
	string SpatialAudioMode,
	string EqualizerPreset,
	string HardwareDecoding,
	string VideoOutput,
	int CacheMilliseconds);

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

	public static PlaybackEngineProfile Resolve(AppState state, bool isVideo)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (state.SafePlaybackMode)
		{
			return new PlaybackEngineProfile(
				"Off",
				"DirectSound",
				"Off",
				"Off",
				"Disabled",
				"Auto",
				2500);
		}
		return new PlaybackEngineProfile(
			isVideo ? "Off" : state.VisualizationMode,
			state.AudioBackend,
			AudioEffectPresets.NormalizeSpatialAudio(state.SpatialAudioMode),
			AudioEffectPresets.NormalizeEqualizer(state.EqualizerPreset),
			state.HardwareDecoding,
			state.VideoOutput,
			1500);
	}
}
