using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace OfflineMusicLibrary;

public partial class DesktopLyricsWindow : Window, IComponentConnector
{
	private const int WmNcHitTest = 132;

	private static readonly nint HtTransparent = new IntPtr(-1);

	private AppState _settings;

	private bool _locked;

	private bool _hovered;

	private bool _interactionActivated;

	private HwndSource? _windowSource;

	private LyricLine? _currentLine;

	private string _fallbackPrimary = "桌面歌词";

	private string _fallbackSecondary = "";

	public event EventHandler? Dismissed;

	public event EventHandler? ActivateMainRequested;

	public event EventHandler? PreviousRequested;

	public event EventHandler? PlayPauseRequested;

	public event EventHandler? NextRequested;

	public event EventHandler? SettingsRequested;

	public event EventHandler? PositionChangedByUser;

	public event Action<int>? OffsetChangeRequested;

	public event Action<bool>? LockChanged;

	public event Action<string>? LyricsDisplayModeChanged;

	public DesktopLyricsWindow(AppState settings)
	{
		_settings = settings;
		InitializeComponent();
		ApplySettings(settings);
		ApplySavedPosition(settings);
	}

	public void ResetPosition()
	{
		Rect workArea = SystemParameters.WorkArea;
		base.Left = Math.Max(workArea.Left + 20.0, workArea.Left + (workArea.Width - base.Width) / 2.0);
		base.Top = Math.Max(workArea.Top + 20.0, workArea.Bottom - base.Height - 70.0);
	}

	private void ApplySavedPosition(AppState settings)
	{
		Rect workArea = SystemParameters.WorkArea;
		double? desktopLyricsLeft = settings.DesktopLyricsLeft;
		double left2;
		if (desktopLyricsLeft.HasValue)
		{
			double left = desktopLyricsLeft.GetValueOrDefault();
			left2 = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - base.Width));
		}
		else
		{
			left2 = Math.Max(workArea.Left + 20.0, (workArea.Width - base.Width) / 2.0);
		}
		base.Left = left2;
		desktopLyricsLeft = settings.DesktopLyricsTop;
		double top2;
		if (desktopLyricsLeft.HasValue)
		{
			double top = desktopLyricsLeft.GetValueOrDefault();
			top2 = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - base.Height));
		}
		else
		{
			top2 = Math.Max(workArea.Top + 20.0, workArea.Bottom - base.Height - 70.0);
		}
		base.Top = top2;
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
		_windowSource?.AddHook(WindowMessageHook);
	}

	protected override void OnClosed(EventArgs e)
	{
		_windowSource?.RemoveHook(WindowMessageHook);
		_windowSource = null;
		base.OnClosed(e);
	}

	public void ApplySettings(AppState settings)
	{
		_settings = settings;
		_interactionActivated = false;
		base.Width = Math.Clamp(settings.DesktopLyricsWidth, base.MinWidth, 1200.0);
		base.Topmost = settings.DesktopLyricsTopmost;
		OriginalText.FontFamily = SafeFontFamily(settings.DesktopLyricsFontFamily);
		TranslationText.FontFamily = OriginalText.FontFamily;
		TertiaryText.FontFamily = OriginalText.FontFamily;
		OriginalText.FontSize = Math.Clamp(settings.DesktopLyricsFontSize, 12.0, 72.0);
		TranslationText.FontSize = Math.Clamp(settings.DesktopLyricsTranslationFontSize, 10.0, 72.0);
		TertiaryText.FontSize = Math.Clamp(settings.DesktopLyricsTranslationFontSize - 2.0, 10.0, 72.0);
		OriginalText.FontWeight = FontWeightFromTag(settings.DesktopLyricsFontWeight, settings.DesktopLyricsBold);
		TranslationText.FontWeight = OriginalText.FontWeight;
		TertiaryText.FontWeight = OriginalText.FontWeight;
		ApplyAlignment(settings.DesktopLyricsAlignment);
		SetLocked(settings.DesktopLyricsLocked, notify: false);
		UpdateOffset(settings.LyricOffsetMs);
		UpdateLyricsModeButton();
		RenderLyrics();
		UpdateHoverVisual();
	}

	public void UpdateLyrics(string original, string translation)
	{
		_currentLine = null;
		_fallbackPrimary = (string.IsNullOrWhiteSpace(original) ? "♪" : original);
		_fallbackSecondary = translation;
		RenderLyrics();
	}

	public void UpdateLyrics(LyricLine line)
	{
		_currentLine = line;
		RenderLyrics();
	}

	private void RenderLyrics()
	{
		LyricsDisplayContent content = _currentLine == null
			? new LyricsDisplayContent
			{
				Primary = _fallbackPrimary,
				PrimaryKind = LyricTextKind.Original,
				Secondary = _fallbackSecondary,
				SecondaryKind = LyricTextKind.Translation
			}
			: LyricsDisplayService.Resolve(_currentLine, _settings.LyricsDisplayMode);
		string primary = string.IsNullOrWhiteSpace(content.Primary) ? "♪" : content.Primary;
		string secondary = _settings.DesktopLyricsShowTranslation ? content.Secondary : "";
		string tertiary = _settings.DesktopLyricsShowTranslation ? content.Tertiary : "";
		if ((!string.IsNullOrWhiteSpace(secondary) || !string.IsNullOrWhiteSpace(tertiary)) &&
			string.Equals(_settings.DesktopLyricsLayout, "SingleLine", StringComparison.OrdinalIgnoreCase))
		{
			OriginalText.Text = string.Join("  ·  ", new[] { primary, secondary, tertiary }.Where(text => !string.IsNullOrWhiteSpace(text)));
			TranslationText.Text = "";
			TranslationText.Visibility = Visibility.Collapsed;
			TertiaryText.Text = "";
			TertiaryText.Visibility = Visibility.Collapsed;
			Grid.SetRowSpan(OriginalText, 3);
		}
		else
		{
			bool showSecondary = !string.IsNullOrWhiteSpace(secondary);
			bool showTertiary = !string.IsNullOrWhiteSpace(tertiary);
			OriginalText.Text = primary;
			TranslationText.Text = secondary;
			TertiaryText.Text = tertiary;
			TranslationText.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
			TertiaryText.Visibility = showTertiary ? Visibility.Visible : Visibility.Collapsed;
			Grid.SetRowSpan(OriginalText, showSecondary || showTertiary ? 1 : 3);
			Grid.SetRowSpan(TranslationText, showTertiary ? 1 : 2);
		}
		ApplyTextStyles(content);
	}

	public void UpdatePlayState(bool isPlaying)
	{
		ToolbarPlayPauseButton.Content = (isPlaying ? "Ⅱ" : "▶");
	}

	public void UpdateOffset(int milliseconds)
	{
		OffsetText.Text = ((milliseconds == 0) ? "" : $"{(double)milliseconds / 1000.0:+0.0;-0.0}s");
	}

	private void ApplyTextStyles(LyricsDisplayContent content)
	{
		ApplyTextStyle(OriginalText, NormalizeKind(content.PrimaryKind), allowOriginalGradient: true);
		ApplyTextStyle(TranslationText, NormalizeKind(content.SecondaryKind), allowOriginalGradient: false);
		ApplyTextStyle(TertiaryText, NormalizeKind(content.TertiaryKind), allowOriginalGradient: false);
	}

	private void ApplyTextStyle(OutlinedTextBlock textBlock, LyricTextKind kind, bool allowOriginalGradient)
	{
		textBlock.Foreground = LyricsStyleService.CreateForeground(_settings, kind, allowOriginalGradient);
		textBlock.Stroke = LyricsStyleService.CreateStroke(_settings);
		textBlock.StrokeThickness = LyricsStyleService.StrokeThickness(_settings, textBlock.FontSize, kind);
	}

	private static LyricTextKind NormalizeKind(LyricTextKind kind)
	{
		return kind == LyricTextKind.None ? LyricTextKind.Original : kind;
	}

	private void ApplyAlignment(string alignment)
	{
		TextAlignment textAlignment = ((!(alignment == "Left")) ? ((alignment == "Right") ? TextAlignment.Right : TextAlignment.Center) : TextAlignment.Left);
		TextAlignment textAlignment2 = textAlignment;
		OriginalText.TextAlignment = textAlignment2;
		TranslationText.TextAlignment = textAlignment2;
		TertiaryText.TextAlignment = textAlignment2;
	}

	private void Window_MouseEnter(object sender, MouseEventArgs e)
	{
		_hovered = true;
		UpdateHoverVisual();
	}

	private void Window_MouseLeave(object sender, MouseEventArgs e)
	{
		_hovered = false;
		_interactionActivated = false;
		UpdateHoverVisual();
	}

	private void UpdateHoverVisual()
	{
		bool num;
		if (!_settings.DesktopLyricsClickToActivate)
		{
			num = _hovered;
		}
		else
		{
			if (!_hovered)
			{
				goto IL_0033;
			}
			num = _interactionActivated;
		}
		if (!num)
		{
			goto IL_0033;
		}
		int num2 = ((!_locked) ? 1 : 0);
		goto IL_0034;
		IL_0033:
		num2 = 0;
		goto IL_0034;
		IL_0034:
		bool showToolbar = (byte)num2 != 0;
		Toolbar.Visibility = ((!showToolbar) ? Visibility.Collapsed : Visibility.Visible);
		if (showToolbar)
		{
			byte alpha = (byte)Math.Clamp(_settings.DesktopLyricsBackgroundOpacity * 255.0, 50.0, 230.0);
			RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 17, 23, 27));
			RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 135, 151, 145));
		}
		else
		{
			RootBorder.Background = (_settings.DesktopLyricsClickToActivate ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
			RootBorder.BorderBrush = Brushes.Transparent;
		}
	}

	private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!_locked && e.ButtonState == MouseButtonState.Pressed)
		{
			if (_settings.DesktopLyricsClickToActivate)
			{
				_interactionActivated = true;
				UpdateHoverVisual();
				e.Handled = true;
			}
			else if (!Toolbar.IsMouseOver)
			{
				DragMove();
			}
		}
	}

	private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
	{
		if (message != 132 || !_settings.DesktopLyricsClickToActivate || _interactionActivated)
		{
			return IntPtr.Zero;
		}
		long value = ((IntPtr)lParam).ToInt64();
		Point screenPoint = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
		if (!_locked && IsPointOverRenderedLyrics(screenPoint))
		{
			return IntPtr.Zero;
		}
		handled = true;
		return HtTransparent;
	}

	private bool IsPointOverRenderedLyrics(Point screenPoint)
	{
		return IsPointOverRenderedText(OriginalText, screenPoint) ||
			IsPointOverRenderedText(TranslationText, screenPoint) ||
			IsPointOverRenderedText(TertiaryText, screenPoint);
	}

	private static bool IsPointOverRenderedText(OutlinedTextBlock text, Point screenPoint)
	{
		if (text.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(text.Text))
		{
			return false;
		}
		try
		{
			return text.ContainsRenderedText(text.PointFromScreen(screenPoint));
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	private void Window_LocationChanged(object? sender, EventArgs e)
	{
		if (base.IsLoaded)
		{
			this.PositionChangedByUser?.Invoke(this, EventArgs.Empty);
		}
	}

	private void SetLocked(bool locked, bool notify = true)
	{
		_locked = locked;
		LockMenuItem.IsChecked = locked;
		LockButton.Content = (locked ? "■" : "□");
		LockButton.ToolTip = (locked ? "解除位置锁定" : "锁定位置");
		UpdateHoverVisual();
		if (notify)
		{
			this.LockChanged?.Invoke(locked);
		}
	}

	private static FontWeight FontWeightFromTag(string? tag, bool legacyBold)
	{
		return tag switch
		{
			"Normal" => FontWeights.Normal,
			"Medium" => FontWeights.Medium,
			"Bold" => FontWeights.Bold,
			"SemiBold" => FontWeights.SemiBold,
			_ => legacyBold ? FontWeights.SemiBold : FontWeights.Normal,
		};
	}

	private static Color ParseColor(string? value, Color fallback)
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

	private void ActivateMainButton_Click(object sender, RoutedEventArgs e)
	{
		this.ActivateMainRequested?.Invoke(this, EventArgs.Empty);
	}

	private void PreviousButton_Click(object sender, RoutedEventArgs e)
	{
		this.PreviousRequested?.Invoke(this, EventArgs.Empty);
	}

	private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
	{
		this.PlayPauseRequested?.Invoke(this, EventArgs.Empty);
	}

	private void NextButton_Click(object sender, RoutedEventArgs e)
	{
		this.NextRequested?.Invoke(this, EventArgs.Empty);
	}

	private void SettingsButton_Click(object sender, RoutedEventArgs e)
	{
		this.SettingsRequested?.Invoke(this, EventArgs.Empty);
	}

	private static FontFamily SafeFontFamily(string? name)
	{
		try
		{
			return new FontFamily(string.IsNullOrWhiteSpace(name) ? "Microsoft YaHei UI" : name);
		}
		catch
		{
			return new FontFamily("Microsoft YaHei UI");
		}
	}

	private void LyricsModeButton_Click(object sender, RoutedEventArgs e)
	{
		SetLyricsDisplayMode(LyricsDisplayModes.NextCommonMode(_settings.LyricsDisplayMode));
	}

	private void LyricsModeMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is MenuItem menuItem)
		{
			SetLyricsDisplayMode(menuItem.Tag?.ToString());
		}
	}

	private void SetLyricsDisplayMode(string? mode)
	{
		_settings.LyricsDisplayMode = LyricsDisplayModes.Normalize(mode);
		UpdateLyricsModeButton();
		RenderLyrics();
		this.LyricsDisplayModeChanged?.Invoke(_settings.LyricsDisplayMode);
	}

	private void UpdateLyricsModeButton()
	{
		LyricsModeButton.Content = LyricsDisplayModes.CompactLabel(_settings.LyricsDisplayMode);
	}

	private void OffsetEarlierButton_Click(object sender, RoutedEventArgs e)
	{
		this.OffsetChangeRequested?.Invoke(-500);
	}

	private void OffsetLaterButton_Click(object sender, RoutedEventArgs e)
	{
		this.OffsetChangeRequested?.Invoke(500);
	}

	private void LockButton_Click(object sender, RoutedEventArgs e)
	{
		SetLocked(!_locked);
	}

	private void LockMenuItem_Click(object sender, RoutedEventArgs e)
	{
		SetLocked(LockMenuItem.IsChecked);
	}

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		Hide();
		this.Dismissed?.Invoke(this, EventArgs.Empty);
	}

}
