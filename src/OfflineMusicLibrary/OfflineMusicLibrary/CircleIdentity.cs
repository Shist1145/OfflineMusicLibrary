using System.Globalization;
using System.Text;

namespace OfflineMusicLibrary;

public static class CircleIdentity
{
	public const string UnknownKey = "circle::unknown";

	public static string Create(TrackModel track)
	{
		return Create(track.Circle);
	}

	public static string Create(string? circle)
	{
		if (string.IsNullOrWhiteSpace(circle))
		{
			return "circle::unknown";
		}
		string text = circle.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
		StringBuilder builder = new StringBuilder(text.Length);
		bool previousWasSpace = false;
		string text2 = text;
		foreach (char character in text2)
		{
			bool flag = char.IsLetterOrDigit(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber;
			if (!flag)
			{
				UnicodeCategory unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(character);
				bool flag2 = ((unicodeCategory == UnicodeCategory.MathSymbol || (uint)(unicodeCategory - 27) <= 1u) ? true : false);
				flag = flag2;
			}
			if (flag)
			{
				builder.Append(character);
				previousWasSpace = false;
			}
			else if (!previousWasSpace)
			{
				builder.Append(' ');
				previousWasSpace = true;
			}
		}
		string key = builder.ToString().Trim();
		if (key.Length != 0)
		{
			return "circle::" + key;
		}
		return "circle::unknown";
	}
}
