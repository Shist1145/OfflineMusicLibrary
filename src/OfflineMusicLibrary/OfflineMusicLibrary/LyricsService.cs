using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OfflineMusicLibrary;

public static partial class LyricsService
{
	private static readonly string[] MainSuffixes = new string[3] { ".lrc", ".orig.lrc", ".original.lrc" };

	private static readonly string[] TranslationSuffixes = new string[5] { ".zh.lrc", ".cn.lrc", ".trans.lrc", ".translated.lrc", ".tlrc" };

	public static string? FindMainLyricsPath(string audioPath)
	{
		return FindBySuffixes(audioPath, MainSuffixes);
	}

	public static string? FindTranslationLyricsPath(string audioPath)
	{
		return FindBySuffixes(audioPath, TranslationSuffixes);
	}

	public static List<LyricLine> LoadForTrack(string audioPath)
	{
		string mainPath = FindMainLyricsPath(audioPath);
		if (mainPath == null)
		{
			return new List<LyricLine>();
		}
		SortedDictionary<long, List<string>> main = ParseFile(mainPath);
		if (main.Count == 0)
		{
			return new List<LyricLine>();
		}
		string translationPath = FindTranslationLyricsPath(audioPath);
		SortedDictionary<long, List<string>> translation = (SortedDictionary<long, List<string>>)((translationPath == null) ? ((IDictionary)new SortedDictionary<long, List<string>>()) : ((IDictionary)ParseFile(translationPath)));
		List<LyricLine> result = new List<LyricLine>(main.Count);
		foreach (KeyValuePair<long, List<string>> entry in main)
		{
			List<string> texts = entry.Value.Where((string value) => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
			if (texts.Count != 0)
			{
				ClassifyInlineLyrics(texts, out string romanization, out string embeddedTranslation);
				string translated = ((translation.Count > 0) ? FindNearestText(translation, entry.Key) : embeddedTranslation);
				result.Add(new LyricLine
				{
					TimeMs = entry.Key,
					Original = texts[0],
					Romanization = romanization,
					Translation = translated
				});
			}
		}
		return result.OrderBy((LyricLine line) => line.TimeMs).ToList();
	}

	private static void ClassifyInlineLyrics(IReadOnlyList<string> texts, out string romanization, out string translation)
	{
		romanization = "";
		translation = "";
		foreach (string candidate in texts.Skip(1))
		{
			if (string.IsNullOrWhiteSpace(romanization) && LooksLikeRomanization(candidate))
			{
				romanization = candidate;
			}
			else if (string.IsNullOrWhiteSpace(translation))
			{
				translation = candidate;
			}
		}
	}

	private static bool LooksLikeRomanization(string text)
	{
		int latinLetters = 0;
		foreach (char character in text)
		{
			if (IsCjk(character))
			{
				return false;
			}
			if (IsLatinLetter(character))
			{
				latinLetters++;
			}
			else if (char.IsLetter(character))
			{
				return false;
			}
		}
		return latinLetters > 0;
	}

	private static bool IsLatinLetter(char character)
	{
		return character >= 'A' && character <= 'Z' ||
			character >= 'a' && character <= 'z' ||
			character >= '\u00c0' && character <= '\u024f' ||
			character >= '\u1e00' && character <= '\u1eff';
	}

	private static bool IsCjk(char character)
	{
		return character >= '\u3040' && character <= '\u30ff' ||
			character >= '\u3400' && character <= '\u9fff' ||
			character >= '\uac00' && character <= '\ud7af' ||
			character >= '\uf900' && character <= '\ufaff';
	}

	public static int FindCurrentIndex(IReadOnlyList<LyricLine> lines, long timeMs, int offsetMs = 0)
	{
		timeMs -= offsetMs;
		int low = 0;
		int high = lines.Count - 1;
		int result = -1;
		while (low <= high)
		{
			int middle = low + (high - low) / 2;
			if (lines[middle].TimeMs <= timeMs + 120)
			{
				result = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}
		return result;
	}

	private static SortedDictionary<long, List<string>> ParseFile(string path)
	{
		SortedDictionary<long, List<string>> result = new SortedDictionary<long, List<string>>();
		string[] lines;
		try
		{
			lines = ReadAllLines(path);
		}
		catch
		{
			return result;
		}
		List<string> untimed = new List<string>();
		string[] array = lines;
		foreach (string raw in array)
		{
			MatchCollection matches = TimeTagRegex().Matches(raw);
			string text = TimeTagRegex().Replace(raw, "").Trim();
			if (matches.Count == 0)
			{
				if (!MetadataRegex().IsMatch(raw) && !string.IsNullOrWhiteSpace(raw))
				{
					untimed.Add(raw.Trim());
				}
				continue;
			}
			foreach (Match item in matches)
			{
				int minutes = int.Parse(item.Groups[1].Value);
				int seconds = int.Parse(item.Groups[2].Value);
				string fraction = item.Groups[3].Value;
				long timestamp = ((long)minutes * 60L + seconds) * 1000 + fraction.Length switch
				{
					0 => 0,
					1 => int.Parse(fraction) * 100,
					2 => int.Parse(fraction) * 10,
					_ => int.Parse(fraction.Substring(0, 3)),
				};
				if (!result.TryGetValue(timestamp, out var values))
				{
					values = (result[timestamp] = new List<string>());
				}
				values.Add(text);
			}
		}
		if (result.Count == 0)
		{
			for (int index = 0; index < untimed.Count; index++)
			{
				long key = (long)index * 5000L;
				int i = 1;
				List<string> list2 = new List<string>(i);
				CollectionsMarshal.SetCount(list2, i);
				CollectionsMarshal.AsSpan(list2)[0] = untimed[index];
				result[key] = list2;
			}
		}
		return result;
	}

	private static string[] ReadAllLines(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		try
		{
			return SplitLines(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes));
		}
		catch (DecoderFallbackException)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			return SplitLines(Encoding.GetEncoding(936).GetString(bytes));
		}
	}

	private static string[] SplitLines(string text)
	{
		return text.TrimStart('\ufeff').Replace("\r\n", "\n").Replace('\r', '\n')
			.Split('\n');
	}

	private static string FindNearestText(SortedDictionary<long, List<string>> lines, long timeMs)
	{
		return (from entry in lines
			where Math.Abs(entry.Key - timeMs) <= 600
			orderby Math.Abs(entry.Key - timeMs)
			select entry).FirstOrDefault().Value?.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value)) ?? "";
	}

	private static string? FindBySuffixes(string audioPath, IReadOnlyList<string> suffixes)
	{
		string directory = Path.GetDirectoryName(audioPath);
		string stem = Path.GetFileNameWithoutExtension(audioPath);
		if (string.IsNullOrWhiteSpace(directory))
		{
			return null;
		}
		foreach (string suffix in suffixes)
		{
			string candidate = Path.Combine(directory, stem + suffix);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		return null;
	}

	[GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]", RegexOptions.Compiled)]
	private static partial Regex TimeTagRegex();

	[GeneratedRegex(@"^\[(ar|ti|al|by|offset|re|ve|length):", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex MetadataRegex();
}
