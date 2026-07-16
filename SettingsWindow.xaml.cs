using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using DrawingColor = System.Drawing.Color;
using FormsColorDialog = System.Windows.Forms.ColorDialog;
using FormsDialogResult = System.Windows.Forms.DialogResult;

namespace OfflineMusicLibrary;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _libraryFolders;
    private bool _resetLyricsPosition;
    private bool _initialized;
    private string _primaryColor;
    private string _secondaryColor;
    private string _strokeColor;

    public SettingsWindow(AppState state)
    {
        InitializeComponent();
        FontFamily = SafeFontFamily(state.UiFontFamily);
        _libraryFolders = new ObservableCollection<string>(state.LibraryFolders);
        LibraryFoldersList.ItemsSource = _libraryFolders;

        var fonts = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        UiFontCombo.ItemsSource = fonts;
        LyricsFontCombo.ItemsSource = fonts;
        UiFontCombo.Text = state.UiFontFamily;
        LyricsFontCombo.Text = state.DesktopLyricsFontFamily;

        AutoCloseHoursCombo.ItemsSource = Enumerable.Range(0, 25).ToList();
        AutoCloseMinutesCombo.ItemsSource = Enumerable.Range(0, 60).ToList();
        AutoCloseHoursCombo.SelectedItem = Math.Clamp(state.AutoCloseMinutes / 60, 0, 24);
        AutoCloseMinutesCombo.SelectedItem = Math.Clamp(state.AutoCloseMinutes % 60, 0, 59);

        DefaultVolumeSlider.Value = state.Volume;
        RunAtStartupCheckBox.IsChecked = state.RunAtStartup;
        StartMinimizedCheckBox.IsChecked = state.StartMinimized;
        AutoCloseCheckBox.IsChecked = state.AutoCloseEnabled;
        AutoPlayOnStartupCheckBox.IsChecked = state.AutoPlayOnStartup;
        RememberPlaybackProgressCheckBox.IsChecked = state.RememberPlaybackProgress;
        GlobalHotkeysCheckBox.IsChecked = state.GlobalHotkeysEnabled;
        SystemMediaKeysCheckBox.IsChecked = state.SystemMediaKeysEnabled;
        ScanOnStartupCheckBox.IsChecked = state.ScanOnStartup;
        LyricsTopmostCheckBox.IsChecked = state.DesktopLyricsTopmost;
        ShowTranslationCheckBox.IsChecked = state.DesktopLyricsShowTranslation;
        LyricsStrokeCheckBox.IsChecked = state.DesktopLyricsStroke;
        LyricsLockedCheckBox.IsChecked = state.DesktopLyricsLocked;
        LyricsClickToActivateCheckBox.IsChecked = state.DesktopLyricsClickToActivate;
        LyricsGradientCheckBox.IsChecked = state.DesktopLyricsUseGradient;
        LyricsFontSizeSlider.Value = state.DesktopLyricsFontSize;
        LyricsTranslationFontSizeSlider.Value = state.DesktopLyricsTranslationFontSize;
        LyricsOpacitySlider.Value = state.DesktopLyricsBackgroundOpacity;
        LyricsWidthSlider.Value = state.DesktopLyricsWidth;
        LyricsOffsetSlider.Value = state.LyricOffsetMs / 1000d;
        SelectByTag(CloseBehaviorCombo, state.CloseBehavior);
        SelectByTag(DoubleClickQueueModeCombo, state.DoubleClickQueueMode);
        SelectByTag(LyricsFontWeightCombo,
            string.IsNullOrWhiteSpace(state.DesktopLyricsFontWeight)
                ? state.DesktopLyricsBold ? "SemiBold" : "Normal"
                : state.DesktopLyricsFontWeight);
        SelectByTag(LyricsLayoutCombo, state.DesktopLyricsLayout);
        SelectByTag(LyricsAlignmentCombo, state.DesktopLyricsAlignment);
        SelectByTag(LyricsColorCombo, state.DesktopLyricsColorScheme);
        SelectByTag(AudioBackendCombo, state.AudioBackend);
        SelectByTag(HardwareDecodingCombo, state.HardwareDecoding);
        SelectByTag(VideoOutputCombo, state.VideoOutput);
        SelectByTag(VisualizationModeCombo, state.VisualizationMode);
        InPlayerSubtitlesCheckBox.IsChecked = state.InPlayerBilingualSubtitles;

        _primaryColor = NormalizeColor(state.DesktopLyricsPrimaryColor, "#C9B7FF");
        _secondaryColor = NormalizeColor(state.DesktopLyricsSecondaryColor, "#79D9A9");
        _strokeColor = NormalizeColor(state.DesktopLyricsStrokeColor, "#000000");
        UpdateColorButtons();
        _initialized = true;
        UpdateLyricsPreview();
    }

    public bool RescanRequested { get; private set; }

    public void ApplyTo(AppState state)
    {
        state.Volume = (int)DefaultVolumeSlider.Value;
        state.UiFontFamily = SelectedFont(UiFontCombo, "Microsoft YaHei UI");
        state.RunAtStartup = RunAtStartupCheckBox.IsChecked == true;
        state.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
        state.CloseBehavior = SelectedTag(CloseBehaviorCombo, "Exit");
        state.AutoCloseEnabled = AutoCloseCheckBox.IsChecked == true;
        state.AutoCloseMinutes = SelectedInt(AutoCloseHoursCombo) * 60 + SelectedInt(AutoCloseMinutesCombo);
        state.AutoPlayOnStartup = AutoPlayOnStartupCheckBox.IsChecked == true;
        state.RememberPlaybackProgress = RememberPlaybackProgressCheckBox.IsChecked == true;
        state.DoubleClickQueueMode = SelectedTag(DoubleClickQueueModeCombo, "Replace");
        state.GlobalHotkeysEnabled = GlobalHotkeysCheckBox.IsChecked == true;
        state.SystemMediaKeysEnabled = SystemMediaKeysCheckBox.IsChecked == true;
        state.ScanOnStartup = ScanOnStartupCheckBox.IsChecked == true;
        state.DesktopLyricsTopmost = LyricsTopmostCheckBox.IsChecked == true;
        state.DesktopLyricsShowTranslation = ShowTranslationCheckBox.IsChecked == true;
        state.DesktopLyricsStroke = LyricsStrokeCheckBox.IsChecked == true;
        state.DesktopLyricsLocked = LyricsLockedCheckBox.IsChecked == true;
        state.DesktopLyricsClickToActivate = LyricsClickToActivateCheckBox.IsChecked == true;
        state.DesktopLyricsFontFamily = SelectedFont(LyricsFontCombo, "Microsoft YaHei UI");
        state.DesktopLyricsFontWeight = SelectedTag(LyricsFontWeightCombo, "SemiBold");
        state.DesktopLyricsBold = state.DesktopLyricsFontWeight is "SemiBold" or "Bold";
        state.DesktopLyricsFontSize = LyricsFontSizeSlider.Value;
        state.DesktopLyricsTranslationFontSize = LyricsTranslationFontSizeSlider.Value;
        state.DesktopLyricsBackgroundOpacity = LyricsOpacitySlider.Value;
        state.DesktopLyricsWidth = LyricsWidthSlider.Value;
        state.LyricOffsetMs = (int)(LyricsOffsetSlider.Value * 1000);
        state.DesktopLyricsLayout = SelectedTag(LyricsLayoutCombo, "Stacked");
        state.DesktopLyricsAlignment = SelectedTag(LyricsAlignmentCombo, "Center");
        state.DesktopLyricsColorScheme = SelectedTag(LyricsColorCombo, "MintPurple");
        state.DesktopLyricsPrimaryColor = _primaryColor;
        state.DesktopLyricsSecondaryColor = _secondaryColor;
        state.DesktopLyricsStrokeColor = _strokeColor;
        state.DesktopLyricsUseGradient = LyricsGradientCheckBox.IsChecked == true;
        state.AudioBackend = SelectedTag(AudioBackendCombo, "DirectSound");
        state.HardwareDecoding = SelectedTag(HardwareDecodingCombo, "Auto");
        state.VideoOutput = SelectedTag(VideoOutputCombo, "Auto");
        state.VisualizationMode = SelectedTag(VisualizationModeCombo, "Off");
        state.InPlayerBilingualSubtitles = InPlayerSubtitlesCheckBox.IsChecked == true;
        state.LibraryFolders = _libraryFolders.ToList();
        if (_resetLyricsPosition)
        {
            state.DesktopLyricsLeft = null;
            state.DesktopLyricsTop = null;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void SaveAndRescanButton_Click(object sender, RoutedEventArgs e)
    {
        RescanRequested = true;
        DialogResult = true;
    }

    private void AddLibraryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "添加音乐文件夹" };
        if (dialog.ShowDialog(this) == true && !_libraryFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            _libraryFolders.Add(dialog.FolderName);
    }

    private void RemoveLibraryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryFoldersList.SelectedItem is string folder)
            _libraryFolders.Remove(folder);
    }

    private void ResetLyricsPositionButton_Click(object sender, RoutedEventArgs e) => _resetLyricsPosition = true;

    private void DefaultVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DefaultVolumeText is not null)
            DefaultVolumeText.Text = $"{e.NewValue:0}%";
    }

    private void LyricsFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LyricsFontSizeText is not null)
            LyricsFontSizeText.Text = $"{e.NewValue:0}";
        UpdateLyricsPreview();
    }

    private void LyricsTranslationFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LyricsTranslationFontSizeText is not null)
            LyricsTranslationFontSizeText.Text = $"{e.NewValue:0}";
        UpdateLyricsPreview();
    }

    private void LyricsOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LyricsOpacityText is not null)
            LyricsOpacityText.Text = $"{e.NewValue:P0}";
    }

    private void LyricsWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LyricsWidthText is not null)
            LyricsWidthText.Text = $"{e.NewValue:0} px";
    }

    private void LyricsOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LyricsOffsetText is not null)
            LyricsOffsetText.Text = e.NewValue == 0 ? "0.0 s" : $"{e.NewValue:+0.0;-0.0} s";
    }

    private void LyricsPreviewSetting_Changed(object sender, RoutedEventArgs e) => UpdateLyricsPreview();

    private void PrimaryColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseColor(_primaryColor) is { } color)
            _primaryColor = color;
        SelectByTag(LyricsColorCombo, "Custom");
        UpdateColorButtons();
        UpdateLyricsPreview();
    }

    private void SecondaryColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseColor(_secondaryColor) is { } color)
            _secondaryColor = color;
        SelectByTag(LyricsColorCombo, "Custom");
        UpdateColorButtons();
        UpdateLyricsPreview();
    }

    private void StrokeColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseColor(_strokeColor) is { } color)
            _strokeColor = color;
        UpdateColorButtons();
        UpdateLyricsPreview();
    }

    private string? ChooseColor(string initial)
    {
        var color = ParseColor(initial, Colors.White);
        using var dialog = new FormsColorDialog
        {
            FullOpen = true,
            Color = DrawingColor.FromArgb(color.A, color.R, color.G, color.B)
        };
        return dialog.ShowDialog() == FormsDialogResult.OK
            ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"
            : null;
    }

    private void UpdateColorButtons()
    {
        SetColorButton(PrimaryColorButton, _primaryColor, "主色");
        SetColorButton(SecondaryColorButton, _secondaryColor, "副色");
        SetColorButton(StrokeColorButton, _strokeColor, "描边色");
    }

    private static void SetColorButton(Button button, string hex, string label)
    {
        var color = ParseColor(hex, Colors.White);
        button.Background = new SolidColorBrush(color);
        var luminance = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
        button.Foreground = luminance > 150 ? Brushes.Black : Brushes.White;
        button.Content = $"{label}  {hex}";
    }

    private void UpdateLyricsPreview()
    {
        if (!_initialized || LyricsPreviewOriginal is null)
            return;

        var font = SafeFontFamily(SelectedFont(LyricsFontCombo, "Microsoft YaHei UI"));
        LyricsPreviewOriginal.FontFamily = font;
        LyricsPreviewTranslation.FontFamily = font;
        LyricsPreviewOriginal.FontSize = Math.Clamp(LyricsFontSizeSlider.Value, 12, 72);
        LyricsPreviewTranslation.FontSize = Math.Clamp(LyricsTranslationFontSizeSlider.Value, 10, 72);
        var weight = FontWeightFromTag(SelectedTag(LyricsFontWeightCombo, "SemiBold"));
        LyricsPreviewOriginal.FontWeight = weight;
        LyricsPreviewTranslation.FontWeight = weight;

        var scheme = SelectedTag(LyricsColorCombo, "MintPurple");
        var (primary, secondary) = SchemeColors(scheme, _primaryColor, _secondaryColor);
        LyricsPreviewOriginal.Foreground = LyricsGradientCheckBox.IsChecked == true
            ? new LinearGradientBrush(primary, secondary, 90)
            : new SolidColorBrush(primary);
        LyricsPreviewTranslation.Foreground = new SolidColorBrush(secondary);
        var strokeEnabled = LyricsStrokeCheckBox.IsChecked == true;
        var stroke = strokeEnabled
            ? new SolidColorBrush(ParseColor(_strokeColor, Colors.Black))
            : Brushes.Transparent;
        LyricsPreviewOriginal.Stroke = stroke;
        LyricsPreviewTranslation.Stroke = stroke;
        LyricsPreviewOriginal.StrokeThickness = strokeEnabled ? Math.Clamp(LyricsPreviewOriginal.FontSize * 0.075, 1.4, 3.2) : 0;
        LyricsPreviewTranslation.StrokeThickness = strokeEnabled ? Math.Clamp(LyricsPreviewTranslation.FontSize * 0.075, 1.1, 2.4) : 0;

        var alignment = SelectedTag(LyricsAlignmentCombo, "Center") switch
        {
            "Left" => TextAlignment.Left,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Center
        };
        LyricsPreviewOriginal.TextAlignment = alignment;
        LyricsPreviewTranslation.TextAlignment = alignment;

        var showTranslation = ShowTranslationCheckBox.IsChecked == true;
        if (SelectedTag(LyricsLayoutCombo, "Stacked") == "SingleLine" && showTranslation)
        {
            LyricsPreviewOriginal.Text = "夜空中的旋律正在流动  ·  The melody is flowing through the night";
            LyricsPreviewTranslation.Visibility = Visibility.Collapsed;
        }
        else
        {
            LyricsPreviewOriginal.Text = "夜空中的旋律正在流动";
            LyricsPreviewTranslation.Text = "The melody is flowing through the night";
            LyricsPreviewTranslation.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static (Color Primary, Color Secondary) SchemeColors(string scheme, string customPrimary, string customSecondary) =>
        scheme switch
        {
            "Classic" => (ParseColor("#F4F7F6", Colors.White), ParseColor("#C7D2CE", Colors.LightGray)),
            "HighContrast" => (ParseColor("#FFE16B", Colors.Yellow), Colors.White),
            "Custom" => (ParseColor(customPrimary, Colors.White), ParseColor(customSecondary, Colors.LightGray)),
            _ => (ParseColor("#C9B7FF", Colors.White), ParseColor("#79D9A9", Colors.LightGreen))
        };

    private static FontWeight FontWeightFromTag(string tag) => tag switch
    {
        "Normal" => FontWeights.Normal,
        "Medium" => FontWeights.Medium,
        "Bold" => FontWeights.Bold,
        _ => FontWeights.SemiBold
    };

    private static FontFamily SafeFontFamily(string? name)
    {
        try { return new FontFamily(string.IsNullOrWhiteSpace(name) ? "Microsoft YaHei UI" : name); }
        catch { return new FontFamily("Microsoft YaHei UI"); }
    }

    private static string SelectedFont(ComboBox comboBox, string fallback) =>
        string.IsNullOrWhiteSpace(comboBox.Text) ? fallback : comboBox.Text.Trim();

    private static int SelectedInt(ComboBox comboBox) => comboBox.SelectedItem is int value ? value : 0;

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedIndex = Math.Max(0, comboBox.SelectedIndex);
    }

    private static string SelectedTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static string NormalizeColor(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        try
        {
            _ = (Color)ColorConverter.ConvertFromString(normalized);
            return normalized.StartsWith('#') ? normalized.ToUpperInvariant() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }
}
