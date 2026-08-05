using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OfflineMusicLibrary;

public static class AlbumIdentity
{
	private static readonly Regex DiscFolderRegex = new Regex("^(?:cd|disc|disk|vol(?:ume)?|第?\\s*\\d+\\s*[枚卷碟]|disk)\\s*[-_ ]?\\s*\\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex BracketedSuffixRegex = new Regex("[\\(\\[【（].*?(?:flac|mp3|wav|aac|m4a|hi[\\s-]?res|lossless|无损|自抓|抓轨|分轨|整轨|bk|booklet|scan|scans|log|cue).*?[\\)\\]】）]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex AlbumNoiseRegex = new Regex("(?i)\\b(?:flac|mp3|wav|aac|m4a|hi[\\s-]?res|lossless|remaster(?:ed)?|limited edition|bonus disc)\\b|无损|自抓|抓轨|分轨|整轨|限定版|通常版|初回|扫图|歌词本", RegexOptions.Compiled);

	private static readonly HashSet<string> GenericAlbumNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"未知专辑", "unknown album", "unknown", "album", "albums", "single", "singles", "cd", "disc", "disk",
		"ost", "original soundtrack", "various artists", "v a", "va"
	};

	private static readonly HashSet<char> MeaningfulAlbumSymbols = new HashSet<char>
	{
		'△', '▽', '▲', '▼', '○', '●', '◎', '◇', '◆', '□',
		'■', '☆', '★', '∞', '∴', '∵', '※', '＊', '♪', '♫',
		'♬', '♭', '♯', '＋', '−', '×', '÷', '＝', '≠', '≈',
		'≡', 'Ⅰ', 'Ⅱ', 'Ⅲ', 'Ⅳ', 'Ⅴ', 'Ⅵ', 'Ⅶ', 'Ⅷ', 'Ⅸ',
		'Ⅹ', 'Ⅺ', 'Ⅻ'
	};

	public static string Create(TrackModel track)
	{
		string albumFolder = GetAlbumFolder(track.FilePath);
		string value = FirstNonEmpty(track.Album, Path.GetFileName(albumFolder), "未知专辑");
		string normalizedAlbum = NormalizeAlbum(value);
		if (!IsUsableAlbumTitle(value, normalizedAlbum))
		{
			if (!string.IsNullOrWhiteSpace(albumFolder))
			{
				return "folder::" + NormalizePath(albumFolder);
			}
			return "unknown::" + track.Id;
		}
		return "album::" + normalizedAlbum;
	}

	public static string Create(string? albumArtist, string? artist, string? album)
	{
		string value = FirstNonEmpty(album, "未知专辑");
		string normalizedAlbum = NormalizeAlbum(value);
		if (!IsUsableAlbumTitle(value, normalizedAlbum))
		{
			return "tag::" + Normalize(FirstNonEmpty(albumArtist, artist, "未知艺术家")) + "::" + normalizedAlbum;
		}
		return "album::" + normalizedAlbum;
	}

	public static string FolderScopedFallback(TrackModel track)
	{
		string albumFolder = GetAlbumFolder(track.FilePath);
		if (!string.IsNullOrWhiteSpace(albumFolder))
		{
			return "folder::" + NormalizePath(albumFolder);
		}
		return Create(track.AlbumArtist, track.Artist, track.Album);
	}

	public static List<string> MigrateFavoriteKeys(IEnumerable<TrackModel> tracks, IEnumerable<string> favoriteKeys)
	{
		List<string> original = favoriteKeys
			.Where(key => !string.IsNullOrWhiteSpace(key))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		HashSet<string> migrated = new HashSet<string>(original, StringComparer.OrdinalIgnoreCase);
		foreach (IGrouping<string, TrackModel> album in tracks.GroupBy(Create, StringComparer.OrdinalIgnoreCase))
		{
			List<string> legacyFolderKeys = album
				.Select(FolderScopedFallback)
				.Where(key => !string.Equals(key, album.Key, StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (!legacyFolderKeys.Any(migrated.Contains))
			{
				continue;
			}
			migrated.Add(album.Key);
			foreach (string legacyKey in legacyFolderKeys)
			{
				migrated.Remove(legacyKey);
			}
		}
		return original
			.Where(migrated.Contains)
			.Concat(migrated.Where(key => !original.Contains(key, StringComparer.OrdinalIgnoreCase)))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	public static string DisplayArtist(TrackModel track)
	{
		return FirstNonEmpty(track.AlbumArtist, track.Artist, "未知艺术家");
	}

	private static string FirstNonEmpty(params string?[] values)
	{
		return values.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
	}

	private static string GetAlbumFolder(string filePath)
	{
		try
		{
			string directory = Path.GetDirectoryName(filePath);
			if (string.IsNullOrWhiteSpace(directory))
			{
				return "";
			}
			string folderName = Path.GetFileName(directory);
			if (DiscFolderRegex.IsMatch(folderName))
			{
				string parent = Directory.GetParent(directory)?.FullName;
				if (!string.IsNullOrWhiteSpace(parent))
				{
					return parent;
				}
			}
			return directory;
		}
		catch
		{
			return "";
		}
	}

	private static string NormalizePath(string path)
	{
		try
		{
			return Path.GetFullPath(path).TrimEnd(new char[2]
			{
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar
			}).ToUpperInvariant();
		}
		catch
		{
			return path.Trim().ToUpperInvariant();
		}
	}

	private static string NormalizeAlbum(string value)
	{
		string withoutDecorations = BracketedSuffixRegex.Replace(value, "");
		withoutDecorations = AlbumNoiseRegex.Replace(withoutDecorations, "");
		return Normalize(withoutDecorations);
	}

	private static bool IsUsableAlbumTitle(string value, string normalized)
	{
		if (string.IsNullOrWhiteSpace(normalized))
		{
			return false;
		}
		return !GenericAlbumNames.Contains(normalized);
	}

	private static string Normalize(string value)
	{
		string text = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
		StringBuilder builder = new StringBuilder(text.Length);
		bool previousWasSpace = false;
		foreach (Rune character in text.EnumerateRunes())
		{
			UnicodeCategory category = Rune.GetUnicodeCategory(character);
			if (Rune.IsLetterOrDigit(character) || category == UnicodeCategory.LetterNumber || IsMeaningfulSymbol(character))
			{
				builder.Append(character.ToString());
				previousWasSpace = false;
			}
			else if (category == UnicodeCategory.Format ||
				category == UnicodeCategory.Control ||
				category == UnicodeCategory.NonSpacingMark ||
				category == UnicodeCategory.SpacingCombiningMark ||
				category == UnicodeCategory.EnclosingMark)
			{
				// Invisible formatting and variation characters must not split two
				// album titles that are visually identical in the library.
				continue;
			}
			else if (!previousWasSpace)
			{
				builder.Append(' ');
				previousWasSpace = true;
			}
		}
		return builder.ToString().Trim();
	}

	private static bool IsMeaningfulSymbol(Rune character)
	{
		if (character.IsBmp && MeaningfulAlbumSymbols.Contains((char)character.Value))
		{
			return true;
		}
		UnicodeCategory unicodeCategory = Rune.GetUnicodeCategory(character);
		if (unicodeCategory == UnicodeCategory.MathSymbol || (uint)(unicodeCategory - 27) <= 1u)
		{
			return true;
		}
		return false;
	}
}
