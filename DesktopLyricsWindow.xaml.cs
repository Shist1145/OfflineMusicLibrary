using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace OfflineMusicLibrary;

public partial class DesktopLyricsWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private AppState _settings;
    private bool _locked;
    private bool _hovered;
    private bool _interactionActivated;
    private HwndSource? _windowSource;
    private string _currentOriginal = "桌面歌词";
    private string _currentTranslation = "";

    public DesktopLyricsWindow(AppState settings)
    {
        _settings = settings;
        InitializeComponent();
        ApplySettings(settings);
        ApplySavedPosition(settings);
    }

    public void ResetPosition()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 20, workArea.Left + (workArea.Width - Width) / 2);
        Top = Math.Max(workArea.Top + 20, workArea.Bottom - Height - 70);
    }

    private void ApplySavedPosition(AppState settings)
    {
        var workArea = SystemParameters.WorkArea;
        Left = settings.DesktopLyricsLeft is { } left
            ? Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width))
            : Math.Max(workArea.Left + 20, (workArea.Width - Width) / 2);
        Top = settings.DesktopLyricsTop is { } top
            ? Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height))
            : Math.Max(workArea.Top + 20, workArea.Bottom - Height - 70);
    }

    public event EventHandler? Dismissed;
    public event EventHandler? ActivateMainRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? PlayPauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? PositionChangedByUser;
    public event Action<int>? OffsetChangeRequested;
    public event Action<bool>? LockChanged;

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
        Width = Math.Clamp(settings.DesktopLyricsWidth, MinWidth, 1200);
        Topmost = settings.DesktopLyricsTopmost;
        OriginalText.FontFamily = new FontFamily(settings.DesktopLyricsFontFamily);
        TranslationText.FontFamily = OriginalText.FontFamily;
        OriginalText.FontSize = Math.Clamp(settings.DesktopLyricsFontSize, 12, 72);
        TranslationText.FontSize = Math.Clamp(settings.DesktopLyricsTranslationFontSize, 10, 72);
        OriginalText.FontWeight = FontWeightFromTag(settings.DesktopLyricsFontWeight, settings.DesktopLyricsBold);
        TranslationText.FontWeight = OriginalText.FontWeight;
        ApplyColors(settings);
        ApplyAlignment(settings.DesktopLyricsAlignment);
        var strokeEnabled = settings.DesktopLyricsStroke;
        var stroke = strokeEnabled
            ? new SolidColorBrush(ParseColor(settings.DesktopLyricsStrokeColor, Colors.Black))
            : Brushes.Transparent;
        OriginalText.Stroke = stroke;
        TranslationText.Stroke = stroke;
        OriginalText.StrokeThickness = strokeEnabled ? Math.Clamp(OriginalText.FontSize * 0.075, 1.4, 3.2) : 0;
        TranslationText.StrokeThickness = strokeEnabled ? Math.Clamp(TranslationText.FontSize * 0.075, 1.1, 2.4) : 0;
        SetLocked(settings.DesktopLyricsLocked, notify: false);
        UpdateOffset(settings.LyricOffsetMs);
        RenderLyrics();
        UpdateHoverVisual();
    }

    public void UpdateLyrics(string original, string translation)
    {
        _currentOriginal = string.IsNullOrWhiteSpace(original) ? "♪" : original;
        _currentTranslation = translation;
        RenderLyrics();
    }

    private void RenderLyrics()
    {
        var showTranslation = _settings.DesktopLyricsShowTranslation && !string.IsNullOrWhiteSpace(_currentTranslation);
        if (showTranslation && string.Equals(_settings.DesktopLyricsLayout, "SingleLine", StringComparison.OrdinalIgnoreCase))
        {
            OriginalText.Text = $"{_currentOriginal}  ·  {_currentTranslation}";
            TranslationText.Text = "";
            TranslationText.Visibility = Visibility.Collapsed;
            Grid.SetRowSpan(OriginalText, 2);
        }
        else
        {
            OriginalText.Text = _currentOriginal;
            TranslationText.Text = _currentTranslation;
            TranslationText.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRowSpan(OriginalText, showTranslation ? 1 : 2);
        }
    }

    public void UpdatePlayState(bool isPlaying) => ToolbarPlayPauseButton.Content = isPlaying ? "Ⅱ" : "▶";

    public void UpdateOffset(int milliseconds)
    {
        OffsetText.Text = milliseconds == 0 ? "" : $"{milliseconds / 1000d:+0.0;-0.0}s";
    }

    private void ApplyColors(AppState settings)
    {
        var (primary, secondary) = settings.DesktopLyricsColorScheme switch
        {
            "Classic" => (ParseColor("#F4F7F6", Colors.White), ParseColor("#C7D2CE", Colors.LightGray)),
            "HighContrast" => (ParseColor("#FFE16B", Colors.Yellow), Colors.White),
            "Custom" => (ParseColor(settings.DesktopLyricsPrimaryColor, Colors.White),
                ParseColor(settings.DesktopLyricsSecondaryColor, Colors.LightGreen)),
            _ => (ParseColor("#C9B7FF", Colors.White), ParseColor("#79D9A9", Colors.LightGreen))
        };
        OriginalText.Foreground = settings.DesktopLyricsUseGradient
            ? new LinearGradientBrush(primary, secondary, 90)
            : new SolidColorBrush(primary);
        TranslationText.Foreground = new SolidColorBrush(secondary);
    }

    private void ApplyAlignment(string alignment)
    {
        var textAlignment = alignment switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        OriginalText.TextAlignment = textAlignment;
        TranslationText.TextAlignment = textAlignment;
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
        var interactionVisible = _settings.DesktopLyricsClickToActivate
            ? _hovered && _interactionActivated
            : _hovered;
        var showToolbar = interactionVisible && !_locked;
        Toolbar.Visibility = showToolbar ? Visibility.Visible : Visibility.Collapsed;
        if (showToolbar)
        {
            var alpha = (byte)Math.Clamp(_settings.DesktopLyricsBackgroundOpacity * 255, 50, 230);
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 17, 23, 27));
            RootBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 135, 151, 145));
        }
        else
        {
            RootBorder.Background = _settings.DesktopLyricsClickToActivate
                ? Brushes.Transparent
                : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            RootBorder.BorderBrush = Brushes.Transparent;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked || e.ButtonState != MouseButtonState.Pressed)
            return;

        if (_settings.DesktopLyricsClickToActivate)
        {
            _interactionActivated = true;
            UpdateHoverVisual();
            e.Handled = true;
            return;
        }

        if (!Toolbar.IsMouseOver)
            DragMove();
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest || !_settings.DesktopLyricsClickToActivate || _interactionActivated)
            return IntPtr.Zero;

        var value = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(value & 0xFFFF)),
            unchecked((short)((value >> 16) & 0xFFFF)));
        if (!_locked && IsPointOverRenderedLyrics(screenPoint))
            return IntPtr.Zero;

        handled = true;
        return HtTransparent;
    }

    private bool IsPointOverRenderedLyrics(Point screenPoint) =>
        IsPointOverRenderedText(OriginalText, screenPoint) ||
        IsPointOverRenderedText(TranslationText, screenPoint);

    private static bool IsPointOverRenderedText(OutlinedTextBlock text, Point screenPoint)
    {
        if (text.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(text.Text))
            return false;
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
        if (IsLoaded)
            PositionChangedByUser?.Invoke(this, EventArgs.Empty);
    }

    private void SetLocked(bool locked, bool notify = true)
    {
        _locked = locked;
        LockMenuItem.IsChecked = locked;
        LockButton.Content = locked ? "■" : "□";
        LockButton.ToolTip = locked ? "解除位置锁定" : "锁定位置";
        UpdateHoverVisual();
        if (notify)
            LockChanged?.Invoke(locked);
    }

    private static FontWeight FontWeightFromTag(string? tag, bool legacyBold) => tag switch
    {
        "Normal" => FontWeights.Normal,
        "Medium" => FontWeights.Medium,
        "Bold" => FontWeights.Bold,
        "SemiBold" => FontWeights.SemiBold,
        _ => legacyBold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static Color ParseColor(string? value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value ?? ""); }
        catch { return fallback; }
    }

    private void ActivateMainButton_Click(object sender, RoutedEventArgs e) => ActivateMainRequested?.Invoke(this, EventArgs.Empty);
    private void PreviousButton_Click(object sender, RoutedEventArgs e) => PreviousRequested?.Invoke(this, EventArgs.Empty);
    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
    private void NextButton_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OffsetEarlierButton_Click(object sender, RoutedEventArgs e) => OffsetChangeRequested?.Invoke(-500);
    private void OffsetLaterButton_Click(object sender, RoutedEventArgs e) => OffsetChangeRequested?.Invoke(500);
    private void LockButton_Click(object sender, RoutedEventArgs e) => SetLocked(!_locked);
    private void LockMenuItem_Click(object sender, RoutedEventArgs e) => SetLocked(LockMenuItem.IsChecked);

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
