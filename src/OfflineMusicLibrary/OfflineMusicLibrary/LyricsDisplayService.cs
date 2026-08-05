using System;
using System.Collections.Generic;
using System.Linq;

namespace OfflineMusicLibrary;

public static class LyricsDisplayModes
{
	public const string OriginalTranslation = "OriginalTranslation";

	public const string OriginalRomanization = "OriginalRomanization";

	public const string RomanizationTranslation = "RomanizationTranslation";

	public const string All = "All";

	public const string OriginalOnly = "OriginalOnly";

	public const string RomanizationOnly = "RomanizationOnly";

	public const string TranslationOnly = "TranslationOnly";

	public static string Normalize(string? mode)
	{
		return mode switch
		{
			OriginalTranslation => OriginalTranslation,
			OriginalRomanization => OriginalRomanization,
			RomanizationTranslation => RomanizationTranslation,
			All => All,
			OriginalOnly => OriginalOnly,
			RomanizationOnly => RomanizationOnly,
			TranslationOnly => TranslationOnly,
			_ => OriginalTranslation
		};
	}

	public static string NextCommonMode(string? mode)
	{
		return Normalize(mode) switch
		{
			OriginalTranslation => OriginalRomanization,
			OriginalRomanization => RomanizationTranslation,
			RomanizationTranslation => All,
			_ => OriginalTranslation
		};
	}

	public static string CompactLabel(string? mode)
	{
		return Normalize(mode) switch
		{
			OriginalTranslation => "原+中",
			OriginalRomanization => "原+音",
			RomanizationTranslation => "音+中",
			All => "三语",
			OriginalOnly => "原文",
			RomanizationOnly => "音译",
			TranslationOnly => "中文",
			_ => "原+中"
		};
	}
}

public enum LyricTextKind
{
	None,
	Original,
	Romanization,
	Translation
}

public sealed class LyricsDisplayContent
{
	public string Primary { get; init; } = "";

	public LyricTextKind PrimaryKind { get; init; }

	public string Secondary { get; init; } = "";

	public LyricTextKind SecondaryKind { get; init; }

	public string Tertiary { get; init; } = "";

	public LyricTextKind TertiaryKind { get; init; }
}

public static class LyricsDisplayService
{
	public static LyricsDisplayContent Resolve(LyricLine line, string? mode)
	{
		ArgumentNullException.ThrowIfNull(line);
		string normalized = LyricsDisplayModes.Normalize(mode);
		LyricTextKind[] preferred = normalized switch
		{
			LyricsDisplayModes.OriginalRomanization => new[] { LyricTextKind.Original, LyricTextKind.Romanization, LyricTextKind.Translation },
			LyricsDisplayModes.RomanizationTranslation => new[] { LyricTextKind.Romanization, LyricTextKind.Translation, LyricTextKind.Original },
			LyricsDisplayModes.All => new[] { LyricTextKind.Original, LyricTextKind.Romanization, LyricTextKind.Translation },
			LyricsDisplayModes.OriginalOnly => new[] { LyricTextKind.Original, LyricTextKind.Translation, LyricTextKind.Romanization },
			LyricsDisplayModes.RomanizationOnly => new[] { LyricTextKind.Romanization, LyricTextKind.Original, LyricTextKind.Translation },
			LyricsDisplayModes.TranslationOnly => new[] { LyricTextKind.Translation, LyricTextKind.Original, LyricTextKind.Romanization },
			_ => new[] { LyricTextKind.Original, LyricTextKind.Translation, LyricTextKind.Romanization }
		};
		int limit = normalized == LyricsDisplayModes.All ? 3 :
			normalized.EndsWith("Only", StringComparison.Ordinal) ? 1 : 2;
		List<(LyricTextKind Kind, string Text)> available = preferred
			.Select(kind => (Kind: kind, Text: GetText(line, kind)))
			.Where(item => !string.IsNullOrWhiteSpace(item.Text))
			.DistinctBy(item => item.Text, StringComparer.Ordinal)
			.Take(limit)
			.ToList();
		return new LyricsDisplayContent
		{
			Primary = available.ElementAtOrDefault(0).Text ?? "",
			PrimaryKind = available.ElementAtOrDefault(0).Kind,
			Secondary = available.ElementAtOrDefault(1).Text ?? "",
			SecondaryKind = available.ElementAtOrDefault(1).Kind,
			Tertiary = available.ElementAtOrDefault(2).Text ?? "",
			TertiaryKind = available.ElementAtOrDefault(2).Kind
		};
	}

	private static string GetText(LyricLine line, LyricTextKind kind)
	{
		return kind switch
		{
			LyricTextKind.Romanization => line.Romanization,
			LyricTextKind.Translation => line.Translation,
			_ => line.Original
		};
	}
}
