using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OfflineMusicLibrary;

internal static class ThemeService
{
	private sealed record Palette(
		string Background,
		string Surface,
		string Sidebar,
		string Text,
		string Muted,
		string Accent,
		string AccentHover,
		string Selection,
		string Control,
		string ControlHover,
		string ControlPressed,
		string Line,
		string CoverFallback,
		string CoverGlyph);

	private static readonly IReadOnlyDictionary<string, Palette> Palettes = new Dictionary<string, Palette>(StringComparer.OrdinalIgnoreCase)
	{
		["Blue"] = new("#F2F5FB", "#FFFFFF", "#EAF0FA", "#1C2738", "#657289", "#4F6FE8", "#3E5BC7", "#DEE6FF", "#F7F9FD", "#ECF1FA", "#DCE5F6", "#D9E1EE", "#E2E8F2", "#93A1B7"),
		["Mint"] = new("#F3F5F4", "#FFFFFF", "#EAF0ED", "#1E2725", "#68746F", "#147D6A", "#0F6D5C", "#DCEDE8", "#F8FAF9", "#EDF3F0", "#DCE9E5", "#DCE3E0", "#E3E9E6", "#9AA9A3"),
		["Green"] = new("#F3F7F2", "#FFFFFF", "#E9F1E7", "#202A20", "#6B786A", "#438D54", "#337442", "#DEEFDF", "#F8FAF7", "#ECF4EA", "#DCEBDD", "#DCE6DB", "#E3EAE1", "#96A493"),
		["Yellow"] = new("#FAF8EF", "#FFFFFF", "#F3EFD9", "#2E2A1E", "#7A725D", "#B58B1D", "#957014", "#F5E9B9", "#FBFAF5", "#F4F0DE", "#EAE1BC", "#E8E1C9", "#ECE7D5", "#AAA185"),
		["Brown"] = new("#F6F2EE", "#FFFFFF", "#EEE5DD", "#2C241F", "#776960", "#8A5A38", "#71482D", "#EEDFCF", "#FAF8F6", "#F2EBE5", "#E7DAD0", "#E5DAD1", "#E8DED6", "#A8978B"),
		["Gold"] = new("#F8F5ED", "#FFFFFF", "#F0E9D7", "#2B281F", "#746D5A", "#A87918", "#895F10", "#F2E5B9", "#FAF8F3", "#F2EDDF", "#E8DFC6", "#E5DDC9", "#EAE4D5", "#A79D83"),
		["Pink"] = new("#FAF3F6", "#FFFFFF", "#F4E8ED", "#2C2227", "#7B6871", "#C75C86", "#A94870", "#F3DDE7", "#FCF8FA", "#F7EDF1", "#EEDDE5", "#E9DCE2", "#EDE2E7", "#AD98A2"),
		["Dark"] = new("#17191F", "#20232B", "#1C2027", "#F2F4F7", "#AAB2C0", "#7B83FF", "#9096FF", "#323849", "#272B34", "#303641", "#39404D", "#353B46", "#2E333D", "#747D8C")
	};

	public static void Apply(AppState state)
	{
		Palette palette = ResolvePalette(state);
		bool hasBackground = IsUsableBackgroundPath(state.BackgroundImagePath);
		double opacity = hasBackground ? Math.Clamp(state.UiSurfaceOpacity, 0.72, 1.0) : 1.0;

		SetBrush("AppBackgroundBrush", palette.Background, opacity);
		SetBrush("SurfaceBrush", palette.Surface, opacity);
		SetBrush("SidebarBrush", palette.Sidebar, Math.Min(1.0, opacity + 0.02));
		SetBrush("TextBrush", palette.Text, 1.0);
		SetBrush("MutedTextBrush", palette.Muted, 1.0);
		SetBrush("AccentBrush", palette.Accent, 1.0);
		SetBrush("AccentHoverBrush", palette.AccentHover, 1.0);
		SetBrush("WarmBrush", "#C55C47", 1.0);
		SetBrush("LineBrush", palette.Line, Math.Min(1.0, opacity + 0.04));
		SetBrush("SelectionBrush", palette.Selection, Math.Min(1.0, opacity + 0.02));
		SetBrush("ControlBrush", palette.Control, opacity);
		SetBrush("ControlHoverBrush", palette.ControlHover, Math.Min(1.0, opacity + 0.02));
		SetBrush("ControlPressedBrush", palette.ControlPressed, Math.Min(1.0, opacity + 0.03));
		SetBrush("InputBrush", palette.Control, opacity);
		SetBrush("ItemHoverBrush", palette.ControlHover, Math.Min(1.0, opacity + 0.02));
		SetBrush("GridLineBrush", palette.Line, Math.Min(1.0, opacity + 0.02));
		SetBrush("RowHoverBrush", palette.Control, Math.Min(1.0, opacity + 0.02));
		SetBrush("HeaderBrush", palette.Control, Math.Min(1.0, opacity + 0.02));
		SetBrush("SecondarySurfaceBrush", palette.Control, opacity);
		SetBrush("CoverFallbackBrush", palette.CoverFallback, opacity);
		SetBrush("CoverGlyphBrush", palette.CoverGlyph, 1.0);
	}

	public static Brush CreateWindowBackground(AppState state)
	{
		if (IsUsableBackgroundPath(state.BackgroundImagePath))
		{
			BitmapSource? bitmap = CoverService.LoadImageFile(state.BackgroundImagePath, 1600);
			if (bitmap != null)
			{
				return new ImageBrush(bitmap)
				{
					Stretch = Stretch.UniformToFill,
					AlignmentX = AlignmentX.Center,
					AlignmentY = AlignmentY.Center
				};
			}
		}

		Color color = ParseColor(ResolvePalette(state).Background, Colors.White);
		color.A = byte.MaxValue;
		return new SolidColorBrush(color);
	}

	private static Palette ResolvePalette(AppState state)
	{
		if (string.Equals(state.UiTheme, "Custom", StringComparison.OrdinalIgnoreCase))
		{
			Palette source = Palettes["Mint"];
			string accent = NormalizeColor(state.CustomAccentColor, source.Accent);
			Color accentColor = ParseColor(accent, ParseColor(source.Accent, Colors.Teal));
			Color hover = Color.Multiply(accentColor, 0.82f);
			Color selection = Blend(accentColor, Colors.White, 0.82);
			return source with
			{
				Accent = accent,
				AccentHover = ToHex(hover),
				Selection = ToHex(selection)
			};
		}

		return Palettes.TryGetValue(state.UiTheme ?? "", out Palette? palette) ? palette : Palettes["Mint"];
	}

	private static void SetBrush(string key, string hex, double opacity)
	{
		Color color = ParseColor(hex, Colors.Transparent);
		color.A = (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * 255.0);
		Application.Current.Resources[key] = new SolidColorBrush(color);
	}

	private static string NormalizeColor(string? value, string fallback)
	{
		try
		{
			Color color = (Color)ColorConverter.ConvertFromString(value ?? fallback);
			return ToHex(color);
		}
		catch
		{
			return fallback;
		}
	}

	private static Color ParseColor(string value, Color fallback)
	{
		try
		{
			return (Color)ColorConverter.ConvertFromString(value);
		}
		catch
		{
			return fallback;
		}
	}

	private static Color Blend(Color foreground, Color background, double backgroundAmount)
	{
		double foregroundAmount = 1.0 - backgroundAmount;
		return Color.FromRgb(
			(byte)Math.Round(foreground.R * foregroundAmount + background.R * backgroundAmount),
			(byte)Math.Round(foreground.G * foregroundAmount + background.G * backgroundAmount),
			(byte)Math.Round(foreground.B * foregroundAmount + background.B * backgroundAmount));
	}

	private static bool IsUsableBackgroundPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			FileInfo info = new(path);
			return info.Exists && info.Length is > 0 and <= ContentReadLimits.ArtworkBytes;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
		{
			return false;
		}
	}

	private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
