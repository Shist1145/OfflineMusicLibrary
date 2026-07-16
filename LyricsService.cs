using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OfflineMusicLibrary;

public static partial class LyricsService
{
    private static readonly string[] MainSuffixes = [".lrc", ".orig.lrc", ".original.lrc"];
    private static readonly string[] TranslationSuffixes = [".zh.lrc", ".cn.lrc", ".trans.lrc", ".translated.lrc", ".tlrc"];

    public static string? FindMainLyricsPath(string audioPath) =>
        FindBySuffixes(audioPath, MainSuffixes);

    public static string? FindTranslationLyricsPath(string audioPath) =>
        FindBySuffixes(audioPath, TranslationSuffixes);

    public static List<LyricLine> LoadForTrack(string audioPath)
    {
        var mainPath = FindMainLyricsPath(audioPath);
        if (mainPath is null)
            return [];

        var main = ParseFile(mainPath);
        if (main.Count == 0)
            return [];

        var translationPath = FindTranslationLyricsPath(audioPath);
        var translation = translationPath is null ? [] : ParseFile(translationPath);
        var result = new List<LyricLine>(main.Count);

        foreach (var entry in main)
        {
            var texts = entry.Value.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
            if (texts.Count == 0)
                continue;

            var translated = translation.Count > 0
                ? FindNearestText(translation, entry.Key)
                : texts.Skip(1).FirstOrDefault() ?? "";

            result.Add(new LyricLine
            {
                TimeMs = entry.Key,
                Original = texts[0],
                Translation = translated
            });
        }

        return result.OrderBy(line => line.TimeMs).ToList();
    }

    public static int FindCurrentIndex(IReadOnlyList<LyricLine> lines, long timeMs, int offsetMs = 0)
    {
        timeMs -= offsetMs;
        var low = 0;
        var high = lines.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
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
        var result = new SortedDictionary<long, List<string>>();
        string[] lines;
        try
        {
            lines = ReadAllLines(path);
        }
        catch
        {
            return result;
        }

        var untimed = new List<string>();
        foreach (var raw in lines)
        {
            var matches = TimeTagRegex().Matches(raw);
            var text = TimeTagRegex().Replace(raw, "").Trim();
            if (matches.Count == 0)
            {
                if (!MetadataRegex().IsMatch(raw) && !string.IsNullOrWhiteSpace(raw))
                    untimed.Add(raw.Trim());
                continue;
            }

            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var fraction = match.Groups[3].Value;
                var milliseconds = fraction.Length switch
                {
                    0 => 0,
                    1 => int.Parse(fraction) * 100,
                    2 => int.Parse(fraction) * 10,
                    _ => int.Parse(fraction[..3])
                };
                var timestamp = (minutes * 60L + seconds) * 1000L + milliseconds;
                if (!result.TryGetValue(timestamp, out var values))
                    result[timestamp] = values = [];
                values.Add(text);
            }
        }

        if (result.Count == 0)
        {
            for (var index = 0; index < untimed.Count; index++)
                result[index * 5000L] = [untimed[index]];
        }
        return result;
    }

    private static string[] ReadAllLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return SplitLines(new UTF8Encoding(false, true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return SplitLines(Encoding.GetEncoding(936).GetString(bytes));
        }
    }

    private static string[] SplitLines(string text) => text
        .TrimStart('\uFEFF')
        .Replace("\r\n", "\n")
        .Replace('\r', '\n')
        .Split('\n');

    private static string FindNearestText(SortedDictionary<long, List<string>> lines, long timeMs)
    {
        var match = lines
            .Where(entry => Math.Abs(entry.Key - timeMs) <= 600)
            .OrderBy(entry => Math.Abs(entry.Key - timeMs))
            .FirstOrDefault();
        return match.Value?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static string? FindBySuffixes(string audioPath, IReadOnlyList<string> suffixes)
    {
        var directory = Path.GetDirectoryName(audioPath);
        var stem = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        foreach (var suffix in suffixes)
        {
            var candidate = Path.Combine(directory, stem + suffix);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[\.:](\d{1,3}))?\]", RegexOptions.Compiled)]
    private static partial Regex TimeTagRegex();

    [GeneratedRegex(@"^\[(ar|ti|al|by|offset|re|ve|length):", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MetadataRegex();
}
