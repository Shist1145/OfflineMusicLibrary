using OfflineMusicLibrary;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
	[STAThread]
	private static void Main()
	{
		App application = new();
		application.InitializeComponent();

		RequireTheme("Mint", minimumNormalContrast: 7.0, minimumMutedContrast: 4.0);
		RequireTheme("Dark", minimumNormalContrast: 7.0, minimumMutedContrast: 4.5);
		Console.WriteLine("Theme checks passed.");
	}

static void RequireTheme(string theme, double minimumNormalContrast, double minimumMutedContrast)
{
	ThemeService.Apply(new AppState { UiTheme = theme });
	Brush text = ResourceBrush("TextBrush");
	Brush muted = ResourceBrush("MutedTextBrush");
	Brush surface = ResourceBrush("SurfaceBrush");
	Brush input = ResourceBrush("InputBrush");
	Brush sidebar = ResourceBrush("SidebarBrush");
	Brush control = ResourceBrush("ControlBrush");

	RequireContrast(theme, "normal text / surface", text, surface, minimumNormalContrast);
	RequireContrast(theme, "normal text / input", text, input, minimumNormalContrast);
	RequireContrast(theme, "normal text / sidebar", text, sidebar, minimumNormalContrast);
	RequireContrast(theme, "muted text / surface", muted, surface, minimumMutedContrast);
	RequireContrast(theme, "muted text / control", muted, control, minimumMutedContrast);

	RequireDynamicForeground<TextBlock>(theme);
	RequireDynamicForeground<Label>(theme);
	RequireDynamicForeground<CheckBox>(theme);
	RequireDynamicForeground<RadioButton>(theme);
	RequireDynamicForeground<ListBox>(theme);
	RequireDynamicForeground<ListBoxItem>(theme);
	RequireDynamicForeground<ComboBox>(theme);
	RequireDynamicForeground<ComboBoxItem>(theme);
	RequireDynamicForeground<TabControl>(theme);
	RequireDynamicForeground<TabItem>(theme);
	RequireDynamicForeground<MenuItem>(theme);
	RequireDynamicForeground<ContextMenu>(theme);

	TextBox textBox = new();
	textBox.Style = Application.Current.TryFindResource(typeof(TextBox)) as Style;
	textBox.ApplyTemplate();
	Require(SameColor(textBox.Foreground, text), $"{theme}: TextBox must use TextBrush.");
	Require(SameColor(textBox.Background, input), $"{theme}: TextBox must use InputBrush instead of a white system default.");
	Require(SameColor(textBox.CaretBrush, text), $"{theme}: TextBox caret must remain visible.");

	ComboBox comboBox = new();
	comboBox.Style = Application.Current.TryFindResource(typeof(ComboBox)) as Style;
	comboBox.ApplyTemplate();
	Require(SameColor(comboBox.Foreground, text), $"{theme}: ComboBox must use TextBrush.");
	Require(SameColor(comboBox.Background, input), $"{theme}: ComboBox must use InputBrush instead of a white system default.");
}

static void RequireDynamicForeground<T>(string theme) where T : FrameworkElement, new()
{
	T control = new();
	control.Style = Application.Current.TryFindResource(typeof(T)) as Style;
	control.ApplyTemplate();
	Brush? foreground = control switch
	{
		Control element => element.Foreground,
		TextBlock element => element.Foreground,
		_ => null
	};
	Require(SameColor(foreground, ResourceBrush("TextBrush")) || control is TabItem,
		$"{theme}: {typeof(T).Name} must not fall back to the Windows black foreground.");
}

static Brush ResourceBrush(string key) =>
	Application.Current.Resources[key] as Brush ?? throw new InvalidOperationException($"Missing brush resource: {key}");

static void RequireContrast(string theme, string pair, Brush foreground, Brush background, double minimum)
{
	double ratio = ContrastRatio(ToOpaqueColor(foreground), ToOpaqueColor(background));
	Require(ratio >= minimum,
		$"{theme}: {pair} contrast {ratio.ToString("0.00", CultureInfo.InvariantCulture)} is below {minimum.ToString("0.0", CultureInfo.InvariantCulture)}.");
}

static Color ToOpaqueColor(Brush brush)
{
	if (brush is not SolidColorBrush solid)
	{
		throw new InvalidOperationException("Theme contrast checks require solid color brushes.");
	}
	return Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B);
}

static bool SameColor(Brush? left, Brush right) =>
	left is SolidColorBrush leftSolid && right is SolidColorBrush rightSolid && leftSolid.Color == rightSolid.Color;

static double ContrastRatio(Color first, Color second)
{
	double light = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
	double dark = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
	return (light + 0.05) / (dark + 0.05);
}

static double RelativeLuminance(Color color)
{
	static double Channel(byte value)
	{
		double component = value / 255.0;
		return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);
	}
	return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
}

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}
}
