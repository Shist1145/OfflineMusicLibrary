using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace OfflineMusicLibrary;

public partial class SettingsWindow : Window, IComponentConnector
{
	private readonly AppState _sourceState;

	private readonly ObservableCollection<string> _libraryFolders;

	private bool _resetLyricsPosition;

	private bool _initialized;

	private string _primaryColor;

	private string _secondaryColor;

	private string _romanizationColor;

	private string _translationColor;

	private string _strokeColor;

	private string _customAccentColor = "#147D6A";

	public bool RescanRequested { get; private set; }

	public SettingsWindow(AppState state)
	{
		_sourceState = state;
		InitializeComponent();
		base.Closed += SettingsWindow_Closed;
		base.FontFamily = SafeFontFamily(state.UiFontFamily);
		_libraryFolders = new ObservableCollection<string>(state.LibraryFolders);
		LibraryFoldersList.ItemsSource = _libraryFolders;
		List<string> fonts = (from font in Fonts.SystemFontFamilies
			select font.Source into name
			where !string.IsNullOrWhiteSpace(name)
			select name).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).OrderBy<string, string>((string name) => name, StringComparer.CurrentCultureIgnoreCase).ToList();
		UiFontCombo.ItemsSource = fonts;
		LyricsFontCombo.ItemsSource = fonts;
		UiFontCombo.Text = state.UiFontFamily;
		LyricsFontCombo.Text = state.DesktopLyricsFontFamily;
		SelectByTag(UiThemeCombo, string.IsNullOrWhiteSpace(state.UiTheme) ? "Mint" : state.UiTheme);
		_customAccentColor = NormalizeColor(state.CustomAccentColor, "#147D6A");
		UiSurfaceOpacitySlider.Value = Math.Clamp(state.UiSurfaceOpacity, 0.72, 1.0);
		BackgroundImagePathTextBox.Text = state.BackgroundImagePath ?? "";
		AppTitleTextBox.Text = string.IsNullOrWhiteSpace(state.AppTitleText) ? "本地音乐库" : state.AppTitleText;
		SelectByTag(StartPageCombo, string.IsNullOrWhiteSpace(state.StartPage) ? "Discover" : state.StartPage);
		ShowRecommendationsCheckBox.IsChecked = state.ShowRecommendationsInSidebar;
		ShowCirclesCheckBox.IsChecked = state.ShowCirclesInSidebar;
		ShowRecentCheckBox.IsChecked = state.ShowRecentInSidebar;
		ShowHistoryCheckBox.IsChecked = state.ShowHistoryInSidebar;
		ShowCategoriesCheckBox.IsChecked = state.ShowCategoriesInSidebar;
		UpdateCustomAccentButton();
		AutoCloseHoursCombo.ItemsSource = Enumerable.Range(0, 25).ToList();
		AutoCloseMinutesCombo.ItemsSource = Enumerable.Range(0, 60).ToList();
		AutoCloseHoursCombo.SelectedItem = Math.Clamp(state.AutoCloseMinutes / 60, 0, 24);
		AutoCloseMinutesCombo.SelectedItem = Math.Clamp(state.AutoCloseMinutes % 60, 0, 59);
		DefaultVolumeSlider.Value = state.Volume;
		RunAtStartupCheckBox.IsChecked = state.RunAtStartup;
		StartMinimizedCheckBox.IsChecked = state.StartMinimized;
		FloatingMiniPlayerCheckBox.IsChecked = state.FloatingMiniPlayerEnabled;
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
		LyricsOffsetSlider.Value = (double)state.LyricOffsetMs / 1000.0;
		SelectByTag(CloseBehaviorCombo, state.CloseBehavior);
		SelectByTag(DoubleClickQueueModeCombo, state.DoubleClickQueueMode);
		SelectByTag(LyricsFontWeightCombo, (!string.IsNullOrWhiteSpace(state.DesktopLyricsFontWeight)) ? state.DesktopLyricsFontWeight : (state.DesktopLyricsBold ? "SemiBold" : "Normal"));
		SelectByTag(LyricsLayoutCombo, state.DesktopLyricsLayout);
		SelectByTag(LyricsAlignmentCombo, state.DesktopLyricsAlignment);
		SelectByTag(LyricsColorCombo, state.DesktopLyricsColorScheme);
		SelectByTag(AudioBackendCombo, state.AudioBackend);
		SelectByTag(HardwareDecodingCombo, state.HardwareDecoding);
		SelectByTag(VideoOutputCombo, state.VideoOutput);
		SelectByTag(VisualizationModeCombo, state.VisualizationMode);
		SelectByTag(EqualizerPresetCombo, AudioEffectPresets.NormalizeEqualizer(state.EqualizerPreset));
		SelectByTag(SpatialAudioCombo, AudioEffectPresets.NormalizeSpatialAudio(state.SpatialAudioMode));
		InPlayerSubtitlesCheckBox.IsChecked = state.InPlayerBilingualSubtitles;
		PlaybackRecoveryCheckBox.IsChecked = state.PlaybackRecoveryEnabled;
		PlaybackWatchdogTimeoutSlider.Value = Math.Clamp(state.PlaybackWatchdogTimeoutSeconds, 8, 30);
		PlaybackRecoveryAttemptsCombo.ItemsSource = Enumerable.Range(1, 5).ToList();
		PlaybackRecoveryAttemptsCombo.SelectedItem = Math.Clamp(state.PlaybackRecoveryAttempts, 1, 5);
		SkipFailedTrackCheckBox.IsChecked = state.SkipTrackAfterRecoveryFailure;
		SafePlaybackModeCheckBox.IsChecked = state.SafePlaybackMode;
		StateBackupCheckBox.IsChecked = state.StateBackupEnabled;
		_primaryColor = NormalizeColor(state.DesktopLyricsPrimaryColor, "#C9B7FF");
		_secondaryColor = NormalizeColor(state.DesktopLyricsSecondaryColor, "#79D9A9");
		_romanizationColor = NormalizeColor(state.DesktopLyricsRomanizationColor, _secondaryColor);
		_translationColor = NormalizeColor(state.DesktopLyricsTranslationColor, _primaryColor);
		_strokeColor = NormalizeColor(state.DesktopLyricsStrokeColor, "#000000");
		LyricsOriginalOpacitySlider.Value = Math.Clamp(state.DesktopLyricsOriginalOpacity, 0.35, 1.0);
		LyricsRomanizationOpacitySlider.Value = Math.Clamp(state.DesktopLyricsRomanizationOpacity, 0.35, 1.0);
		LyricsTranslationOpacitySlider.Value = Math.Clamp(state.DesktopLyricsTranslationOpacity, 0.35, 1.0);
		LyricsStrokeScaleSlider.Value = Math.Clamp(state.DesktopLyricsStrokeScale, 0.5, 2.0);
		InPlayerLyricsStyleCheckBox.IsChecked = state.InPlayerSubtitlesUseLyricsStyle;
		UpdateColorButtons();
		_initialized = true;
		UpdateLyricsPreview();
	}

	public void ApplyTo(AppState state)
	{
		state.Volume = (int)DefaultVolumeSlider.Value;
		state.UiFontFamily = SelectedFont(UiFontCombo, "Microsoft YaHei UI");
		state.UiTheme = SelectedTag(UiThemeCombo, "Mint");
		state.CustomAccentColor = _customAccentColor;
		state.UiSurfaceOpacity = Math.Clamp(UiSurfaceOpacitySlider.Value, 0.72, 1.0);
		state.BackgroundImagePath = BackgroundImagePathTextBox.Text?.Trim() ?? "";
		state.AppTitleText = string.IsNullOrWhiteSpace(AppTitleTextBox.Text) ? "本地音乐库" : AppTitleTextBox.Text.Trim();
		state.StartPage = SelectedTag(StartPageCombo, "Discover");
		state.ShowRecommendationsInSidebar = ShowRecommendationsCheckBox.IsChecked == true;
		state.ShowCirclesInSidebar = ShowCirclesCheckBox.IsChecked == true;
		state.ShowRecentInSidebar = ShowRecentCheckBox.IsChecked == true;
		state.ShowHistoryInSidebar = ShowHistoryCheckBox.IsChecked == true;
		state.ShowCategoriesInSidebar = ShowCategoriesCheckBox.IsChecked == true;
		state.RunAtStartup = RunAtStartupCheckBox.IsChecked == true;
		state.StartMinimized = StartMinimizedCheckBox.IsChecked == true;
		state.FloatingMiniPlayerEnabled = FloatingMiniPlayerCheckBox.IsChecked == true;
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
		string desktopLyricsFontWeight = state.DesktopLyricsFontWeight;
		bool desktopLyricsBold = ((desktopLyricsFontWeight == "SemiBold" || desktopLyricsFontWeight == "Bold") ? true : false);
		state.DesktopLyricsBold = desktopLyricsBold;
		state.DesktopLyricsFontSize = LyricsFontSizeSlider.Value;
		state.DesktopLyricsTranslationFontSize = LyricsTranslationFontSizeSlider.Value;
		state.DesktopLyricsBackgroundOpacity = LyricsOpacitySlider.Value;
		state.DesktopLyricsWidth = LyricsWidthSlider.Value;
		state.LyricOffsetMs = (int)(LyricsOffsetSlider.Value * 1000.0);
		state.DesktopLyricsLayout = SelectedTag(LyricsLayoutCombo, "Stacked");
		state.DesktopLyricsAlignment = SelectedTag(LyricsAlignmentCombo, "Center");
		state.DesktopLyricsColorScheme = SelectedTag(LyricsColorCombo, "MintPurple");
		state.DesktopLyricsPrimaryColor = _primaryColor;
		state.DesktopLyricsSecondaryColor = _secondaryColor;
		state.DesktopLyricsRomanizationColor = _romanizationColor;
		state.DesktopLyricsTranslationColor = _translationColor;
		state.DesktopLyricsStrokeColor = _strokeColor;
		state.DesktopLyricsOriginalOpacity = Math.Clamp(LyricsOriginalOpacitySlider.Value, 0.35, 1.0);
		state.DesktopLyricsRomanizationOpacity = Math.Clamp(LyricsRomanizationOpacitySlider.Value, 0.35, 1.0);
		state.DesktopLyricsTranslationOpacity = Math.Clamp(LyricsTranslationOpacitySlider.Value, 0.35, 1.0);
		state.DesktopLyricsStrokeScale = Math.Clamp(LyricsStrokeScaleSlider.Value, 0.5, 2.0);
		state.InPlayerSubtitlesUseLyricsStyle = InPlayerLyricsStyleCheckBox.IsChecked == true;
		state.DesktopLyricsUseGradient = LyricsGradientCheckBox.IsChecked == true;
		state.AudioBackend = SelectedTag(AudioBackendCombo, "DirectSound");
		state.HardwareDecoding = SelectedTag(HardwareDecodingCombo, "Auto");
		state.VideoOutput = SelectedTag(VideoOutputCombo, "Auto");
		state.VisualizationMode = SelectedTag(VisualizationModeCombo, "Off");
		state.EqualizerPreset = AudioEffectPresets.NormalizeEqualizer(SelectedTag(EqualizerPresetCombo, "Off"));
		state.SpatialAudioMode = AudioEffectPresets.NormalizeSpatialAudio(SelectedTag(SpatialAudioCombo, "Off"));
		state.InPlayerBilingualSubtitles = InPlayerSubtitlesCheckBox.IsChecked == true;
		state.PlaybackRecoveryEnabled = PlaybackRecoveryCheckBox.IsChecked == true;
		state.PlaybackWatchdogTimeoutSeconds = (int)Math.Round(Math.Clamp(PlaybackWatchdogTimeoutSlider.Value, 8.0, 30.0));
		state.PlaybackRecoveryAttempts = Math.Clamp(SelectedInt(PlaybackRecoveryAttemptsCombo), 1, 5);
		state.SkipTrackAfterRecoveryFailure = SkipFailedTrackCheckBox.IsChecked == true;
		state.SafePlaybackMode = SafePlaybackModeCheckBox.IsChecked == true;
		state.StateBackupEnabled = StateBackupCheckBox.IsChecked == true;
		state.LibraryFolders = _libraryFolders.ToList();
		if (_resetLyricsPosition)
		{
			state.DesktopLyricsLeft = null;
			state.DesktopLyricsTop = null;
		}
	}

	private void SaveButton_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = true;
	}

	private void SaveAndRescanButton_Click(object sender, RoutedEventArgs e)
	{
		RescanRequested = true;
		base.DialogResult = true;
	}

	private void AddLibraryFolderButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog dialog = new OpenFolderDialog
		{
			Title = "添加音乐文件夹"
		};
		if (dialog.ShowDialog(this) == true && !_libraryFolders.Contains<string>(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
		{
			_libraryFolders.Add(dialog.FolderName);
		}
	}

	private void RemoveLibraryFolderButton_Click(object sender, RoutedEventArgs e)
	{
		if (LibraryFoldersList.SelectedItem is string folder)
		{
			_libraryFolders.Remove(folder);
		}
	}

	private void ResetLyricsPositionButton_Click(object sender, RoutedEventArgs e)
	{
		_resetLyricsPosition = true;
	}

	private void CustomAccentButton_Click(object sender, RoutedEventArgs e)
	{
		string? color = ChooseColor(_customAccentColor);
		if (color == null)
		{
			return;
		}

		_customAccentColor = color;
		SelectByTag(UiThemeCombo, "Custom");
		UpdateCustomAccentButton();
		PreviewTheme();
	}

	private void UiThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		PreviewTheme();
	}

	private void UiSurfaceOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (UiSurfaceOpacityText != null)
		{
			UiSurfaceOpacityText.Text = $"{e.NewValue:P0}";
		}
		PreviewTheme();
	}

	private void ChooseBackgroundImageButton_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog dialog = new()
		{
			Title = "选择个性化背景图片",
			Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.webp|所有文件|*.*",
			CheckFileExists = true,
			Multiselect = false
		};
		if (dialog.ShowDialog(this) == true)
		{
			BackgroundImagePathTextBox.Text = dialog.FileName;
		}
	}

	private void ClearBackgroundImageButton_Click(object sender, RoutedEventArgs e)
	{
		BackgroundImagePathTextBox.Clear();
	}

	private void UpdateCustomAccentButton()
	{
		SetColorButton(CustomAccentButton, _customAccentColor, "强调色");
	}

	private void PreviewTheme()
	{
		if (!_initialized || UiThemeCombo == null || UiSurfaceOpacitySlider == null)
		{
			return;
		}

		ThemeService.Apply(new AppState
		{
			UiTheme = SelectedTag(UiThemeCombo, "Mint"),
			CustomAccentColor = _customAccentColor,
			UiSurfaceOpacity = Math.Clamp(UiSurfaceOpacitySlider.Value, 0.72, 1.0),
			BackgroundImagePath = _sourceState.BackgroundImagePath
		});
	}

	private void SettingsWindow_Closed(object? sender, EventArgs e)
	{
		if (base.DialogResult != true)
		{
			ThemeService.Apply(_sourceState);
		}
	}

	private void DefaultVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (DefaultVolumeText != null)
		{
			DefaultVolumeText.Text = $"{e.NewValue:0}%";
		}
	}

	private void LyricsFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (LyricsFontSizeText != null)
		{
			LyricsFontSizeText.Text = $"{e.NewValue:0}";
		}
		UpdateLyricsPreview();
	}

	private void LyricsTranslationFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (LyricsTranslationFontSizeText != null)
		{
			LyricsTranslationFontSizeText.Text = $"{e.NewValue:0}";
		}
		UpdateLyricsPreview();
	}

	private void LyricsOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (LyricsOpacityText != null)
		{
			LyricsOpacityText.Text = $"{e.NewValue:P0}";
		}
	}

	private void LyricsWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (LyricsWidthText != null)
		{
			LyricsWidthText.Text = $"{e.NewValue:0} px";
		}
	}

	private void LyricsOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (LyricsOffsetText != null)
		{
			LyricsOffsetText.Text = ((e.NewValue == 0.0) ? "0.0 s" : $"{e.NewValue:+0.0;-0.0} s");
		}
	}

	private void LyricsPreviewSetting_Changed(object sender, RoutedEventArgs e)
	{
		UpdateLyricsPreview();
	}

	private void PrimaryColorButton_Click(object sender, RoutedEventArgs e)
	{
		string color = ChooseColor(_primaryColor);
		if (color != null)
		{
			_primaryColor = color;
		}
		SelectByTag(LyricsColorCombo, "Custom");
		UpdateColorButtons();
		UpdateLyricsPreview();
	}

	private void SecondaryColorButton_Click(object sender, RoutedEventArgs e)
	{
		string color = ChooseColor(_secondaryColor);
		if (color != null)
		{
			_secondaryColor = color;
		}
		SelectByTag(LyricsColorCombo, "Custom");
		UpdateColorButtons();
		UpdateLyricsPreview();
	}

	private void RomanizationColorButton_Click(object sender, RoutedEventArgs e)
	{
		string color = ChooseColor(_romanizationColor);
		if (color != null)
		{
			_romanizationColor = color;
		}
		SelectByTag(LyricsColorCombo, "Custom");
		UpdateColorButtons();
		UpdateLyricsPreview();
	}

	private void TranslationColorButton_Click(object sender, RoutedEventArgs e)
	{
		string color = ChooseColor(_translationColor);
		if (color != null)
		{
			_translationColor = color;
		}
		SelectByTag(LyricsColorCombo, "Custom");
		UpdateColorButtons();
		UpdateLyricsPreview();
	}

	private void StrokeColorButton_Click(object sender, RoutedEventArgs e)
	{
		string color = ChooseColor(_strokeColor);
		if (color != null)
		{
			_strokeColor = color;
		}
		UpdateColorButtons();
		UpdateLyricsPreview();
	}

	private string? ChooseColor(string initial)
	{
		System.Windows.Media.Color color = ParseColor(initial, Colors.White);
		using ColorDialog dialog = new ColorDialog
		{
			FullOpen = true,
			Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B)
		};
		return (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) ? $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}" : null;
	}

	private void UpdateColorButtons()
	{
		SetColorButton(PrimaryColorButton, _primaryColor, "原文色");
		SetColorButton(SecondaryColorButton, _secondaryColor, "渐变末端");
		SetColorButton(RomanizationColorButton, _romanizationColor, "音译色");
		SetColorButton(TranslationColorButton, _translationColor, "翻译色");
		SetColorButton(StrokeColorButton, _strokeColor, "描边色");
	}

	private static void SetColorButton(System.Windows.Controls.Button button, string hex, string label)
	{
		System.Windows.Media.Color color = ParseColor(hex, Colors.White);
		button.Background = new SolidColorBrush(color);
		double luminance = (double)(int)color.R * 0.299 + (double)(int)color.G * 0.587 + (double)(int)color.B * 0.114;
		button.Foreground = ((luminance > 150.0) ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White);
		button.Content = label + "  " + hex;
	}

	private void UpdateLyricsPreview()
	{
		if (_initialized && LyricsPreviewOriginal != null)
		{
			System.Windows.Media.FontFamily font = SafeFontFamily(SelectedFont(LyricsFontCombo, "Microsoft YaHei UI"));
			LyricsPreviewOriginal.FontFamily = font;
			LyricsPreviewRomanization.FontFamily = font;
			LyricsPreviewTranslation.FontFamily = font;
			LyricsPreviewOriginal.FontSize = Math.Clamp(LyricsFontSizeSlider.Value, 12.0, 72.0);
			LyricsPreviewRomanization.FontSize = Math.Clamp(LyricsTranslationFontSizeSlider.Value, 10.0, 72.0);
			LyricsPreviewTranslation.FontSize = Math.Clamp(LyricsTranslationFontSizeSlider.Value, 10.0, 72.0);
			FontWeight weight = FontWeightFromTag(SelectedTag(LyricsFontWeightCombo, "SemiBold"));
			LyricsPreviewOriginal.FontWeight = weight;
			LyricsPreviewRomanization.FontWeight = weight;
			LyricsPreviewTranslation.FontWeight = weight;
			AppState previewState = new AppState
			{
				DesktopLyricsColorScheme = SelectedTag(LyricsColorCombo, "MintPurple"),
				DesktopLyricsPrimaryColor = _primaryColor,
				DesktopLyricsSecondaryColor = _secondaryColor,
				DesktopLyricsRomanizationColor = _romanizationColor,
				DesktopLyricsTranslationColor = _translationColor,
				DesktopLyricsStrokeColor = _strokeColor,
				DesktopLyricsUseGradient = LyricsGradientCheckBox.IsChecked == true,
				DesktopLyricsStroke = LyricsStrokeCheckBox.IsChecked == true,
				DesktopLyricsOriginalOpacity = Math.Clamp(LyricsOriginalOpacitySlider.Value, 0.35, 1.0),
				DesktopLyricsRomanizationOpacity = Math.Clamp(LyricsRomanizationOpacitySlider.Value, 0.35, 1.0),
				DesktopLyricsTranslationOpacity = Math.Clamp(LyricsTranslationOpacitySlider.Value, 0.35, 1.0),
				DesktopLyricsStrokeScale = Math.Clamp(LyricsStrokeScaleSlider.Value, 0.5, 2.0)
			};
			ApplyPreviewStyle(LyricsPreviewOriginal, previewState, LyricTextKind.Original, allowOriginalGradient: true);
			ApplyPreviewStyle(LyricsPreviewRomanization, previewState, LyricTextKind.Romanization, allowOriginalGradient: false);
			ApplyPreviewStyle(LyricsPreviewTranslation, previewState, LyricTextKind.Translation, allowOriginalGradient: false);
			string text = SelectedTag(LyricsAlignmentCombo, "Center");
			TextAlignment textAlignment = ((!(text == "Left")) ? ((text == "Right") ? TextAlignment.Right : TextAlignment.Center) : TextAlignment.Left);
			TextAlignment alignment = textAlignment;
			LyricsPreviewOriginal.TextAlignment = alignment;
			LyricsPreviewRomanization.TextAlignment = alignment;
			LyricsPreviewTranslation.TextAlignment = alignment;
			bool showTranslation = ShowTranslationCheckBox.IsChecked == true;
			if (SelectedTag(LyricsLayoutCombo, "Stacked") == "SingleLine" && showTranslation)
			{
				LyricsPreviewOriginal.Text = "夜空のメロディー  ·  yozora no melody  ·  夜空中的旋律";
				LyricsPreviewRomanization.Visibility = Visibility.Collapsed;
				LyricsPreviewTranslation.Visibility = Visibility.Collapsed;
			}
			else
			{
				LyricsPreviewOriginal.Text = "夜空のメロディー";
				LyricsPreviewRomanization.Text = "yozora no melody";
				LyricsPreviewTranslation.Text = "夜空中的旋律";
				LyricsPreviewRomanization.Visibility = ((!showTranslation) ? Visibility.Collapsed : Visibility.Visible);
				LyricsPreviewTranslation.Visibility = ((!showTranslation) ? Visibility.Collapsed : Visibility.Visible);
			}
		}
	}

	private static void ApplyPreviewStyle(OutlinedTextBlock textBlock, AppState state, LyricTextKind kind, bool allowOriginalGradient)
	{
		textBlock.Foreground = LyricsStyleService.CreateForeground(state, kind, allowOriginalGradient);
		textBlock.Stroke = LyricsStyleService.CreateStroke(state);
		textBlock.StrokeThickness = LyricsStyleService.StrokeThickness(state, textBlock.FontSize, kind);
	}

	private void LyricsOpacitySetting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (LyricsOriginalOpacityText != null && LyricsOriginalOpacitySlider != null)
		{
			LyricsOriginalOpacityText.Text = $"{LyricsOriginalOpacitySlider.Value:P0}";
		}
		if (LyricsRomanizationOpacityText != null && LyricsRomanizationOpacitySlider != null)
		{
			LyricsRomanizationOpacityText.Text = $"{LyricsRomanizationOpacitySlider.Value:P0}";
		}
		if (LyricsTranslationOpacityText != null && LyricsTranslationOpacitySlider != null)
		{
			LyricsTranslationOpacityText.Text = $"{LyricsTranslationOpacitySlider.Value:P0}";
		}
		UpdateLyricsPreview();
	}

	private void LyricsStrokeScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (LyricsStrokeScaleText != null)
		{
			LyricsStrokeScaleText.Text = $"{e.NewValue:0.0}×";
		}
		UpdateLyricsPreview();
	}

	private void PlaybackWatchdogTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (PlaybackWatchdogTimeoutText != null)
		{
			PlaybackWatchdogTimeoutText.Text = $"{e.NewValue:0} 秒";
		}
	}

	private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			System.IO.Directory.CreateDirectory(DiagnosticLog.LogDirectory);
			Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DiagnosticLog.LogDirectory}\"")
			{
				UseShellExecute = true
			});
		}
		catch (Exception exception)
		{
			System.Windows.MessageBox.Show(this, exception.Message, "无法打开日志文件夹", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private static FontWeight FontWeightFromTag(string tag)
	{
		return tag switch
		{
			"Normal" => FontWeights.Normal,
			"Medium" => FontWeights.Medium,
			"Bold" => FontWeights.Bold,
			_ => FontWeights.SemiBold,
		};
	}

	private static System.Windows.Media.FontFamily SafeFontFamily(string? name)
	{
		try
		{
			return new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(name) ? "Microsoft YaHei UI" : name);
		}
		catch
		{
			return new System.Windows.Media.FontFamily("Microsoft YaHei UI");
		}
	}

	private static string SelectedFont(System.Windows.Controls.ComboBox comboBox, string fallback)
	{
		if (!string.IsNullOrWhiteSpace(comboBox.Text))
		{
			return comboBox.Text.Trim();
		}
		return fallback;
	}

	private static int SelectedInt(System.Windows.Controls.ComboBox comboBox)
	{
		object selectedItem = comboBox.SelectedItem;
		if (selectedItem is int)
		{
			return (int)selectedItem;
		}
		return 0;
	}

	private static void SelectByTag(System.Windows.Controls.ComboBox comboBox, string tag)
	{
		comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>().FirstOrDefault((ComboBoxItem item) => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
		comboBox.SelectedIndex = Math.Max(0, comboBox.SelectedIndex);
	}

	private static string SelectedTag(System.Windows.Controls.ComboBox comboBox, string fallback)
	{
		return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
	}

	private static string NormalizeColor(string? value, string fallback)
	{
		string normalized = (string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
		try
		{
			_ = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(normalized);
			return normalized.StartsWith('#') ? normalized.ToUpperInvariant() : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static System.Windows.Media.Color ParseColor(string value, System.Windows.Media.Color fallback)
	{
		try
		{
			return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
		}
		catch
		{
			return fallback;
		}
	}

}
