using System.Collections.Generic;

namespace OfflineMusicLibrary;

public sealed class AppState
{
	public int StateFormatVersion { get; set; } = 2;

	public List<string> LibraryFolders { get; set; } = new List<string>();

	public List<TrackModel> Tracks { get; set; } = new List<TrackModel>();

	public List<PlaylistModel> Playlists { get; set; } = new List<PlaylistModel>();

	public List<string> FavoriteAlbumKeys { get; set; } = new List<string>();

	public int Volume { get; set; } = 76;

	public bool DesktopLyricsEnabled { get; set; }

	public string RepeatMode { get; set; } = "All";

	public bool ShuffleEnabled { get; set; }

	public string ShuffleMode { get; set; } = "Off";

	public double PlaybackRate { get; set; } = 1.0;

	public string VisualizationMode { get; set; } = "Off";

	public string HardwareDecoding { get; set; } = "Auto";

	public string VideoOutput { get; set; } = "Auto";

	public string AudioBackend { get; set; } = "DirectSound";

	public string PreferredAudioDeviceId { get; set; } = "";

	public string EqualizerPreset { get; set; } = "Off";

	public string SpatialAudioMode { get; set; } = "Off";

	public bool InPlayerBilingualSubtitles { get; set; } = true;

	public string LyricsDisplayMode { get; set; } = LyricsDisplayModes.OriginalTranslation;

	public string PlayerPageMode { get; set; } = PlayerPageModes.Standard;

	public bool GlobalHotkeysEnabled { get; set; } = true;

	public bool SystemMediaKeysEnabled { get; set; } = true;

	public bool ScanOnStartup { get; set; }

	public string UiFontFamily { get; set; } = "Microsoft YaHei UI";

	public string UiTheme { get; set; } = "Mint";

	public string CustomAccentColor { get; set; } = "#147D6A";

	public double UiSurfaceOpacity { get; set; } = 0.94;

	public string BackgroundImagePath { get; set; } = "";

	public string AppTitleText { get; set; } = "本地音乐库";

	public string StartPage { get; set; } = "Discover";

	public bool ShowRecommendationsInSidebar { get; set; } = true;

	public bool ShowCirclesInSidebar { get; set; } = true;

	public bool ShowRecentInSidebar { get; set; } = true;

	public bool ShowHistoryInSidebar { get; set; } = true;

	public bool ShowCategoriesInSidebar { get; set; } = true;

	public bool RunAtStartup { get; set; }

	public bool StartMinimized { get; set; }

	public bool FloatingMiniPlayerEnabled { get; set; }

	public string CloseBehavior { get; set; } = "Exit";

	public bool AutoCloseEnabled { get; set; }

	public int AutoCloseMinutes { get; set; }

	public bool AutoPlayOnStartup { get; set; }

	public bool RememberPlaybackProgress { get; set; } = true;

	public string DoubleClickQueueMode { get; set; } = "Replace";

	public string? LastTrackId { get; set; }

	public long LastPlaybackPositionMs { get; set; }

	public int LyricOffsetMs { get; set; }

	public bool DesktopLyricsTopmost { get; set; } = true;

	public bool DesktopLyricsShowTranslation { get; set; } = true;

	public bool DesktopLyricsBold { get; set; } = true;

	public bool DesktopLyricsStroke { get; set; } = true;

	public bool DesktopLyricsLocked { get; set; }

	public bool DesktopLyricsClickToActivate { get; set; }

	public double DesktopLyricsFontSize { get; set; } = 25.0;

	public double DesktopLyricsTranslationFontSize { get; set; } = 17.0;

	public double DesktopLyricsBackgroundOpacity { get; set; } = 0.5;

	public double DesktopLyricsWidth { get; set; } = 760.0;

	public string DesktopLyricsFontFamily { get; set; } = "Microsoft YaHei UI";

	public string DesktopLyricsFontWeight { get; set; } = "SemiBold";

	public string DesktopLyricsColorScheme { get; set; } = "MintPurple";

	public string DesktopLyricsPrimaryColor { get; set; } = "#C9B7FF";

	public string DesktopLyricsSecondaryColor { get; set; } = "#79D9A9";

	public string DesktopLyricsStrokeColor { get; set; } = "#000000";

	public string DesktopLyricsRomanizationColor { get; set; } = "";

	public string DesktopLyricsTranslationColor { get; set; } = "";

	public double DesktopLyricsOriginalOpacity { get; set; } = 1.0;

	public double DesktopLyricsRomanizationOpacity { get; set; } = 0.92;

	public double DesktopLyricsTranslationOpacity { get; set; } = 0.9;

	public double DesktopLyricsStrokeScale { get; set; } = 1.0;

	public bool InPlayerSubtitlesUseLyricsStyle { get; set; }

	public bool DesktopLyricsUseGradient { get; set; } = true;

	public string DesktopLyricsLayout { get; set; } = "Stacked";

	public string DesktopLyricsAlignment { get; set; } = "Center";

	public double? DesktopLyricsLeft { get; set; }

	public double? DesktopLyricsTop { get; set; }

	public bool PlaybackRecoveryEnabled { get; set; } = true;

	public int PlaybackWatchdogTimeoutSeconds { get; set; } = 12;

	public int PlaybackRecoveryAttempts { get; set; } = 3;

	public bool SkipTrackAfterRecoveryFailure { get; set; } = true;

	public bool SafePlaybackMode { get; set; }

	public bool StateBackupEnabled { get; set; } = true;
}
