using System;
using System.Windows.Media;

namespace OfflineMusicLibrary;

public readonly record struct LyricsPalette(
	Color Original,
	Color GradientEnd,
	Color Romanization,
	Color Translation,
	Color Stroke);

public static class LyricsStyleService
{
	public static LyricsPalette ResolvePalette(AppState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		Color stroke = ParseColor(state.DesktopLyricsStrokeColor, Colors.Black);
		return state.DesktopLyricsColorScheme switch
		{
			"Classic" => new LyricsPalette(
				ParseColor("#F4F7F6", Colors.White),
				ParseColor("#C7D2CE", Colors.LightGray),
				ParseColor("#DCE6E2", Colors.LightGray),
				ParseColor("#C7D2CE", Colors.LightGray),
				stroke),
			"HighContrast" => new LyricsPalette(
				ParseColor("#FFE16B", Colors.Yellow),
				Colors.White,
				ParseColor("#67E8F9", Colors.Cyan),
				Colors.White,
				stroke),
			"Custom" => new LyricsPalette(
				ParseColor(state.DesktopLyricsPrimaryColor, Colors.White),
				ParseColor(state.DesktopLyricsSecondaryColor, Colors.LightGreen),
				ParseColor(state.DesktopLyricsRomanizationColor, ParseColor(state.DesktopLyricsSecondaryColor, Colors.LightGreen)),
				ParseColor(state.DesktopLyricsTranslationColor, ParseColor(state.DesktopLyricsPrimaryColor, Colors.White)),
				stroke),
			_ => new LyricsPalette(
				ParseColor("#C9B7FF", Colors.White),
				ParseColor("#79D9A9", Colors.LightGreen),
				ParseColor("#79D9A9", Colors.LightGreen),
				ParseColor("#C9B7FF", Colors.White),
				stroke)
		};
	}

	public static Brush CreateForeground(AppState state, LyricTextKind kind, bool allowOriginalGradient = true)
	{
		LyricsPalette palette = ResolvePalette(state);
		double opacity = OpacityFor(state, kind);
		Brush brush;
		if (kind == LyricTextKind.Original && allowOriginalGradient && state.DesktopLyricsUseGradient)
		{
			brush = new LinearGradientBrush(palette.Original, palette.GradientEnd, 90.0);
		}
		else
		{
			Color color = kind switch
			{
				LyricTextKind.Romanization => palette.Romanization,
				LyricTextKind.Translation => palette.Translation,
				_ => palette.Original
			};
			brush = new SolidColorBrush(color);
		}
		brush.Opacity = opacity;
		return brush;
	}

	public static Brush CreateStroke(AppState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state.DesktopLyricsStroke
			? new SolidColorBrush(ResolvePalette(state).Stroke)
			: Brushes.Transparent;
	}

	public static double StrokeThickness(AppState state, double fontSize, LyricTextKind kind)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (!state.DesktopLyricsStroke)
		{
			return 0.0;
		}
		double scale = Math.Clamp(state.DesktopLyricsStrokeScale, 0.5, 2.0);
		return kind switch
		{
			LyricTextKind.Romanization => Math.Clamp(fontSize * 0.075, 1.0, 2.4) * scale,
			LyricTextKind.Translation => Math.Clamp(fontSize * 0.075, 1.0, 2.4) * scale,
			_ => Math.Clamp(fontSize * 0.075, 1.4, 3.2) * scale
		};
	}

	public static double OpacityFor(AppState state, LyricTextKind kind)
	{
		ArgumentNullException.ThrowIfNull(state);
		return kind switch
		{
			LyricTextKind.Romanization => Math.Clamp(state.DesktopLyricsRomanizationOpacity, 0.35, 1.0),
			LyricTextKind.Translation => Math.Clamp(state.DesktopLyricsTranslationOpacity, 0.35, 1.0),
			_ => Math.Clamp(state.DesktopLyricsOriginalOpacity, 0.35, 1.0)
		};
	}

	public static string NormalizeColor(string? value, string fallback)
	{
		string normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
		try
		{
			_ = (Color)ColorConverter.ConvertFromString(normalized);
			return normalized;
		}
		catch
		{
			return fallback;
		}
	}

	public static Color ParseColor(string? value, Color fallback)
	{
		try
		{
			return (Color)ColorConverter.ConvertFromString(value ?? "");
		}
		catch
		{
			return fallback;
		}
	}
}
