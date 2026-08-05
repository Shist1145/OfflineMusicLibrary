using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineMusicLibrary;

public static class AudioEffectPresets
{
	private static readonly IReadOnlyDictionary<string, EqualizerProfile> Profiles = new Dictionary<string, EqualizerProfile>(StringComparer.OrdinalIgnoreCase)
	{
		["BassBoost"] = new EqualizerProfile(-4f, new float[10] { 5.5f, 4.5f, 3f, 1f, 0f, -0.5f, -1f, -1f, -0.5f, 0f }),
		["Vocal"] = new EqualizerProfile(-2.5f, new float[10] { -2f, -1f, 0f, 1.5f, 3.5f, 4f, 2.5f, 0.5f, 0f, -0.5f }),
		["Pop"] = new EqualizerProfile(-3f, new float[10] { -1f, 1f, 2.5f, 3f, 1.5f, -0.5f, -1f, 0.5f, 2f, 2.5f }),
		["Rock"] = new EqualizerProfile(-3.5f, new float[10] { 4f, 2.5f, -1f, -2f, -0.5f, 2f, 3.5f, 4f, 4f, 4f }),
		["Electronic"] = new EqualizerProfile(-4f, new float[10] { 4.5f, 3.5f, 0.5f, -1.5f, -1f, 1f, 2f, 3.5f, 4.5f, 4.5f }),
		["Classical"] = new EqualizerProfile(-2.5f, new float[10] { 3f, 2f, 1f, 0f, -1f, -1f, 0f, 1.5f, 2.5f, 3f }),
		["Night"] = new EqualizerProfile(-4.5f, new float[10] { -2.5f, -1.5f, 0f, 2f, 3f, 2.5f, 0.5f, -1.5f, -2.5f, -3f })
	};

	public static string NormalizeEqualizer(string? value)
	{
		if (value == null || !Profiles.ContainsKey(value))
		{
			return "Off";
		}
		return Profiles.Keys.First((string key) => string.Equals(key, value, StringComparison.OrdinalIgnoreCase));
	}

	public static string NormalizeSpatialAudio(string? value)
	{
		if (string.Equals(value, "StereoWide", StringComparison.OrdinalIgnoreCase))
		{
			return "StereoWide";
		}
		if (string.Equals(value, "Room3D", StringComparison.OrdinalIgnoreCase))
		{
			return "Room3D";
		}
		return "Off";
	}

	public static EqualizerProfile? GetProfile(string? value)
	{
		string normalized = NormalizeEqualizer(value);
		if (!(normalized == "Off"))
		{
			return Profiles[normalized];
		}
		return null;
	}

	public static string SpatialFilter(string? value)
	{
		string text = NormalizeSpatialAudio(value);
		if (!(text == "StereoWide"))
		{
			if (text == "Room3D")
			{
				return "spatializer";
			}
			return "";
		}
		return "stereo_widen";
	}
}
