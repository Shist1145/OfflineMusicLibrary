using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OfflineMusicLibrary;

public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
	private enum LibraryView
	{
		All,
		Favorites,
		Recent,
		History,
		Category,
		Albums,
		Album,
		Circles,
		Circle,
		Playlist,
		Discover,
		Recommendation
	}

	private readonly AppStore _store;

	private readonly MusicLibraryService _libraryService = new MusicLibraryService();

	private readonly LibraryRootHealthService _rootHealthService = new LibraryRootHealthService();

	private readonly NetEasePlaylistService _netEaseService = new NetEasePlaylistService();

	private readonly PlaybackService _playback = new PlaybackService();

	private readonly GlobalHotkeyService _hotkeys = new GlobalHotkeyService();

	private readonly CancellationTokenSource _lifetimeCancellation = new CancellationTokenSource();

	private readonly DispatcherTimer _playerTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromMilliseconds(250L)
	};

	private readonly DispatcherTimer _autoCloseTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(20L)
	};

	private readonly DispatcherTimer _stateSaveTimer = new DispatcherTimer
	{
		Interval = TimeSpan.FromSeconds(5L)
	};

	private readonly Random _random = new Random();

	private readonly ObservableCollection<string> _categories = new ObservableCollection<string>();

	private readonly ObservableCollection<AlbumViewModel> _albums = new ObservableCollection<AlbumViewModel>();

	private readonly ObservableCollection<SidebarNavigationItem> _sidebarItems = new ObservableCollection<SidebarNavigationItem>();

	private readonly ObservableCollection<AlbumViewModel> _albumCards = new ObservableCollection<AlbumViewModel>();

	private readonly ObservableCollection<CircleViewModel> _circles = new ObservableCollection<CircleViewModel>();

	private readonly ObservableCollection<CircleViewModel> _circleCards = new ObservableCollection<CircleViewModel>();

	private readonly Queue<string> _recentTrackIds = new Queue<string>();

	private readonly Dictionary<RecommendationPreset, RecommendationResult> _recommendationCache = new();

	private readonly SemaphoreSlim _albumCoverLoadGate = new SemaphoreSlim(4, 4);

	private AppState _state = new AppState();

	private List<TrackModel> _visibleTracks = new List<TrackModel>();

	private List<AlbumViewModel> _filteredAlbums = new List<AlbumViewModel>();

	private List<CircleViewModel> _filteredCircles = new List<CircleViewModel>();

	private List<TrackModel> _queue = new List<TrackModel>();

	private List<LyricLine> _lyrics = new List<LyricLine>();

	private TrackModel? _currentTrack;

	private PlaylistModel? _currentPlaylist;

	private string? _currentCategory;

	private string? _currentAlbumKey;

	private string? _currentCircleKey;

	private LibraryView _view;

	private DesktopLyricsWindow? _desktopLyrics;

	private PreviewPlayerWindow? _previewPlayer;

	private TaskbarPreviewService? _taskbarHoverPreview;

	private TaskbarPlaybackControls? _taskbarControls;

	private NotifyIcon? _trayIcon;

	private int _queueIndex = -1;

	private int _volumeBeforeMiniMute = 76;

	private int _currentLyricIndex = -1;

	private bool _initialized;

	private bool _isScanning;

	private CancellationTokenSource? _scanCancellation;

	private bool _seeking;

	private bool _suppressNavigation;

	private bool _shuttingDown;
	private bool _shutdownSaveCompleted;

	private bool _refreshingMediaControls;

	private bool _showingPlayerView;

	private bool _forceExit;

	private bool _startupPlaybackApplied;

	private long? _pendingSeekMs;

	private string? _pendingSeekTrackId;

	private DateTime? _autoCloseAt;

	private int _albumCardLoadVersion;

	private int _circleCardLoadVersion;

	private int _playlistCoverLoadVersion;

	private int _nowPlayingLoadVersion;

	private int _watchdogRecoveryCount;

	private long _watchdogRecoveryResumeAt = -1L;

	private bool _playbackRecoveryInProgress;

	private bool _rootReconnectInProgress;

	private string? _watchdogTrackId;

	private CancellationTokenSource? _rootWaitCancellation;

	private string? _waitingRootId;

	private long _waitingResumePositionMs;

	private int _playTrackRequestVersion;

	private MediaDetails _mediaDetails = new MediaDetails();

	private string _trackSortKey = "ArtistAlbum";

	private ListSortDirection _trackSortDirection;

	private bool _syncingSortCombo;

	private bool _suppressSearchRefresh;

	private bool _syncingAudioEffectControls;

	private bool _syncingLyricsDisplayMode;

	private bool _syncingPlayerPageMode;

	private bool _syncingVolumeControls;

	private bool _syncingPositionControls;

	private bool _syncingDesktopLyricsControls;

	private bool _sidebarCategoriesExpanded = true;

	private bool _sidebarPlaylistsExpanded = true;

	private bool _showFavoriteAlbumsOnly;

	private bool _previewDismissedWhileMinimized;

	private int _unfilteredTrackCount;

	private int _recommendationRefreshSalt;

	private AnimationClock? _standardVinylClock;

	private AnimationClock? _vinylFocusClock;

	private AnimationClock? _lyricsModeDiscClock;

	private bool _vinylAnimationRunning;

	private RecommendationPreset _activeRecommendationPreset = RecommendationPreset.DailyRecommendation;

	private List<TrackModel> _recommendationTracks = new();

	private const int AlbumCardBatchSize = 64;

	private const int CircleCardBatchSize = 64;

	private bool IsAlbumPage => _view == LibraryView.Albums;

	private bool IsCirclePage => _view == LibraryView.Circles;

	private bool IsDiscoveryPage => _view == LibraryView.Discover;

	private bool IsRecommendationPage => _view == LibraryView.Recommendation;

	public MainWindow()
		: this(new AppStore(), loadStateOnShow: true)
	{
	}

	internal MainWindow(AppStore store, bool loadStateOnShow)
	{
		ArgumentNullException.ThrowIfNull(store, "store");
		_store = store;
		InitializeComponent();
		InitializeVinylAnimations();
		ApplyImmersiveLyricStyles();
		ConfigureTaskbarHoverPlayer();
		ConfigurePreviewPlayer();
		if (!loadStateOnShow)
		{
			base.Loaded -= Window_Loaded;
		}
		AttachPlaybackSurface();
		AddHandler(Keyboard.KeyDownEvent, new System.Windows.Input.KeyEventHandler(Window_HandledKeyDown), handledEventsToo: true);
		SidebarList.ItemsSource = _sidebarItems;
		AlbumGridList.ItemsSource = _albumCards;
		CircleGridList.ItemsSource = _circleCards;
		RefreshQueueLists();
		RefreshLyricsLists();
		_playerTimer.Tick += PlayerTimer_Tick;
		_autoCloseTimer.Tick += AutoCloseTimer_Tick;
		_stateSaveTimer.Tick += StateSaveTimer_Tick;
		_playback.Ended += delegate
		{
			_ = base.Dispatcher.BeginInvoke(new Action(HandleTrackEnded));
		};
		_playback.PlaybackError += delegate
		{
			_ = base.Dispatcher.BeginInvoke((Action)delegate
			{
				if (_rootReconnectInProgress)
				{
					return;
				}
				if (!_state.PlaybackRecoveryEnabled)
				{
					StatusText.Text = "播放引擎报告异常；自动恢复已关闭";
					return;
				}
				StatusText.Text = "播放引擎报告异常，正在尝试恢复……";
				DiagnosticLog.Observe(RecoverPlaybackAsync("底层播放错误"), "WATCHDOG", "Playback error recovery task failed");
			});
		};
		_playback.PlaybackReady += delegate
		{
			_ = base.Dispatcher.BeginInvoke((Action)delegate
			{
				DiagnosticLog.Observe(HandlePlaybackReadyAsync(), "PLAY", "Could not finish playback-ready initialization");
			});
		};
		_playback.PlayerChanged += delegate
		{
			_ = base.Dispatcher.BeginInvoke((Action)delegate
			{
				AttachPlaybackSurface();
			});
		};
		_playback.MediaDetailsChanged += delegate(MediaDetails details)
		{
			_ = base.Dispatcher.BeginInvoke((Action)delegate
			{
				UpdateMediaDetails(details);
			});
		};
		_hotkeys.Invoked += delegate(HotkeyAction action)
		{
			_ = base.Dispatcher.BeginInvoke((Action)delegate
			{
				HandleHotkey(action);
			});
		};
	}

	private async Task HandlePlaybackReadyAsync()
	{
		await Task.Delay(350);
		long? pendingSeekMs = _pendingSeekMs;
		if (pendingSeekMs.HasValue && string.Equals(_pendingSeekTrackId, _currentTrack?.Id, StringComparison.OrdinalIgnoreCase))
		{
			_playback.Seek(pendingSeekMs.GetValueOrDefault());
		}
		_pendingSeekMs = null;
		_pendingSeekTrackId = null;
		await RefreshMediaControlsAsync();
	}

	private async void Window_Loaded(object sender, RoutedEventArgs e)
	{
		_ = 1;
		try
		{
			_state = await _store.LoadAsync();
			PersistentAssetCache.Configure(
				_store.AssetCacheDirectory,
				_state.PersistentAssetCacheEnabled,
				_state.PersistentAssetCacheMaxMegabytes);
			ApplyUiFont();
			ApplyPersonalization();
			try
			{
				StartupRegistrationService.Apply(_state.RunAtStartup);
			}
			catch
			{
			}
			if (_state.ShuffleEnabled && _state.ShuffleMode == "Off")
			{
				_state.ShuffleMode = "Uniform";
			}
			_state.ShuffleEnabled = _state.ShuffleMode != "Off";
			_volumeBeforeMiniMute = Math.Max(1, _state.Volume);
			VolumeSlider.Value = _state.Volume;
			_playback.Volume = _state.Volume;
			SelectComboByTag(PlaybackRateCombo, _state.PlaybackRate.ToString(CultureInfo.InvariantCulture));
			SelectComboByTag(VisualizationCombo, _state.VisualizationMode);
			_state.LyricsDisplayMode = LyricsDisplayModes.Normalize(_state.LyricsDisplayMode);
			SelectComboByTag(LyricsDisplayModeCombo, _state.LyricsDisplayMode);
			_state.PlayerPageMode = PlayerPageModes.Normalize(_state.PlayerPageMode);
			ApplyPlayerPageMode(_state.PlayerPageMode);
			ApplyImmersiveLyricStyles();
			ApplyImmersivePerformanceProfile();
			SyncAudioEffectControls();
			_playback.SetEqualizerPreset(_state.SafePlaybackMode ? "Off" : _state.EqualizerPreset);
			UpdatePlaybackModeButtons();
			ShowPlayerView(showPlayer: false);
			RefreshNavigation();
			_initialized = true;
			ApplyStartupPage();
			DesktopLyricsCheckBox.IsChecked = _state.DesktopLyricsEnabled;
			if (_state.Tracks.Count == 0 || _state.ScanOnStartup)
			{
				await ScanLibraryAsync();
			}
			else
			{
				ApplyFilter();
				StatusText.Text = $"已载入 {_state.Tracks.Count:N0} 首本地歌曲";
			}
			ConfigureHotkeys();
			ConfigureTrayIcon();
			ConfigureAutoCloseTimer();
			_playerTimer.Start();
			ApplyStartupPlayback();
			DiagnosticLog.Observe(MonitorLibraryRootsAsync(_lifetimeCancellation.Token), "NAS", "Library root monitor stopped unexpectedly");
			ApplyStartupWindowState();
		}
		catch (Exception ex)
		{
			_initialized = true;
			StatusText.Text = "曲库载入失败";
			System.Windows.MessageBox.Show(this, ex.Message, "无法载入曲库", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private async void Window_Closing(object? sender, CancelEventArgs e)
	{
		RememberCurrentPlayback();
		if (!_forceExit && string.Equals(_state.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase))
		{
			e.Cancel = true;
			HideToTray();
			DiagnosticLog.Observe(_store.SaveAsync(_state), "STATE", "Could not save state while minimizing to tray");
			return;
		}
		if (!_forceExit && string.Equals(_state.CloseBehavior, "Minimize", StringComparison.OrdinalIgnoreCase))
		{
			e.Cancel = true;
			base.WindowState = WindowState.Minimized;
			DiagnosticLog.Observe(_store.SaveAsync(_state), "STATE", "Could not save state while minimizing");
			return;
		}
		if (!_shutdownSaveCompleted)
		{
			e.Cancel = true;
			if (_shuttingDown)
			{
				return;
			}

			_shuttingDown = true;
			_lifetimeCancellation.Cancel();
			CancelRootWait();
			_scanCancellation?.Cancel();
			_playerTimer.Stop();
			_autoCloseTimer.Stop();
			_stateSaveTimer.Stop();
			_state.Volume = (int)VolumeSlider.Value;
			try
			{
				await _store.SaveAsync(_state);
			}
			catch (Exception ex)
			{
				DiagnosticLog.Write("STATE", "Final state save failed during shutdown", ex);
			}
			_shutdownSaveCompleted = true;
			Close();
			return;
		}
		DisposeVinylAnimations();
		_desktopLyrics?.Close();
		_previewPlayer?.Close();
		_previewPlayer = null;
		_taskbarHoverPreview?.Dispose();
		_taskbarHoverPreview = null;
		_hotkeys.Dispose();
		_playback.Dispose();
		_lifetimeCancellation.Dispose();
		if (_trayIcon != null)
		{
			_trayIcon.Visible = false;
			_trayIcon.Dispose();
		}
	}

	private async Task ScanLibraryAsync(bool forceMetadataRefresh = false)
	{
		if (_isScanning)
		{
			return;
		}
		SynchronizeLibraryRootsFromFolders();
		if (_state.LibraryFolders.Count == 0)
		{
			StatusText.Text = "请先添加音乐文件夹";
			return;
		}
		_isScanning = true;
		CancellationTokenSource scanCancellation = new CancellationTokenSource();
		_scanCancellation = scanCancellation;
		RescanButton.IsEnabled = true;
		RescanButton.Content = "×";
		RescanButton.ToolTip = "取消当前扫描";
		AddFolderButton.IsEnabled = false;
		Progress<ScanProgress> progress = new Progress<ScanProgress>(delegate(ScanProgress value)
		{
			StatusText.Text = $"正在扫描 {value.Scanned:N0} / {value.Total:N0}，元数据回退 {value.Errors:N0}";
			TrackCountText.Text = Path.GetFileName(value.CurrentFile);
		});
		try
		{
			bool hadIndexedTracks = _state.Tracks.Count > 0;
			AppState state = _state;
			state.Tracks = await _libraryService.ScanAsync(
				_state.LibraryFolders,
				_state.Tracks,
				progress,
				scanCancellation.Token,
				forceMetadataRefresh);
			MarkReachableRootsScanned();
			await _store.SaveAsync(_state);
			bool confirmedEmptyLibrary = hadIndexedTracks && _state.Tracks.Count == 0 && AreAllRootsKnownReachable();
			RefreshNavigation(confirmedEmptyLibrary);
			if (IsDiscoveryPage)
			{
				RefreshRecommendationOverview();
				UpdateLibraryContentVisibility();
			}
			else if (IsRecommendationPage)
			{
				OpenRecommendation(_activeRecommendationPreset);
			}
			else
			{
				ApplyFilter();
			}
			StatusText.Text = $"扫描完成，共 {_state.Tracks.Count:N0} 首本地歌曲";
		}
		catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
		{
			StatusText.Text = "扫描已取消，原有曲库未被改写";
		}
		catch (Exception ex)
		{
			StatusText.Text = "扫描未完成";
			System.Windows.MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			if (ReferenceEquals(_scanCancellation, scanCancellation))
			{
				_scanCancellation = null;
			}
			scanCancellation.Dispose();
			_isScanning = false;
			RescanButton.IsEnabled = true;
			RescanButton.Content = "↻";
			RescanButton.ToolTip = "重新扫描曲库";
			AddFolderButton.IsEnabled = true;
		}
	}

	private void RefreshNavigation(bool allowEmptyLibraryCleanup = false)
	{
		_recommendationCache.Clear();
		bool stateNeedsSave = false;
		if (_state.Tracks.Count > 0 || allowEmptyLibraryCleanup)
		{
			foreach (PlaylistModel playlist in _state.Playlists)
			{
				PlaylistCleanupResult cleanup = PlaylistMaintenance.Clean(playlist, _state.Tracks);
				if (!cleanup.Changed)
				{
					continue;
				}
				playlist.UpdatedAt = DateTime.Now;
				playlist.InvalidateCover();
				playlist.NotifyMetadataChanged();
				stateNeedsSave = true;
			}
		}
		PopulateCircleFallbacks();
		_categories.Clear();
		foreach (string category in (from value in _state.Tracks.SelectMany((TrackModel track) => track.Categories)
			where !string.IsNullOrWhiteSpace(value)
			select value).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).OrderBy<string, string>((string result) => result, StringComparer.CurrentCultureIgnoreCase))
		{
			_categories.Add(category);
		}
		List<string> migratedFavoriteAlbumKeys = AlbumIdentity.MigrateFavoriteKeys(_state.Tracks, _state.FavoriteAlbumKeys);
		if (!_state.FavoriteAlbumKeys.SequenceEqual(migratedFavoriteAlbumKeys, StringComparer.OrdinalIgnoreCase))
		{
			_state.FavoriteAlbumKeys = migratedFavoriteAlbumKeys;
			stateNeedsSave = true;
		}
		if (stateNeedsSave)
		{
			ScheduleStateSave();
		}
		_albums.Clear();
		foreach (IGrouping<string, TrackModel> group in _state.Tracks.GroupBy<TrackModel, string>(AlbumIdentity.Create, StringComparer.OrdinalIgnoreCase).OrderBy<IGrouping<string, TrackModel>, string>((IGrouping<string, TrackModel> tracks) => GetAlbumTitle(tracks), StringComparer.CurrentCultureIgnoreCase).ThenBy<IGrouping<string, TrackModel>, string>(GetAlbumArtist, StringComparer.CurrentCultureIgnoreCase))
		{
			TrackModel representative = group.OrderBy((TrackModel track) => track.TrackNumber).ThenBy<TrackModel, string>((TrackModel track) => track.Title, StringComparer.CurrentCultureIgnoreCase).First();
			bool isFavorite = IsFavoriteAlbum(group.Key) || group.Any((TrackModel track) => IsFavoriteAlbum(AlbumIdentity.FolderScopedFallback(track)));
			if (isFavorite)
			{
				AddFavoriteAlbum(group.Key);
			}
			_albums.Add(new AlbumViewModel
			{
				Key = group.Key,
				Title = GetAlbumTitle(group),
				Artist = GetAlbumArtist(group),
				CircleNames = string.Join(" ", (from track in @group
					select track.Circle into value
					where !string.IsNullOrWhiteSpace(value)
					select value).Distinct<string>(StringComparer.CurrentCultureIgnoreCase)),
				TrackCount = group.Count(),
				IsFavorite = isFavorite,
				RepresentativeTrack = representative
			});
		}
		_circles.Clear();
		foreach (IGrouping<string, TrackModel> group2 in _state.Tracks.Where((TrackModel track) => !string.IsNullOrWhiteSpace(track.Circle)).GroupBy<TrackModel, string>(CircleIdentity.Create, StringComparer.OrdinalIgnoreCase).OrderBy<IGrouping<string, TrackModel>, string>((IGrouping<string, TrackModel> tracks) => GetCircleName(tracks), StringComparer.CurrentCultureIgnoreCase))
		{
			TrackModel representative2 = group2.OrderByDescending((TrackModel track) => track.HasCover).ThenBy<TrackModel, string>((TrackModel track) => track.Album, StringComparer.CurrentCultureIgnoreCase).ThenBy((TrackModel track) => track.TrackNumber)
				.First();
			_circles.Add(new CircleViewModel
			{
				Key = group2.Key,
				Name = GetCircleName(group2),
				AlbumCount = group2.Select(AlbumIdentity.Create).Distinct<string>(StringComparer.OrdinalIgnoreCase).Count(),
				TrackCount = group2.Count(),
				RepresentativeTrack = representative2
			});
		}
		if (IsAlbumPage)
		{
			ApplyAlbumFilter();
		}
		else if (IsCirclePage)
		{
			ApplyCircleFilter();
		}
		RefreshSidebarNavigation();
		QueuePlaylistCoverLoads();
		UpdatePlaylistHeader();
	}

	private void RefreshSidebarNavigation()
	{
		bool wasSuppressingNavigation = _suppressNavigation;
		_suppressNavigation = true;
		try
		{
			_sidebarItems.Clear();
			_sidebarItems.Add(new SidebarNavigationItem
			{
				Kind = SidebarNavigationKind.LibraryHeader,
				Title = "资料库"
			});
			if (_state.ShowRecommendationsInSidebar)
			{
				AddLibrarySidebarItem("Discover", "✦", "为你推荐");
			}
			AddLibrarySidebarItem("All", "♫", "全部音乐", _state.Tracks.Count.ToString("N0"));
			AddLibrarySidebarItem("Favorites", "♥", "我的收藏");
			AddLibrarySidebarItem("Albums", "▦", "专辑", _albums.Count.ToString("N0"));
			if (_state.ShowCirclesInSidebar)
			{
				AddLibrarySidebarItem("Circles", "♬", "社团");
			}
			if (_state.ShowRecentInSidebar)
			{
				AddLibrarySidebarItem("Recent", "◷", "最近添加");
			}
			if (_state.ShowHistoryInSidebar)
			{
				AddLibrarySidebarItem("History", "↶", "播放历史");
			}
			if (_state.ShowCategoriesInSidebar)
			{
				_sidebarItems.Add(new SidebarNavigationItem
				{
					Kind = SidebarNavigationKind.CategoryHeader,
					Title = "分类",
					CountText = _categories.Count.ToString("N0"),
					ExpansionGlyph = (_sidebarCategoriesExpanded ? "▾" : "▸")
				});
				if (_sidebarCategoriesExpanded)
				{
					foreach (string category in _categories)
					{
						_sidebarItems.Add(new SidebarNavigationItem
						{
							Kind = SidebarNavigationKind.Category,
							Category = category,
							Title = category
						});
					}
				}
			}
			_sidebarItems.Add(new SidebarNavigationItem
			{
				Kind = SidebarNavigationKind.PlaylistHeader,
				Title = "歌单",
				CountText = _state.Playlists.Count.ToString("N0"),
				ExpansionGlyph = (_sidebarPlaylistsExpanded ? "▾" : "▸")
			});
			if (_sidebarPlaylistsExpanded)
			{
				foreach (PlaylistModel playlist in _state.Playlists)
				{
					_sidebarItems.Add(new SidebarNavigationItem
					{
						Kind = SidebarNavigationKind.Playlist,
						Playlist = playlist,
						Title = playlist.Name,
						CountText = playlist.CountText
					});
				}
			}
			SidebarList.SelectedItem = FindCurrentSidebarItem();
		}
		finally
		{
			_suppressNavigation = wasSuppressingNavigation;
		}
	}

	private void AddLibrarySidebarItem(string key, string icon, string title, string countText = "")
	{
		_sidebarItems.Add(new SidebarNavigationItem
		{
			Kind = SidebarNavigationKind.LibraryItem,
			NavigationKey = key,
			Icon = icon,
			Title = title,
			CountText = countText
		});
	}

	private SidebarNavigationItem? FindCurrentSidebarItem()
	{
		return _view switch
		{
			LibraryView.Discover => FindLibrarySidebarItem("Discover"),
			LibraryView.Recommendation => FindLibrarySidebarItem("Discover"),
			LibraryView.All => FindLibrarySidebarItem("All"),
			LibraryView.Favorites => FindLibrarySidebarItem("Favorites"),
			LibraryView.Albums => FindLibrarySidebarItem("Albums"),
			LibraryView.Circles => FindLibrarySidebarItem("Circles"),
			LibraryView.Recent => FindLibrarySidebarItem("Recent"),
			LibraryView.History => FindLibrarySidebarItem("History"),
			LibraryView.Category => _sidebarItems.FirstOrDefault((SidebarNavigationItem item) => item.Kind == SidebarNavigationKind.Category && string.Equals(item.Category, _currentCategory, StringComparison.CurrentCultureIgnoreCase)),
			LibraryView.Album => FindLibrarySidebarItem("Albums"),
			LibraryView.Playlist => _sidebarItems.FirstOrDefault((SidebarNavigationItem item) => item.Kind == SidebarNavigationKind.Playlist && string.Equals(item.Playlist?.Id, _currentPlaylist?.Id, StringComparison.OrdinalIgnoreCase)),
			_ => null,
		};
	}

	private SidebarNavigationItem? FindLibrarySidebarItem(string key)
	{
		return _sidebarItems.FirstOrDefault((SidebarNavigationItem item) => item.Kind == SidebarNavigationKind.LibraryItem && string.Equals(item.NavigationKey, key, StringComparison.Ordinal));
	}

	private void QueuePlaylistCoverLoads()
	{
		int version = ++_playlistCoverLoadVersion;
		foreach (PlaylistModel playlist in _state.Playlists.Where((PlaylistModel playlistModel) => playlistModel.CoverThumbnail == null))
		{
			DiagnosticLog.Observe(LoadPlaylistCoverAsync(playlist, version), "ASSET", "Could not load a playlist cover");
		}
	}

	private async Task LoadPlaylistCoverAsync(PlaylistModel playlist, int version)
	{
		string customCoverPath = playlist.CoverPath;
		Dictionary<string, TrackModel> trackMap = _state.Tracks.GroupBy<TrackModel, string>((TrackModel track) => track.Id, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, TrackModel>, string, TrackModel>((IGrouping<string, TrackModel> group) => group.Key, (IGrouping<string, TrackModel> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		List<TrackModel> candidates = (from id in playlist.TrackIds
			select trackMap.GetValueOrDefault(id) into track
			where track != null
			select track).Cast<TrackModel>().Take(24).ToList();
		await _albumCoverLoadGate.WaitAsync();
		try
		{
			BitmapSource? cover = await Task.Run(delegate
			{
				BitmapSource bitmapSource = CoverService.LoadImageFile(customCoverPath, 360);
				if (bitmapSource != null)
				{
					return bitmapSource;
				}
				foreach (TrackModel item in candidates)
				{
					BitmapSource bitmapSource2 = CoverService.LoadThumbnail(item, 360);
					if (bitmapSource2 != null)
					{
						return bitmapSource2;
					}
				}
				return (BitmapSource)null;
			});
			if (version == _playlistCoverLoadVersion && _state.Playlists.Contains(playlist))
			{
				playlist.CoverThumbnail = cover;
				if (string.Equals(_currentPlaylist?.Id, playlist.Id, StringComparison.OrdinalIgnoreCase))
				{
					PlaylistHeaderCoverImage.Source = cover;
				}
			}
		}
		finally
		{
			_albumCoverLoadGate.Release();
		}
	}

	private static string GetAlbumTitle(IEnumerable<TrackModel> tracks)
	{
		return (from @group in (from @group in tracks.Select((TrackModel track) => (!string.IsNullOrWhiteSpace(track.Album)) ? track.Album.Trim() : "未知专辑").GroupBy<string, string>((string title) => title, StringComparer.CurrentCultureIgnoreCase)
				orderby @group.Count() descending
				select @group).ThenBy<IGrouping<string, string>, string>((IGrouping<string, string> @group) => @group.Key, StringComparer.CurrentCultureIgnoreCase)
			select @group.Key).FirstOrDefault() ?? "未知专辑";
	}

	private static string GetCircleName(IEnumerable<TrackModel> tracks)
	{
		return (from @group in (from @group in (from track in tracks
					select track.Circle.Trim() into value
					where value.Length > 0
					select value).GroupBy<string, string>((string value) => value, StringComparer.CurrentCultureIgnoreCase)
				orderby @group.Count() descending
				select @group).ThenBy<IGrouping<string, string>, string>((IGrouping<string, string> @group) => @group.Key, StringComparer.CurrentCultureIgnoreCase)
			select @group.Key).FirstOrDefault() ?? "未识别社团";
	}

	private void PopulateCircleFallbacks()
	{
		foreach (IGrouping<string, TrackModel> albumGroup in _state.Tracks.GroupBy<TrackModel, string>(AlbumIdentity.Create, StringComparer.OrdinalIgnoreCase))
		{
			List<TrackModel> unresolved = albumGroup.Where((TrackModel track) => !track.CircleIsManual && string.IsNullOrWhiteSpace(track.Circle)).ToList();
			if (unresolved.Count == 0)
			{
				continue;
			}
			var knownCircles = (from @group in albumGroup.Where((TrackModel track) => !string.IsNullOrWhiteSpace(track.Circle)).GroupBy<TrackModel, string>((TrackModel track) => CircleIdentity.Create(track), StringComparer.OrdinalIgnoreCase)
				select new
				{
					Key = @group.Key,
					Name = (from values in @group.Select((TrackModel track) => track.Circle.Trim()).GroupBy<string, string>((string value) => value, StringComparer.CurrentCultureIgnoreCase)
						orderby values.Count() descending
						select values.Key).First()
				}).ToList();
			if (knownCircles.Count > 1)
			{
				continue;
			}
			List<string> albumArtists = albumGroup.Select((TrackModel track) => track.AlbumArtist?.Trim() ?? "").Where(IsUsableCircleCandidate).Distinct<string>(StringComparer.CurrentCultureIgnoreCase)
				.ToList();
			List<string> performers = albumGroup.Select((TrackModel track) => track.Artist?.Trim() ?? "").Where(IsUsableCircleCandidate).Distinct<string>(StringComparer.CurrentCultureIgnoreCase)
				.ToList();
			string candidate = ((knownCircles.Count == 1) ? knownCircles[0].Name : ((albumArtists.Count == 1) ? albumArtists[0] : ((performers.Count == 1) ? performers[0] : null)));
			if (candidate == null)
			{
				continue;
			}
			foreach (TrackModel item in unresolved)
			{
				item.Circle = candidate;
			}
		}
	}

	private static bool IsUsableCircleCandidate(string value)
	{
		if (!string.IsNullOrWhiteSpace(value) && !IsUnknownArtist(value) && !string.Equals(value, "V.A.", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "VA", StringComparison.OrdinalIgnoreCase))
		{
			return !string.Equals(value, "Various Artists", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private static string GetAlbumArtist(IEnumerable<TrackModel> tracks)
	{
		List<TrackModel> list = tracks.ToList();
		List<string> albumArtists = (from track in list
			select track.AlbumArtist?.Trim() ?? "" into value
			where !string.IsNullOrWhiteSpace(value) && !IsUnknownArtist(value)
			select value).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).ToList();
		if (albumArtists.Count == 1)
		{
			return albumArtists[0];
		}
		if (albumArtists.Count > 1)
		{
			return "V.A.";
		}
		List<string> artists = (from track in list
			select track.Artist?.Trim() ?? "" into value
			where !string.IsNullOrWhiteSpace(value) && !IsUnknownArtist(value)
			select value).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).ToList();
		return artists.Count switch
		{
			0 => "未知艺术家",
			1 => artists[0],
			_ => "V.A.",
		};
	}

	private static bool IsUnknownArtist(string value)
	{
		if (!string.Equals(value, "未知艺术家", StringComparison.CurrentCultureIgnoreCase))
		{
			return string.Equals(value, "unknown artist", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private void ApplyFilter()
	{
		if (!_initialized)
		{
			return;
		}
		if (!IsRecommendationPage && _trackSortKey == "RecommendationRank")
		{
			_trackSortKey = "ArtistAlbum";
			_trackSortDirection = ListSortDirection.Ascending;
			SyncSortCombo(_trackSortKey);
		}
		IEnumerable<TrackModel> tracks = IsRecommendationPage ? _recommendationTracks : _state.Tracks;
		switch (_view)
		{
		case LibraryView.Discover:
			tracks = Enumerable.Empty<TrackModel>();
			break;
		case LibraryView.Favorites:
			tracks = tracks.Where((TrackModel track) => track.IsFavorite);
			break;
		case LibraryView.Recent:
			tracks = tracks.Where((TrackModel track) => track.AddedAt >= DateTime.Now.AddDays(-30.0));
			break;
		case LibraryView.History:
			tracks = tracks.Where((TrackModel track) => track.LastPlayedAt.HasValue);
			break;
		case LibraryView.Category:
			if (_currentCategory != null)
			{
				tracks = tracks.Where((TrackModel track) => track.Categories.Contains<string>(_currentCategory, StringComparer.CurrentCultureIgnoreCase));
			}
			break;
		case LibraryView.Album:
			if (_currentAlbumKey != null)
			{
				tracks = tracks.Where((TrackModel track) => string.Equals(AlbumIdentity.Create(track), _currentAlbumKey, StringComparison.OrdinalIgnoreCase));
			}
			break;
		case LibraryView.Circle:
			if (_currentCircleKey != null)
			{
				tracks = tracks.Where((TrackModel track) => string.Equals(CircleIdentity.Create(track), _currentCircleKey, StringComparison.OrdinalIgnoreCase));
			}
			break;
		case LibraryView.Playlist:
			if (_currentPlaylist != null)
			{
				HashSet<string> ids = _currentPlaylist.TrackIds.ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
				tracks = tracks.Where((TrackModel track) => ids.Contains(track.Id));
			}
			break;
		}
		string query = SearchBox.Text.Trim().ToLowerInvariant();
		List<TrackModel> unfilteredTracks = tracks.ToList();
		_unfilteredTrackCount = unfilteredTracks.Count;
		tracks = unfilteredTracks;
		if (query.Length > 0)
		{
			tracks = tracks.Where((TrackModel track) => track.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
		}
		if (_trackSortKey != "RecommendationRank")
		{
			tracks = SortTracks(tracks);
		}
		_visibleTracks = tracks.ToList();
		TrackGrid.ItemsSource = _visibleTracks;
		UpdateTrackSortIndicator();
		int recordedPlaylistCount = ((_view == LibraryView.Playlist && _currentPlaylist != null) ? _currentPlaylist.TrackIds.Count : _unfilteredTrackCount);
		TrackCountText.Text = ((query.Length > 0) ? $"{_visibleTracks.Count:N0} / {_unfilteredTrackCount:N0} 首（搜索筛选）" : ((recordedPlaylistCount > _unfilteredTrackCount) ? $"{_unfilteredTrackCount:N0} 首可用 / {recordedPlaylistCount:N0} 首记录" : $"{_visibleTracks.Count:N0} 首"));
		LastPlayedColumn.Visibility = ((_view != LibraryView.History) ? Visibility.Collapsed : Visibility.Visible);
		PlayCountColumn.Visibility = ((_view != LibraryView.History) ? Visibility.Collapsed : Visibility.Visible);
		RecommendationReasonColumn.Visibility = (IsRecommendationPage ? Visibility.Visible : Visibility.Collapsed);
		RemoveFromPlaylistButton.Visibility = ((_view != LibraryView.Playlist) ? Visibility.Collapsed : Visibility.Visible);
		UpdatePlaylistHeader();
		UpdateEmptyTrackState();
	}

	private void UpdateEmptyTrackState()
	{
		if (_showingPlayerView || IsAlbumPage || IsCirclePage || IsDiscoveryPage || _visibleTracks.Count > 0)
		{
			EmptyTrackPanel.Visibility = Visibility.Collapsed;
			return;
		}
		bool hasSearch = !string.IsNullOrWhiteSpace(SearchBox.Text);
		EmptyTrackPanel.Visibility = Visibility.Visible;
		ClearTrackSearchButton.Visibility = ((!hasSearch) ? Visibility.Collapsed : Visibility.Visible);
		if (hasSearch && _unfilteredTrackCount > 0)
		{
			EmptyTrackTitleText.Text = "当前搜索没有匹配歌曲";
			EmptyTrackMessageText.Text = $"这个页面原有 {_unfilteredTrackCount:N0} 首歌曲。清除顶部搜索后即可全部显示。";
		}
		else if (_view == LibraryView.Playlist && _currentPlaylist != null)
		{
			if (_currentPlaylist.TrackIds.Count > 0)
			{
				EmptyTrackTitleText.Text = "歌单歌曲暂时无法对应到曲库";
				EmptyTrackMessageText.Text = $"歌单记录了 {_currentPlaylist.TrackIds.Count:N0} 首歌曲，但当前曲库没有找到对应文件。请先重新扫描曲库，再重新导入该歌单。";
			}
			else
			{
				EmptyTrackTitleText.Text = "这个歌单还是空的";
				EmptyTrackMessageText.Text = "可以从歌曲列表使用“添加到…”把歌曲加入这里。";
			}
		}
		else
		{
			EmptyTrackTitleText.Text = "没有可显示的歌曲";
			EmptyTrackMessageText.Text = (hasSearch ? "请尝试清除搜索或更换关键词。" : "请添加音乐文件夹并重新扫描曲库。");
		}
	}

	private void ApplyAlbumFilter()
	{
		IEnumerable<AlbumViewModel> albums = _albums;
		if (_showFavoriteAlbumsOnly)
		{
			albums = albums.Where((AlbumViewModel album) => album.IsFavorite);
		}
		string query = SearchBox.Text.Trim().ToLowerInvariant();
		if (query.Length > 0)
		{
			albums = albums.Where((AlbumViewModel album) => album.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
		}
		_filteredAlbums = albums.OrderBy<AlbumViewModel, string>((AlbumViewModel album) => album.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy<AlbumViewModel, string>((AlbumViewModel album) => album.Artist, StringComparer.CurrentCultureIgnoreCase).ToList();
		_albumCardLoadVersion++;
		_albumCards.Clear();
		AddAlbumCardBatch();
		TrackCountText.Text = $"{_albumCards.Count:N0} / {_filteredAlbums.Count:N0} 张专辑";
		UpdateAlbumFilterButtons();
		UpdateLibraryContentVisibility();
	}

	private void AddAlbumCardBatch()
	{
		if (_albumCards.Count >= _filteredAlbums.Count)
		{
			return;
		}
		int version = _albumCardLoadVersion;
		List<AlbumViewModel> next = _filteredAlbums.Skip(_albumCards.Count).Take(64).ToList();
		foreach (AlbumViewModel album in next)
		{
			_albumCards.Add(album);
		}
		TrackCountText.Text = $"{_albumCards.Count:N0} / {_filteredAlbums.Count:N0} 张专辑";
		QueueAlbumCoverLoads(next, version);
	}

	private void QueueAlbumCoverLoads(IEnumerable<AlbumViewModel> albums, int version)
	{
		foreach (AlbumViewModel album in albums)
		{
			DiagnosticLog.Observe(LoadAlbumCoverAsync(album, version), "ASSET", "Could not load an album cover");
		}
	}

	private async Task LoadAlbumCoverAsync(AlbumViewModel album, int version)
	{
		TrackModel track = album.RepresentativeTrack;
		if (track == null || album.CoverThumbnail != null)
		{
			return;
		}
		await _albumCoverLoadGate.WaitAsync();
		try
		{
			BitmapSource? cover = await Task.Run(() => CoverService.LoadThumbnail(track, 260));
			if (version == _albumCardLoadVersion)
			{
				album.CoverThumbnail = cover;
			}
		}
		finally
		{
			_albumCoverLoadGate.Release();
		}
	}

	private void AlbumGridList_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (IsAlbumPage && !(e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 420.0))
		{
			AddAlbumCardBatch();
		}
	}

	private void ApplyCircleFilter()
	{
		IEnumerable<CircleViewModel> circles = _circles;
		string query = SearchBox.Text.Trim().ToLowerInvariant();
		if (query.Length > 0)
		{
			circles = circles.Where((CircleViewModel circle) => circle.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
		}
		_filteredCircles = circles.OrderBy<CircleViewModel, string>((CircleViewModel circle) => circle.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		_circleCardLoadVersion++;
		_circleCards.Clear();
		AddCircleCardBatch();
		int unidentified = _state.Tracks.Count((TrackModel track) => string.IsNullOrWhiteSpace(track.Circle));
		CircleHintText.Text = ((unidentified > 0) ? $"按分组与专辑艺术家识别；另有 {unidentified:N0} 首未识别，可在歌曲右键菜单中设置" : "按分组与专辑艺术家识别；可在歌曲右键菜单中修正");
		UpdateLibraryContentVisibility();
	}

	private void AddCircleCardBatch()
	{
		if (_circleCards.Count >= _filteredCircles.Count)
		{
			return;
		}
		int version = _circleCardLoadVersion;
		List<CircleViewModel> next = _filteredCircles.Skip(_circleCards.Count).Take(64).ToList();
		foreach (CircleViewModel circle in next)
		{
			_circleCards.Add(circle);
		}
		TrackCountText.Text = $"{_circleCards.Count:N0} / {_filteredCircles.Count:N0} 个社团";
		foreach (CircleViewModel circle2 in next)
		{
			DiagnosticLog.Observe(LoadCircleCoverAsync(circle2, version), "ASSET", "Could not load a circle cover");
		}
	}

	private async Task LoadCircleCoverAsync(CircleViewModel circle, int version)
	{
		TrackModel track = circle.RepresentativeTrack;
		if (track == null || circle.CoverThumbnail != null)
		{
			return;
		}
		await _albumCoverLoadGate.WaitAsync();
		try
		{
			BitmapSource? cover = await Task.Run(() => CoverService.LoadThumbnail(track, 260));
			if (version == _circleCardLoadVersion)
			{
				circle.CoverThumbnail = cover;
			}
		}
		finally
		{
			_albumCoverLoadGate.Release();
		}
	}

	private void CircleGridList_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (IsCirclePage && !(e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 420.0))
		{
			AddCircleCardBatch();
		}
	}

	private void UpdateAlbumFilterButtons()
	{
		if (AllAlbumsFilterButton != null && FavoriteAlbumsFilterButton != null)
		{
			AllAlbumsFilterButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, _showFavoriteAlbumsOnly ? "AppBackgroundBrush" : "SelectionBrush");
			FavoriteAlbumsFilterButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, _showFavoriteAlbumsOnly ? "SelectionBrush" : "AppBackgroundBrush");
		}
	}

	private void UpdatePlaylistHeader()
	{
		bool showPlaylist = _view == LibraryView.Playlist && _currentPlaylist != null;
		StandardHeaderPanel.Visibility = (showPlaylist ? Visibility.Collapsed : Visibility.Visible);
		PlaylistHeaderPanel.Visibility = ((!showPlaylist) ? Visibility.Collapsed : Visibility.Visible);
		if (!showPlaylist || _currentPlaylist == null)
		{
			PlaylistHeaderCoverImage.Source = null;
			return;
		}
		PlaylistHeaderTitleText.Text = _currentPlaylist.Name;
		PlaylistHeaderDescriptionText.Text = _currentPlaylist.DescriptionPreview;
		PlaylistHeaderTagsText.Text = _currentPlaylist.TagsText;
		PlaylistHeaderMetaText.Text = _currentPlaylist.UpdatedText;
		PlaylistHeaderCoverImage.Source = _currentPlaylist.CoverThumbnail;
	}

	private void UpdateLibraryContentVisibility()
	{
		if (!_showingPlayerView)
		{
			UpdatePlaylistHeader();
			bool albumPage = IsAlbumPage;
			bool circlePage = IsCirclePage;
			bool discoveryPage = IsDiscoveryPage;
			bool recommendationPage = IsRecommendationPage;
			AlbumToolbar.Visibility = ((!albumPage) ? Visibility.Collapsed : Visibility.Visible);
			AlbumPanel.Visibility = ((!albumPage) ? Visibility.Collapsed : Visibility.Visible);
			CircleToolbar.Visibility = ((!circlePage) ? Visibility.Collapsed : Visibility.Visible);
			CirclePanel.Visibility = ((!circlePage) ? Visibility.Collapsed : Visibility.Visible);
			RecommendationToolbar.Visibility = ((discoveryPage || recommendationPage) ? Visibility.Visible : Visibility.Collapsed);
			RecommendationHomeButton.Visibility = (discoveryPage ? Visibility.Collapsed : Visibility.Visible);
			RefreshRecommendationButton.Visibility = (discoveryPage ? Visibility.Collapsed : Visibility.Visible);
			RecommendationPanel.Visibility = (discoveryPage ? Visibility.Visible : Visibility.Collapsed);
			LibraryToolbar.Visibility = ((albumPage || circlePage || discoveryPage || recommendationPage) ? Visibility.Collapsed : Visibility.Visible);
			TrackGrid.Visibility = ((albumPage || circlePage || discoveryPage) ? Visibility.Collapsed : Visibility.Visible);
			if (albumPage || circlePage || discoveryPage)
			{
				EmptyTrackPanel.Visibility = Visibility.Collapsed;
			}
			else
			{
				UpdateEmptyTrackState();
			}
		}
	}

	private void SelectLibraryView(LibraryView view, string title)
	{
		if (view != LibraryView.Recommendation && _trackSortKey == "RecommendationRank")
		{
			_trackSortKey = "ArtistAlbum";
			_trackSortDirection = ListSortDirection.Ascending;
			SyncSortCombo(_trackSortKey);
		}
		_view = view;
		_currentCategory = null;
		_currentAlbumKey = null;
		_currentCircleKey = null;
		_currentPlaylist = null;
		bool wasSuppressingNavigation = _suppressNavigation;
		_suppressNavigation = true;
		SidebarList.SelectedItem = FindCurrentSidebarItem();
		AlbumGridList.SelectedItem = null;
		CircleGridList.SelectedItem = null;
		_suppressNavigation = wasSuppressingNavigation;
		CurrentViewTitle.Text = title;
		ApplyFilter();
		ShowPlayerView(showPlayer: false);
	}

	private void AllMusicButton_Click(object sender, RoutedEventArgs e)
	{
		SelectLibraryView(LibraryView.All, "全部音乐");
	}

	private void FavoritesButton_Click(object sender, RoutedEventArgs e)
	{
		SelectLibraryView(LibraryView.Favorites, "我的收藏");
	}

	private void AlbumsButton_Click(object sender, RoutedEventArgs e)
	{
		SelectAlbumPage(showFavoritesOnly: false);
	}

	private void CirclesButton_Click(object sender, RoutedEventArgs e)
	{
		SelectCirclePage();
	}

	private void RecentButton_Click(object sender, RoutedEventArgs e)
	{
		SelectLibraryView(LibraryView.Recent, "最近 30 天添加");
	}

	private void HistoryButton_Click(object sender, RoutedEventArgs e)
	{
		_trackSortKey = "LastPlayedAt";
		_trackSortDirection = ListSortDirection.Descending;
		SyncSortCombo(_trackSortKey);
		SelectLibraryView(LibraryView.History, "播放历史");
	}

	private void SelectDiscoveryPage()
	{
		_view = LibraryView.Discover;
		_currentCategory = null;
		_currentAlbumKey = null;
		_currentCircleKey = null;
		_currentPlaylist = null;
		bool wasSuppressingNavigation = _suppressNavigation;
		_suppressNavigation = true;
		SidebarList.SelectedItem = FindCurrentSidebarItem();
		AlbumGridList.SelectedItem = null;
		CircleGridList.SelectedItem = null;
		_suppressNavigation = wasSuppressingNavigation;
		CurrentViewTitle.Text = "为你推荐";
		StatusText.Text = "七种本地预设；安心发现只会推荐能解释缘由的陌生歌曲";
		TrackCountText.Text = "仅在本机计算";
		RefreshRecommendationOverview();
		ShowPlayerView(showPlayer: false);
	}

	private void RefreshRecommendationOverview()
	{
		DiscoverTasteSummaryText.Text = RecommendationService.DescribeTaste(_state.Tracks, GetImplicitFavoriteTrackIds());
		DailyCardDateText.Text = DateTime.Now.ToString("MM/dd · 今日");
		RecommendationResult radar = GetRecommendation(RecommendationPreset.PersonalRadar);
		RecommendationResult daily = GetRecommendation(RecommendationPreset.DailyRecommendation);
		RecommendationResult roam = GetRecommendation(RecommendationPreset.PersonalRoam);
		RecommendationResult rediscover = GetRecommendation(RecommendationPreset.RediscoverFavorites);
		RecommendationResult unplayed = GetRecommendation(RecommendationPreset.UnplayedGems);
		RecommendationResult expansion = GetRecommendation(RecommendationPreset.FavoriteExpansion);
		RecommendationResult radio = GetRecommendation(RecommendationPreset.ThirtyMinuteRadio);
		RadarCardCountText.Text = RecommendationCountText(radar);
		DailyCardCountText.Text = RecommendationCountText(daily);
		RoamCardCountText.Text = RecommendationCountText(roam);
		RediscoverCardCountText.Text = RecommendationCountText(rediscover);
		UnplayedCardCountText.Text = RecommendationCountText(unplayed);
		ExpansionCardCountText.Text = RecommendationCountText(expansion);
		RadioCardCountText.Text = RecommendationCountText(radio);
	}

	private RecommendationResult GetRecommendation(RecommendationPreset preset)
	{
		if (!_recommendationCache.TryGetValue(preset, out RecommendationResult? result))
		{
			result = RecommendationService.Create(
				_state.Tracks,
				preset,
				DateTime.Now,
				_recommendationRefreshSalt,
				implicitFavoriteTrackIds: GetImplicitFavoriteTrackIds());
			_recommendationCache[preset] = result;
		}
		return result;
	}

	private HashSet<string> GetImplicitFavoriteTrackIds()
	{
		return _state.Playlists
			.Where(playlist => IsFavoritePlaylistName(playlist.Name))
			.SelectMany(playlist => playlist.TrackIds ?? new List<string>())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsFavoritePlaylistName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		string value = name.Trim();
		return value.Contains("喜欢的音乐", StringComparison.CurrentCultureIgnoreCase)
			|| value.Equals("我喜欢", StringComparison.CurrentCultureIgnoreCase)
			|| value.Contains("liked songs", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("favorites", StringComparison.OrdinalIgnoreCase)
			|| value.Contains("favourites", StringComparison.OrdinalIgnoreCase);
	}

	private static string RecommendationCountText(RecommendationResult result)
	{
		if (result.Tracks.Count == 0)
		{
			return "暂时没有足够可靠的候选";
		}
		if (result.Preset == RecommendationPreset.ThirtyMinuteRadio)
		{
			long durationMs = result.Tracks.Sum(track => track.DurationMs > 0 ? track.DurationMs : 210000L);
			return $"{result.Tracks.Count:N0} 首 · 约 {Math.Max(1, (int)Math.Round(TimeSpan.FromMilliseconds(durationMs).TotalMinutes))} 分钟";
		}
		return $"已挑选 {result.Tracks.Count:N0} 首 · {result.Tracks.Select(track => track.Artist).Distinct(StringComparer.CurrentCultureIgnoreCase).Count():N0} 位艺术家";
	}

	private void OpenRecommendation(RecommendationPreset preset, bool forceRefresh = false)
	{
		if (forceRefresh)
		{
			_recommendationRefreshSalt++;
			_recommendationCache.Remove(preset);
		}
		RecommendationResult result = GetRecommendation(preset);
		_activeRecommendationPreset = preset;
		_recommendationTracks = result.Tracks.ToList();
		foreach (TrackModel track in _recommendationTracks)
		{
			track.RecommendationReason = result.ReasonFor(track);
		}
		_view = LibraryView.Recommendation;
		_currentCategory = null;
		_currentAlbumKey = null;
		_currentCircleKey = null;
		_currentPlaylist = null;
		_trackSortKey = "RecommendationRank";
		_trackSortDirection = ListSortDirection.Ascending;
		SyncSortCombo(_trackSortKey);
		CurrentViewTitle.Text = result.Title;
		StatusText.Text = result.Insight;
		bool wasSuppressingNavigation = _suppressNavigation;
		_suppressNavigation = true;
		SidebarList.SelectedItem = FindCurrentSidebarItem();
		_suppressNavigation = wasSuppressingNavigation;
		ApplyFilter();
		ShowPlayerView(showPlayer: false);
	}

	private static bool TryGetRecommendationPreset(object? tag, out RecommendationPreset preset)
	{
		return Enum.TryParse(tag?.ToString(), ignoreCase: true, out preset);
	}

	private void RecommendationViewButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement element && TryGetRecommendationPreset(element.Tag, out RecommendationPreset preset))
		{
			OpenRecommendation(preset);
		}
	}

	private void RecommendationPlayButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement element && TryGetRecommendationPreset(element.Tag, out RecommendationPreset preset))
		{
			RecommendationResult result = GetRecommendation(preset);
			if (result.Tracks.Count == 0)
			{
				StatusText.Text = "曲库中还没有可用于推荐的普通音频";
				return;
			}
			PlayTracks(result.Tracks.ToList());
			StatusText.Text = $"正在播放“{result.Title}” · {result.Tracks.Count:N0} 首";
		}
	}

	private void RecommendationPresetButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement element && TryGetRecommendationPreset(element.Tag, out RecommendationPreset preset))
		{
			OpenRecommendation(preset);
		}
	}

	private void RecommendationHomeButton_Click(object sender, RoutedEventArgs e)
	{
		SelectDiscoveryPage();
	}

	private void RefreshRecommendationButton_Click(object sender, RoutedEventArgs e)
	{
		OpenRecommendation(_activeRecommendationPreset, forceRefresh: true);
		StatusText.Text = $"已为“{CurrentViewTitle.Text}”换一批本地推荐";
	}

	private void SelectAlbumPage(bool showFavoritesOnly)
	{
		_view = LibraryView.Albums;
		_showFavoriteAlbumsOnly = showFavoritesOnly;
		_suppressNavigation = true;
		SidebarList.SelectedItem = FindLibrarySidebarItem("Albums");
		AlbumGridList.SelectedItem = null;
		CircleGridList.SelectedItem = null;
		_suppressNavigation = false;
		_currentCategory = null;
		_currentAlbumKey = null;
		_currentCircleKey = null;
		_currentPlaylist = null;
		CurrentViewTitle.Text = (showFavoritesOnly ? "收藏专辑" : "专辑");
		ApplyAlbumFilter();
		ShowPlayerView(showPlayer: false);
	}

	private void SelectCirclePage()
	{
		_view = LibraryView.Circles;
		_currentCategory = null;
		_currentAlbumKey = null;
		_currentCircleKey = null;
		_currentPlaylist = null;
		bool wasSuppressingNavigation = _suppressNavigation;
		_suppressNavigation = true;
		SidebarList.SelectedItem = FindCurrentSidebarItem();
		AlbumGridList.SelectedItem = null;
		CircleGridList.SelectedItem = null;
		_suppressNavigation = wasSuppressingNavigation;
		CurrentViewTitle.Text = "社团";
		ApplyCircleFilter();
		ShowPlayerView(showPlayer: false);
	}

	private void SidebarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_suppressNavigation || !(SidebarList.SelectedItem is SidebarNavigationItem item))
		{
			return;
		}
		if (item.IsHeader)
		{
			_ = base.Dispatcher.BeginInvoke((Action)delegate
			{
				bool suppressNavigation = _suppressNavigation;
				_suppressNavigation = true;
				SidebarList.SelectedItem = FindCurrentSidebarItem();
				_suppressNavigation = suppressNavigation;
			});
			return;
		}
		switch (item.Kind)
		{
		case SidebarNavigationKind.LibraryItem:
			SelectLibrarySidebarItem(item.NavigationKey);
			break;
		case SidebarNavigationKind.Category:
			if (item.Category != null)
			{
				SelectCategoryTracks(item.Category);
			}
			break;
		case SidebarNavigationKind.Playlist:
			if (item.Playlist != null)
			{
				OpenPlaylist(item.Playlist, clearSearch: false);
			}
			break;
		case SidebarNavigationKind.CategoryHeader:
		case SidebarNavigationKind.PlaylistHeader:
			break;
		}
	}

	private void SelectLibrarySidebarItem(string key)
	{
		switch (key)
		{
		case "Discover":
			SelectDiscoveryPage();
			break;
		case "All":
			SelectLibraryView(LibraryView.All, "全部音乐");
			break;
		case "Favorites":
			SelectLibraryView(LibraryView.Favorites, "我的收藏");
			break;
		case "Albums":
			SelectAlbumPage(showFavoritesOnly: false);
			break;
		case "Circles":
			SelectCirclePage();
			break;
		case "Recent":
			SelectLibraryView(LibraryView.Recent, "最近 30 天添加");
			break;
		case "History":
			_trackSortKey = "LastPlayedAt";
			_trackSortDirection = ListSortDirection.Descending;
			SyncSortCombo(_trackSortKey);
			SelectLibraryView(LibraryView.History, "播放历史");
			break;
		}
	}

	private void SidebarSectionButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: SidebarNavigationItem item })
		{
			switch (item.Kind)
			{
			default:
				return;
			case SidebarNavigationKind.CategoryHeader:
				_sidebarCategoriesExpanded = !_sidebarCategoriesExpanded;
				break;
			case SidebarNavigationKind.PlaylistHeader:
				_sidebarPlaylistsExpanded = !_sidebarPlaylistsExpanded;
				break;
			case SidebarNavigationKind.Category:
			case SidebarNavigationKind.Playlist:
				return;
			}
			RefreshSidebarNavigation();
			e.Handled = true;
		}
	}

	private void SelectCategoryTracks(string category)
	{
		_suppressNavigation = true;
		SidebarList.SelectedItem = _sidebarItems.FirstOrDefault((SidebarNavigationItem item) => item.Kind == SidebarNavigationKind.Category && string.Equals(item.Category, category, StringComparison.CurrentCultureIgnoreCase));
		AlbumGridList.SelectedItem = null;
		CircleGridList.SelectedItem = null;
		_suppressNavigation = false;
		_view = LibraryView.Category;
		_currentCategory = category;
		_currentAlbumKey = null;
		_currentCircleKey = null;
		_currentPlaylist = null;
		CurrentViewTitle.Text = category;
		ApplyFilter();
	}

	private void AlbumGridList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_suppressNavigation && AlbumGridList.SelectedItem is AlbumViewModel album)
		{
			SelectAlbumTracks(album);
		}
	}

	private void CircleGridList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_suppressNavigation && CircleGridList.SelectedItem is CircleViewModel circle)
		{
			SelectCircleTracks(circle);
		}
	}

	private void SelectAlbumTracks(AlbumViewModel album)
	{
		_suppressNavigation = true;
		SidebarList.SelectedItem = FindLibrarySidebarItem("Albums");
		AlbumGridList.SelectedItem = null;
		CircleGridList.SelectedItem = null;
		_suppressNavigation = false;
		_view = LibraryView.Album;
		_currentAlbumKey = album.Key;
		_currentCategory = null;
		_currentCircleKey = null;
		_currentPlaylist = null;
		CurrentViewTitle.Text = album.DisplayName;
		ApplyFilter();
		ShowPlayerView(showPlayer: false);
	}

	private void SelectCircleTracks(CircleViewModel circle)
	{
		_suppressNavigation = true;
		SidebarList.SelectedItem = null;
		AlbumGridList.SelectedItem = null;
		CircleGridList.SelectedItem = null;
		_suppressNavigation = false;
		_view = LibraryView.Circle;
		_currentCircleKey = circle.Key;
		_currentCategory = null;
		_currentAlbumKey = null;
		_currentPlaylist = null;
		CurrentViewTitle.Text = "社团 · " + circle.Name;
		ApplyFilter();
		ShowPlayerView(showPlayer: false);
	}

	internal void OpenPlaylist(PlaylistModel playlist, bool clearSearch)
	{
		if (!_sidebarPlaylistsExpanded)
		{
			_sidebarPlaylistsExpanded = true;
			RefreshSidebarNavigation();
		}
		bool wasSuppressingNavigation = _suppressNavigation;
		_suppressNavigation = true;
		try
		{
			AlbumGridList.SelectedItem = null;
			CircleGridList.SelectedItem = null;
			SidebarList.SelectedItem = _sidebarItems.FirstOrDefault((SidebarNavigationItem item) => item.Kind == SidebarNavigationKind.Playlist && string.Equals(item.Playlist?.Id, playlist.Id, StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			_suppressNavigation = wasSuppressingNavigation;
		}
		_view = LibraryView.Playlist;
		_currentPlaylist = playlist;
		_currentCategory = null;
		_currentAlbumKey = null;
		_currentCircleKey = null;
		CurrentViewTitle.Text = playlist.Name;
		if (clearSearch && !string.IsNullOrEmpty(SearchBox.Text))
		{
			_suppressSearchRefresh = true;
			try
			{
				SearchBox.Clear();
			}
			finally
			{
				_suppressSearchRefresh = false;
			}
		}
		UpdatePlaylistHeader();
		ApplyFilter();
		ShowPlayerView(showPlayer: false);
	}

	private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (SearchHint != null)
		{
			SearchHint.Visibility = ((!string.IsNullOrEmpty(SearchBox.Text)) ? Visibility.Collapsed : Visibility.Visible);
		}
		if (!_suppressSearchRefresh)
		{
			if (IsAlbumPage)
			{
				ApplyAlbumFilter();
			}
			else if (IsCirclePage)
			{
				ApplyCircleFilter();
			}
			else
			{
				ApplyFilter();
			}
		}
	}

	private void ClearTrackSearchButton_Click(object sender, RoutedEventArgs e)
	{
		SearchBox.Clear();
		SearchBox.Focus();
	}

	private void AllAlbumsFilterButton_Click(object sender, RoutedEventArgs e)
	{
		SelectAlbumPage(showFavoritesOnly: false);
	}

	private void FavoriteAlbumsFilterButton_Click(object sender, RoutedEventArgs e)
	{
		SelectAlbumPage(showFavoritesOnly: true);
	}

	private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_syncingSortCombo && SortCombo.SelectedItem is ComboBoxItem { Tag: string sortKey })
		{
			_trackSortKey = sortKey;
			_trackSortDirection = DefaultSortDirection(sortKey);
			ApplyFilter();
		}
	}

	private void TrackGrid_Sorting(object sender, DataGridSortingEventArgs e)
	{
		string sortKey = e.Column.SortMemberPath;
		if (!string.IsNullOrWhiteSpace(sortKey))
		{
			e.Handled = true;
			_trackSortDirection = ((!string.Equals(_trackSortKey, sortKey, StringComparison.Ordinal)) ? DefaultSortDirection(sortKey) : ((_trackSortDirection == ListSortDirection.Ascending) ? ListSortDirection.Descending : ListSortDirection.Ascending));
			_trackSortKey = sortKey;
			SyncSortCombo(sortKey);
			ApplyFilter();
			string directionText = ((_trackSortDirection == ListSortDirection.Ascending) ? "升序" : "降序");
			StatusText.Text = $"按“{e.Column.Header}”{directionText}排列";
		}
	}

	private IEnumerable<TrackModel> SortTracks(IEnumerable<TrackModel> tracks)
	{
		bool descending = _trackSortDirection == ListSortDirection.Descending;
		StringComparer textComparer = StringComparer.CurrentCultureIgnoreCase;
		IOrderedEnumerable<TrackModel> ordered;
		if (_trackSortKey == "TrackNumber")
		{
			IOrderedEnumerable<TrackModel> numbered = tracks.OrderBy((TrackModel track) => track.TrackNumber == 0);
			ordered = (descending ? numbered.ThenByDescending((TrackModel track) => track.TrackNumber) : numbered.ThenBy((TrackModel track) => track.TrackNumber));
		}
		else if (_trackSortKey == "ArtistAlbum")
		{
			ordered = OrderByDirection<string>(tracks, (TrackModel track) => track.Artist, descending, textComparer);
			ordered = (descending ? ordered.ThenByDescending<TrackModel, string>((TrackModel track) => track.Album, textComparer).ThenByDescending((TrackModel track) => track.TrackNumber) : ordered.ThenBy<TrackModel, string>((TrackModel track) => track.Album, textComparer).ThenBy((TrackModel track) => track.TrackNumber));
		}
		else
		{
			ordered = _trackSortKey switch
			{
				"Title" => OrderByDirection<string>(tracks, (TrackModel track) => track.Title, descending, textComparer),
				"AddedAt" => OrderByDirection(tracks, (TrackModel track) => track.AddedAt, descending),
				"LastPlayedAt" => OrderByDirection(tracks, (TrackModel track) => track.LastPlayedAt, descending),
				"PlayCount" => OrderByDirection(tracks, (TrackModel track) => track.PlayCount, descending),
				"IsFavorite" => OrderByDirection(tracks, (TrackModel track) => track.IsFavorite, descending),
				"Artist" => OrderByDirection<string>(tracks, (TrackModel track) => track.Artist, descending, textComparer),
				"Album" => OrderByDirection<string>(tracks, (TrackModel track) => track.Album, descending, textComparer),
				"CircleText" => OrderByDirection<string>(tracks, (TrackModel track) => track.CircleText, descending, textComparer),
				"CategoryText" => OrderByDirection<string>(tracks, (TrackModel track) => track.CategoryText, descending, textComparer),
				"DurationMs" => OrderByDirection(tracks, (TrackModel track) => track.DurationMs, descending),
				"Format" => OrderByDirection<string>(tracks, (TrackModel track) => track.Format, descending, textComparer),
				_ => OrderByDirection<string>(tracks, (TrackModel track) => track.Title, descending, textComparer),
			};
		}
		return ordered.ThenBy<TrackModel, string>((TrackModel track) => track.Artist, textComparer).ThenBy<TrackModel, string>((TrackModel track) => track.Album, textComparer).ThenBy((TrackModel track) => track.TrackNumber)
			.ThenBy<TrackModel, string>((TrackModel track) => track.Title, textComparer)
			.ThenBy<TrackModel, string>((TrackModel track) => track.Id, StringComparer.OrdinalIgnoreCase);
	}

	private static IOrderedEnumerable<TrackModel> OrderByDirection<TKey>(IEnumerable<TrackModel> tracks, Func<TrackModel, TKey> selector, bool descending, IComparer<TKey>? comparer = null)
	{
		if (!descending)
		{
			return tracks.OrderBy(selector, comparer);
		}
		return tracks.OrderByDescending(selector, comparer);
	}

	private static ListSortDirection DefaultSortDirection(string sortKey)
	{
		bool flag;
		switch (sortKey)
		{
		case "AddedAt":
		case "LastPlayedAt":
		case "PlayCount":
		case "IsFavorite":
		case "DurationMs":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (!flag)
		{
			return ListSortDirection.Ascending;
		}
		return ListSortDirection.Descending;
	}

	private void SyncSortCombo(string sortKey)
	{
		_syncingSortCombo = true;
		try
		{
			SelectComboByTag(SortCombo, sortKey);
		}
		finally
		{
			_syncingSortCombo = false;
		}
	}

	private void UpdateTrackSortIndicator()
	{
		if (TrackGrid == null)
		{
			return;
		}
		foreach (DataGridColumn column in TrackGrid.Columns)
		{
			column.SortDirection = null;
		}
		string indicatorKey = ((_trackSortKey == "ArtistAlbum") ? "Artist" : _trackSortKey);
		DataGridColumn columnToMark = TrackGrid.Columns.FirstOrDefault((DataGridColumn column) => string.Equals(column.SortMemberPath, indicatorKey, StringComparison.Ordinal));
		if (columnToMark != null)
		{
			columnToMark.SortDirection = _trackSortDirection;
		}
	}

	private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFolderDialog dialog = new OpenFolderDialog
		{
			Title = "选择本地音乐总文件夹",
			InitialDirectory = (_state.LibraryRoots.FirstOrDefault(root => LibraryRootHealthStates.IsReachable(root.Health))?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic))
		};
		if (dialog.ShowDialog(this) == true)
		{
			if (!_state.LibraryFolders.Contains<string>(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
			{
				_state.LibraryFolders.Add(dialog.FolderName);
				SynchronizeLibraryRootsFromFolders();
			}
			await ScanLibraryAsync();
		}
	}

	private async void RescanButton_Click(object sender, RoutedEventArgs e)
	{
		if (_isScanning)
		{
			_scanCancellation?.Cancel();
			StatusText.Text = "正在取消扫描；已开始的文件读取会在完成后停止…";
			return;
		}
		await ScanLibraryAsync();
	}

	private async void ReidentifyCirclesButton_Click(object sender, RoutedEventArgs e)
	{
		await ScanLibraryAsync(forceMetadataRefresh: true);
		if (IsCirclePage)
		{
			ApplyCircleFilter();
		}
	}

	private async void CreatePlaylistButton_Click(object sender, RoutedEventArgs e)
	{
		PlaylistModel playlist = new PlaylistModel
		{
			Name = MakeUniquePlaylistName("新歌单")
		};
		if (TryShowOwnedDialog(() => new PlaylistDetailsWindow(playlist, null), "创建歌单", out var dialog) && ApplyPlaylistDetails(playlist, dialog))
		{
			_state.Playlists.Add(playlist);
			_currentPlaylist = playlist;
			await _store.SaveAsync(_state);
			RefreshNavigation();
			OpenPlaylist(playlist, clearSearch: true);
			StatusText.Text = "已创建歌单“" + playlist.Name + "”";
		}
	}

	private async void RenamePlaylistButton_Click(object sender, RoutedEventArgs e)
	{
		PlaylistModel playlist = (SidebarList.SelectedItem as SidebarNavigationItem)?.Playlist ?? _currentPlaylist;
		if (playlist != null)
		{
			await EditPlaylistAsync(playlist);
		}
	}

	private async void EditPlaylistButton_Click(object sender, RoutedEventArgs e)
	{
		if (_currentPlaylist != null)
		{
			await EditPlaylistAsync(_currentPlaylist);
		}
	}

	private async Task EditPlaylistAsync(PlaylistModel playlist)
	{
		if (TryShowOwnedDialog(() => new PlaylistDetailsWindow(playlist, playlist.CoverThumbnail), "编辑歌单", out var dialog) && ApplyPlaylistDetails(playlist, dialog))
		{
			await _store.SaveAsync(_state);
			RefreshNavigation();
			OpenPlaylist(playlist, clearSearch: false);
			CurrentViewTitle.Text = playlist.Name;
			UpdatePlaylistHeader();
			StatusText.Text = "已保存歌单“" + playlist.Name + "”的资料";
		}
	}

	private bool ApplyPlaylistDetails(PlaylistModel playlist, PlaylistDetailsWindow dialog)
	{
		string coverPath = playlist.CoverPath;
		try
		{
			if (dialog.RemoveCustomCover)
			{
				coverPath = "";
			}
			else if (!string.IsNullOrWhiteSpace(dialog.SelectedCoverFile))
			{
				Directory.CreateDirectory(_store.PlaylistArtworkDirectory);
				string extension = Path.GetExtension(dialog.SelectedCoverFile).ToLowerInvariant();
				switch (extension)
				{
				default:
					extension = ".image";
					break;
				case ".jpg":
				case ".jpeg":
				case ".png":
				case ".webp":
				case ".bmp":
					break;
				}
				string destination = Path.Combine(_store.PlaylistArtworkDirectory, playlist.Id + extension);
				if (!string.Equals(Path.GetFullPath(dialog.SelectedCoverFile), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
				{
					File.Copy(dialog.SelectedCoverFile, destination, overwrite: true);
				}
				coverPath = destination;
			}
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException) ? 1 : 0) != 0)
		{
			System.Windows.MessageBox.Show(this, ex.Message, "无法保存歌单封面", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return false;
		}
		playlist.Name = MakeUniquePlaylistName(dialog.PlaylistName, playlist.Id);
		playlist.Description = dialog.Description;
		playlist.Tags = dialog.Tags.ToList();
		playlist.CoverPath = coverPath;
		if (playlist.CreatedAt == default(DateTime))
		{
			playlist.CreatedAt = DateTime.Now;
		}
		playlist.UpdatedAt = DateTime.Now;
		playlist.InvalidateCover();
		playlist.NotifyMetadataChanged();
		return true;
	}

	private void PlaylistPlayAllButton_Click(object sender, RoutedEventArgs e)
	{
		if (_currentPlaylist != null)
		{
			PlayTracks(GetPlaylistTracks(_currentPlaylist));
		}
	}

	private async void DeletePlaylistButton_Click(object sender, RoutedEventArgs e)
	{
		PlaylistModel playlist = (SidebarList.SelectedItem as SidebarNavigationItem)?.Playlist ?? _currentPlaylist;
		if (playlist != null && System.Windows.MessageBox.Show(this, "删除歌单“" + playlist.Name + "”？\n不会删除任何音乐文件。", "删除歌单", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			_state.Playlists.Remove(playlist);
			SelectLibraryView(LibraryView.All, "全部音乐");
			await _store.SaveAsync(_state);
			RefreshNavigation();
		}
	}

	private async void AddToPlaylistButton_Click(object sender, RoutedEventArgs e)
	{
		List<TrackModel> selected = GetSelectedTracks();
		await AddTracksToPlaylistAsync(selected);
	}

	private async Task AddTracksToPlaylistAsync(IReadOnlyCollection<TrackModel> selected)
	{
		if (selected.Count == 0)
		{
			return;
		}
		List<TrackModel> tracks = selected.DistinctBy<TrackModel, string>((TrackModel trackModel) => trackModel.Id, StringComparer.OrdinalIgnoreCase).ToList();
		if (!TryShowOwnedDialog(() => new TrackDestinationWindow(_state.Playlists, tracks.Count), "添加歌曲", out var dialog))
		{
			return;
		}
		List<PlaylistModel> destinations = (from id in dialog.SelectedPlaylistIds
			select _state.Playlists.FirstOrDefault((PlaylistModel playlistModel) => string.Equals(playlistModel.Id, id, StringComparison.OrdinalIgnoreCase)) into playlistModel
			where playlistModel != null
			select playlistModel).Cast<PlaylistModel>().ToList();
		bool createdPlaylist = false;
		if (dialog.NewPlaylistName.Length > 0)
		{
			PlaylistModel playlist = _state.Playlists.FirstOrDefault((PlaylistModel item) => string.Equals(item.Name, dialog.NewPlaylistName, StringComparison.CurrentCultureIgnoreCase));
			if (playlist == null)
			{
				playlist = new PlaylistModel
				{
					Name = MakeUniquePlaylistName(dialog.NewPlaylistName),
					Description = $"创建时加入 {tracks.Count:N0} 首歌曲"
				};
				_state.Playlists.Add(playlist);
				createdPlaylist = true;
			}
			destinations.Add(playlist);
		}
		int addedEntries = 0;
		List<PlaylistModel> changedPlaylists = new List<PlaylistModel>();
		foreach (PlaylistModel playlist2 in destinations.DistinctBy<PlaylistModel, string>((PlaylistModel playlistModel) => playlistModel.Id, StringComparer.OrdinalIgnoreCase))
		{
			int before = playlist2.TrackIds.Count;
			foreach (TrackModel track in tracks)
			{
				if (!playlist2.TrackIds.Contains<string>(track.Id, StringComparer.OrdinalIgnoreCase))
				{
					playlist2.TrackIds.Add(track.Id);
				}
			}
			int added = playlist2.TrackIds.Count - before;
			if (added != 0)
			{
				addedEntries += added;
				playlist2.UpdatedAt = DateTime.Now;
				playlist2.InvalidateCover();
				playlist2.NotifyMetadataChanged();
				changedPlaylists.Add(playlist2);
			}
		}
		if (changedPlaylists.Count > 0 || createdPlaylist)
		{
			await _store.SaveAsync(_state);
			RefreshNavigation();
			if (_view == LibraryView.Playlist)
			{
				ApplyFilter();
			}
		}
		if (dialog.PlayNext)
		{
			QueueTracksNext(tracks);
		}
		int queued = (dialog.AppendToQueue ? AppendTracksToQueue(tracks) : 0);
		List<string> results = new List<string>();
		if (destinations.Count > 0)
		{
			results.Add($"{destinations.DistinctBy((PlaylistModel item) => item.Id).Count():N0} 个歌单新增 {addedEntries:N0} 条");
		}
		if (dialog.PlayNext)
		{
			results.Add("已安排下一首");
		}
		if (dialog.AppendToQueue)
		{
			results.Add($"队列新增 {queued:N0} 首");
		}
		StatusText.Text = ((results.Count == 0) ? "所选歌曲已在目标中" : string.Join(" · ", results));
	}

	private async void RemoveFromPlaylistButton_Click(object sender, RoutedEventArgs e)
	{
		if (_currentPlaylist == null)
		{
			return;
		}
		List<TrackModel> selected = GetSelectedTracks();
		if (selected.Count != 0)
		{
			HashSet<string> ids = selected.Select((TrackModel track) => track.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
			_currentPlaylist.TrackIds.RemoveAll(ids.Contains);
			_currentPlaylist.UpdatedAt = DateTime.Now;
			_currentPlaylist.InvalidateCover();
			_currentPlaylist.NotifyMetadataChanged();
			await _store.SaveAsync(_state);
			RefreshNavigation();
			ApplyFilter();
			StatusText.Text = $"已从歌单移出 {selected.Count:N0} 首歌曲";
		}
	}

	private async void AddCategoryButton_Click(object sender, RoutedEventArgs e)
	{
		List<TrackModel> selected = GetSelectedTracks();
		if (selected.Count == 0)
		{
			return;
		}
		string category = Prompt("歌曲分类", "分类名称");
		if (category == null)
		{
			return;
		}
		foreach (TrackModel track in selected)
		{
			if (!track.Categories.Contains<string>(category, StringComparer.CurrentCultureIgnoreCase))
			{
				track.Categories.Add(category);
			}
		}
		await _store.SaveAsync(_state);
		RefreshNavigation();
		TrackGrid.Items.Refresh();
		StatusText.Text = $"已为 {selected.Count} 首歌曲添加分类“{category}”";
	}

	private async void RemoveCategoryButton_Click(object sender, RoutedEventArgs e)
	{
		List<TrackModel> selected = GetSelectedTracks();
		if (selected.Count == 0)
		{
			return;
		}
		string category = _currentCategory ?? Prompt("移除分类", "要移除的分类名称");
		if (category == null)
		{
			return;
		}
		foreach (TrackModel item in selected)
		{
			item.Categories.RemoveAll((string value) => string.Equals(value, category, StringComparison.CurrentCultureIgnoreCase));
		}
		await _store.SaveAsync(_state);
		RefreshNavigation();
		if (_view == LibraryView.Category)
		{
			SelectLibraryView(LibraryView.All, "全部音乐");
		}
		else
		{
			TrackGrid.Items.Refresh();
		}
		StatusText.Text = $"已从 {selected.Count} 首歌曲移除分类“{category}”";
	}

	private async void ToggleFavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		List<TrackModel> selected = GetSelectedTracks();
		if (selected.Count == 0)
		{
			return;
		}
		bool favorite = !selected.All((TrackModel track) => track.IsFavorite);
		foreach (TrackModel item in selected)
		{
			item.IsFavorite = favorite;
		}
		await _store.SaveAsync(_state);
		if (_view == LibraryView.Favorites)
		{
			ApplyFilter();
		}
		else
		{
			TrackGrid.Items.Refresh();
		}
		StatusText.Text = (favorite ? $"已收藏 {selected.Count} 首歌曲" : $"已取消收藏 {selected.Count} 首歌曲");
	}

	private async void ToggleAlbumFavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		List<string> albumKeys = (from value in GetSelectedTracks().Select(AlbumIdentity.Create)
			where !string.IsNullOrWhiteSpace(value)
			select value).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		if (albumKeys.Count == 0 && _currentAlbumKey != null)
		{
			albumKeys.Add(_currentAlbumKey);
		}
		if (albumKeys.Count == 0)
		{
			return;
		}
		bool favorite = !albumKeys.All(IsFavoriteAlbum);
		foreach (string key in albumKeys)
		{
			if (favorite)
			{
				AddFavoriteAlbum(key);
			}
			else
			{
				RemoveFavoriteAlbum(key);
			}
		}
		await _store.SaveAsync(_state);
		RefreshNavigation();
		StatusText.Text = (favorite ? $"已收藏 {albumKeys.Count} 张专辑" : $"已取消收藏 {albumKeys.Count} 张专辑");
	}

	private async void FavoriteAlbumCardButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: var dataContext } && dataContext is AlbumViewModel album)
		{
			bool favorite = !IsFavoriteAlbum(album.Key);
			if (favorite)
			{
				AddFavoriteAlbum(album.Key);
			}
			else
			{
				RemoveFavoriteAlbum(album.Key);
			}
			await _store.SaveAsync(_state);
			RefreshNavigation();
			StatusText.Text = (favorite ? ("已收藏专辑“" + album.Title + "”") : ("已取消收藏专辑“" + album.Title + "”"));
			e.Handled = true;
		}
	}

	private async void ImportNetEaseButton_Click(object sender, RoutedEventArgs e)
	{
		string source = Prompt("导入网易云歌单", "粘贴公开歌单链接或歌单 ID");
		if (source == null)
		{
			return;
		}
		ImportNetEaseButton.IsEnabled = false;
		try
		{
			await SyncLibraryBeforeImportAsync();
			StatusText.Text = "正在读取网易云歌单并匹配本地歌曲…";
			NetEaseImportResult result = await _netEaseService.ImportAsync(source, _state.Tracks);
			PlaylistModel playlist = _state.Playlists.FirstOrDefault((PlaylistModel item) => item.CloudPlaylistId == result.PlaylistId);
			if (playlist == null)
			{
				playlist = new PlaylistModel
				{
					Name = MakeUniquePlaylistName(result.PlaylistName),
					Source = "netease",
					CloudPlaylistId = result.PlaylistId,
					Description = "从网易云歌单 " + result.PlaylistId + " 导入",
					Tags = new List<string>(1) { "网易云" }
				};
				_state.Playlists.Add(playlist);
			}
			else
			{
				playlist.Name = result.PlaylistName;
			}
			playlist.TrackIds = PlaylistMaintenance.BuildSynchronizedTrackIds(
				playlist.TrackIds,
				result.Matched,
				result.RemoteTrackIds,
				result.HasCompleteRemoteDetails,
				_state.Tracks);
			playlist.UpdatedAt = DateTime.Now;
			playlist.InvalidateCover();
			playlist.NotifyMetadataChanged();
			await _store.SaveAsync(_state);
			RefreshNavigation();
			OpenPlaylist(playlist, clearSearch: true);
			string missingPreview = string.Join("\n", from track in result.Missing.Take(8)
				select (!string.IsNullOrWhiteSpace(track.Title)) ? ("• " + track.Title + " - " + track.Artist) : ("• 网易云歌曲 ID " + track.Id + "（详情暂未返回）"));
			int ncmCount = _state.Tracks.Count((TrackModel track) => track.IsEncryptedNcm);
			string message = $"歌单：{result.PlaylistName}\n网易云声明歌曲：{result.DeclaredTrackCount}\n已取得完整歌曲 ID：{result.TrackIdCount}\n已读取歌曲详情：{result.ResolvedTrackCount}\n详情暂未返回：{result.UnresolvedTrackIds.Count}\n\n已同步本地文件：{_state.Tracks.Count}\n其中 NCM 文件：{ncmCount}\n云 ID 精确匹配：{result.ExactMatchCount}\n名称/艺术家/专辑匹配：{result.FuzzyMatchCount}\n修正历史错误云 ID：{result.CorrectedCloudIdCount}\n已匹配本地文件：{result.Matched.Count}\n已有详情但仍未匹配：{result.Missing.Count}";
			if (!result.HasCompleteTrackIds)
			{
				message += "\n\n警告：网易云没有返回完整歌曲 ID；本次仅保留仍能对应到本地曲库的原歌单内容，避免临时接口异常造成歌曲丢失。";
			}
			else if (result.UnresolvedTrackIds.Count > 0)
			{
				message += $"\n\n提示：有 {result.UnresolvedTrackIds.Count} 首暂未取得详情；本次会保留原歌单中尚未重新确认的本地歌曲，等下次详情完整后再安全清理。";
			}
			if (missingPreview.Length > 0)
			{
				message = message + "\n\n部分未匹配歌曲：\n" + missingPreview;
			}
			System.Windows.MessageBox.Show(this, message, "导入完成", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			StatusText.Text = $"已导入“{result.PlaylistName}”，匹配 {result.Matched.Count} / {result.DeclaredTrackCount} 首，详情暂缺 {result.UnresolvedTrackIds.Count} 首";
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = "网易云歌单导入已取消";
		}
		catch (Exception ex)
		{
			StatusText.Text = "网易云歌单导入失败";
			System.Windows.MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			ImportNetEaseButton.IsEnabled = true;
		}
	}

	private void NetEaseImportMenuButton_Click(object sender, RoutedEventArgs e)
	{
		if (ImportNetEaseButton.ContextMenu == null)
		{
			return;
		}
		ImportNetEaseButton.ContextMenu.PlacementTarget = ImportNetEaseButton;
		ImportNetEaseButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
		ImportNetEaseButton.ContextMenu.IsOpen = true;
	}

	private async void ImportNetEaseHistoryButton_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "导入网易云播放历史",
			Filter = "网易云播放历史|*.json;*.csv;*.tsv;*.txt|JSON 文件|*.json|CSV / TSV 文件|*.csv;*.tsv;*.txt|所有文件|*.*",
			Multiselect = false,
			CheckFileExists = true
		};
		if (dialog.ShowDialog(this) != true)
		{
			return;
		}

		ImportNetEaseButton.IsEnabled = false;
		try
		{
			await SyncLibraryBeforeImportAsync();
			StatusText.Text = "正在匹配网易云播放历史…";
			NetEaseHistoryImportResult result = await NetEaseHistoryImportService.ImportAsync(dialog.FileName, _state.Tracks);
			await _store.SaveAsync(_state);
			_recommendationCache.Clear();
			RefreshNavigation();
			if (IsRecommendationPage)
			{
				OpenRecommendation(_activeRecommendationPreset, forceRefresh: true);
			}
			else if (!IsAlbumPage && !IsCirclePage)
			{
				ApplyFilter();
			}

			string unmatchedPreview = string.Join("\n", result.Unmatched.Take(8).Select((NetEaseHistoryEntry item) =>
				"• " + item.Title + " - " + (string.IsNullOrWhiteSpace(item.Artist) ? "未知歌手" : item.Artist)));
			string message = $"读取历史记录：{result.SourceRecordCount:N0}\n匹配记录：{result.MatchedRecordCount:N0}\n更新本地歌曲：{result.MatchedTrackCount:N0}\n网易云 ID 精确匹配：{result.ExactMatchCount:N0}\n歌名 / 艺人保守匹配：{result.FuzzyMatchCount:N0}\n新增可信播放次数：{result.PlayCountIncrease:N0}\n更新最近播放时间：{result.LastPlayedUpdateCount:N0}\n未匹配：{result.Unmatched.Count:N0}（含歧义 {result.AmbiguousCount:N0}）";
			if (unmatchedPreview.Length > 0)
			{
				message = message + "\n\n部分未匹配记录：\n" + unmatchedPreview;
			}
			message += "\n\n播放历史已立即用于个性化推荐；重复导入不会重复累加。";
			System.Windows.MessageBox.Show(this, message, "播放历史导入完成", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			StatusText.Text = $"已导入网易云历史，匹配 {result.MatchedRecordCount:N0} / {result.SourceRecordCount:N0} 条";
		}
		catch (OperationCanceledException)
		{
			StatusText.Text = "网易云播放历史导入已取消";
		}
		catch (Exception ex)
		{
			StatusText.Text = "网易云播放历史导入失败";
			DiagnosticLog.Write("NetEaseHistoryImport", "播放历史导入失败。", ex);
			System.Windows.MessageBox.Show(this, ex.Message, "播放历史导入失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			ImportNetEaseButton.IsEnabled = true;
		}
	}

	private async Task SyncLibraryBeforeImportAsync()
	{
		SynchronizeLibraryRootsFromFolders();
		if (_state.LibraryFolders.Count == 0)
		{
			return;
		}
		if (_isScanning)
		{
			throw new InvalidOperationException("曲库正在扫描，请等待当前扫描完成后再导入歌单。");
		}
		_isScanning = true;
		CancellationTokenSource scanCancellation = new CancellationTokenSource();
		_scanCancellation = scanCancellation;
		RescanButton.IsEnabled = true;
		RescanButton.Content = "×";
		RescanButton.ToolTip = "取消导入前的曲库同步";
		AddFolderButton.IsEnabled = false;
		Progress<ScanProgress> progress = new Progress<ScanProgress>(delegate(ScanProgress value)
		{
			StatusText.Text = $"导入前同步音乐文件夹 {value.Scanned:N0} / {value.Total:N0}，元数据回退 {value.Errors:N0}";
			TrackCountText.Text = Path.GetFileName(value.CurrentFile);
		});
		try
		{
			bool hadIndexedTracks = _state.Tracks.Count > 0;
			AppState state = _state;
			state.Tracks = await _libraryService.ScanAsync(
				_state.LibraryFolders,
				_state.Tracks,
				progress,
				scanCancellation.Token);
			MarkReachableRootsScanned();
			await _store.SaveAsync(_state);
			bool confirmedEmptyLibrary = hadIndexedTracks && _state.Tracks.Count == 0 && AreAllRootsKnownReachable();
			RefreshNavigation(confirmedEmptyLibrary);
			if (!IsAlbumPage && !IsCirclePage)
			{
				ApplyFilter();
			}
		}
		finally
		{
			if (ReferenceEquals(_scanCancellation, scanCancellation))
			{
				_scanCancellation = null;
			}
			scanCancellation.Dispose();
			_isScanning = false;
			RescanButton.IsEnabled = true;
			RescanButton.Content = "↻";
			RescanButton.ToolTip = "重新扫描曲库";
			AddFolderButton.IsEnabled = true;
		}
	}

	private List<TrackModel> GetSelectedTracks()
	{
		List<TrackModel> selected = TrackGrid.SelectedItems.Cast<TrackModel>().ToList();
		if (selected.Count == 0 && TrackGrid.SelectedItem is TrackModel track)
		{
			selected.Add(track);
		}
		return selected;
	}

	private void TrackGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (!(e.OriginalSource is DependencyObject source))
		{
			return;
		}
		DataGridRow row = FindVisualParent<DataGridRow>(source);
		if (row != null)
		{
			if (!row.IsSelected)
			{
				TrackGrid.SelectedItems.Clear();
				row.IsSelected = true;
			}
			row.Focus();
		}
	}

	private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
	{
		while (source != null)
		{
			if (source is T target)
			{
				return target;
			}
			source = VisualTreeHelper.GetParent(source);
		}
		return null;
	}

	private static T? GetContextItem<T>(object sender) where T : class
	{
		if (!(sender is System.Windows.Controls.MenuItem { Parent: System.Windows.Controls.ContextMenu contextMenu }))
		{
			return null;
		}
		return (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as T;
	}

	private List<TrackModel> GetAlbumTracks(AlbumViewModel album)
	{
		return (from track in _state.Tracks
			where string.Equals(AlbumIdentity.Create(track), album.Key, StringComparison.OrdinalIgnoreCase)
			orderby track.TrackNumber
			select track).ThenBy<TrackModel, string>((TrackModel track) => track.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	private List<TrackModel> GetCircleTracks(CircleViewModel circle)
	{
		return _state.Tracks.Where((TrackModel track) => string.Equals(CircleIdentity.Create(track), circle.Key, StringComparison.OrdinalIgnoreCase)).OrderBy<TrackModel, string>((TrackModel track) => track.Album, StringComparer.CurrentCultureIgnoreCase).ThenBy((TrackModel track) => track.TrackNumber)
			.ThenBy<TrackModel, string>((TrackModel track) => track.Title, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}

	private List<TrackModel> GetPlaylistTracks(PlaylistModel playlist)
	{
		Dictionary<string, TrackModel> tracksById = _state.Tracks.GroupBy<TrackModel, string>((TrackModel track) => track.Id, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, TrackModel>, string, TrackModel>((IGrouping<string, TrackModel> group) => group.Key, (IGrouping<string, TrackModel> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		return (from id in playlist.TrackIds
			select tracksById.GetValueOrDefault(id) into track
			where track != null
			select track).Cast<TrackModel>().DistinctBy<TrackModel, string>((TrackModel track) => track.Id, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private void PlayTracks(IReadOnlyCollection<TrackModel> tracks)
	{
		_queue = tracks.DistinctBy<TrackModel, string>((TrackModel track) => track.Id, StringComparer.OrdinalIgnoreCase).ToList();
		if (_queue.Count != 0)
		{
			_queueIndex = 0;
			RefreshQueueLists();
			QueueList.SelectedItem = _queue[0];
			PlayTrack(_queue[0]);
		}
	}

	private void QueueTracksNext(IReadOnlyCollection<TrackModel> tracks)
	{
		List<TrackModel> additions = tracks.Where((TrackModel track) => _currentTrack == null || !string.Equals(track.Id, _currentTrack.Id, StringComparison.OrdinalIgnoreCase)).DistinctBy<TrackModel, string>((TrackModel track) => track.Id, StringComparer.OrdinalIgnoreCase).ToList();
		if (additions.Count != 0)
		{
			HashSet<string> additionIds = additions.Select((TrackModel track) => track.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
			List<TrackModel> remaining = _queue.Where((TrackModel track) => !additionIds.Contains(track.Id)).ToList();
			int currentIndex = ((_currentTrack == null) ? (-1) : remaining.FindIndex((TrackModel track) => string.Equals(track.Id, _currentTrack.Id, StringComparison.OrdinalIgnoreCase)));
			if (_currentTrack != null && currentIndex < 0)
			{
				remaining.Insert(0, _currentTrack);
				currentIndex = 0;
			}
			remaining.InsertRange(currentIndex + 1, additions);
			_queue = remaining;
			_queueIndex = currentIndex;
			RefreshQueueLists();
			CapturePlaybackSessionAndScheduleSave();
			StatusText.Text = $"已将 {additions.Count:N0} 首歌曲安排为下一首播放";
		}
	}

	private int AppendTracksToQueue(IReadOnlyCollection<TrackModel> tracks)
	{
		HashSet<string> existingIds = _queue.Select((TrackModel track) => track.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<TrackModel> additions = tracks.Where((TrackModel track) => existingIds.Add(track.Id)).ToList();
		if (additions.Count == 0)
		{
			return 0;
		}
		_queue.AddRange(additions);
		RefreshQueueLists();
		CapturePlaybackSessionAndScheduleSave();
		StatusText.Text = $"已向播放队列末尾添加 {additions.Count:N0} 首歌曲";
		return additions.Count;
	}

	private async Task ToggleTrackFavoritesAsync(IReadOnlyCollection<TrackModel> tracks)
	{
		if (tracks.Count == 0)
		{
			return;
		}
		bool favorite = !tracks.All((TrackModel track) => track.IsFavorite);
		foreach (TrackModel track in tracks)
		{
			track.IsFavorite = favorite;
		}
		_recommendationCache.Clear();
		await _store.SaveAsync(_state);
		if (_view == LibraryView.Favorites)
		{
			ApplyFilter();
		}
		else
		{
			TrackGrid.Items.Refresh();
		}
		StatusText.Text = (favorite ? $"已收藏 {tracks.Count:N0} 首歌曲" : $"已取消收藏 {tracks.Count:N0} 首歌曲");
	}

	private void CopyText(string text, string successMessage)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		try
		{
			System.Windows.Clipboard.SetText(text);
			StatusText.Text = successMessage;
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show(this, ex.Message, "无法写入剪贴板", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void CopyFilesForSharing(IEnumerable<TrackModel> tracks)
	{
		string[] paths = tracks.Select((TrackModel track) => track.FilePath).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct<string>(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (paths.Length == 0)
		{
			return;
		}
		try
		{
			System.Windows.DataObject dataObject = new System.Windows.DataObject();
			dataObject.SetData(System.Windows.DataFormats.FileDrop, paths);
			dataObject.SetText(string.Join(Environment.NewLine, paths));
			System.Windows.Clipboard.SetDataObject(dataObject, copy: true);
			StatusText.Text = $"已复制 {paths.Length:N0} 个歌曲文件，可直接粘贴分享";
		}
		catch (Exception ex)
		{
			System.Windows.MessageBox.Show(this, ex.Message, "无法复制歌曲文件", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	private void OpenTrackFolder(TrackModel? track)
	{
		if (track != null)
		{
			DiagnosticLog.Observe(OpenTrackFolderAsync(track), "NAS", "Could not open the track folder");
		}
	}

	private async Task OpenTrackFolderAsync(TrackModel track)
	{
		LibraryRootState? root = LibraryRootCatalog.FindOwningRoot(_state.LibraryRoots, track.FilePath);
		TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(root?.ProbeTimeoutSeconds ?? _state.NasProbeTimeoutSeconds, 1, 15));
		PathAvailabilityResult result = await _rootHealthService.ProbePathAsync(track.FilePath, timeout, _lifetimeCancellation.Token);
		if (!result.Reachable)
		{
			StatusText.Text = result.TimedOut ? "文件夹响应超时，未打开资源管理器" : "歌曲文件当前不可访问";
			return;
		}
		ProcessStartInfo explorer = new("explorer.exe")
		{
			UseShellExecute = false
		};
		explorer.ArgumentList.Add($"/select,{track.FilePath}");
		Process.Start(explorer);
	}

	private async Task<string?> FindSidecarSubtitleAsync(TrackModel track, TimeSpan timeout, CancellationToken cancellationToken)
	{
		if (!track.IsVideo)
		{
			return null;
		}
		string stem = Path.Combine(Path.GetDirectoryName(track.FilePath) ?? "", Path.GetFileNameWithoutExtension(track.FilePath));
		string[] candidates = [stem + ".ass", stem + ".srt", stem + ".vtt"];
		Task<PathAvailabilityResult>[] probes = candidates
			.Select(candidate => _rootHealthService.ProbePathAsync(candidate, timeout, cancellationToken))
			.ToArray();
		PathAvailabilityResult[] results = await Task.WhenAll(probes);
		for (int index = 0; index < candidates.Length; index++)
		{
			if (results[index].Reachable)
			{
				return candidates[index];
			}
		}
		return null;
	}

	private void TrackContextPlay_Click(object sender, RoutedEventArgs e)
	{
		List<TrackModel> tracks = GetSelectedTracks();
		if (tracks.Count > 0)
		{
			PlayFromVisibleTracks(tracks[0]);
		}
	}

	private void TrackContextPlayNext_Click(object sender, RoutedEventArgs e)
	{
		QueueTracksNext(GetSelectedTracks());
	}

	private void TrackContextAppendQueue_Click(object sender, RoutedEventArgs e)
	{
		AppendTracksToQueue(GetSelectedTracks());
	}

	private async void TrackContextFavorite_Click(object sender, RoutedEventArgs e)
	{
		await ToggleTrackFavoritesAsync(GetSelectedTracks());
	}

	private async void TrackContextAddPlaylist_Click(object sender, RoutedEventArgs e)
	{
		await AddTracksToPlaylistAsync(GetSelectedTracks());
	}

	private void TrackContextAddCategory_Click(object sender, RoutedEventArgs e)
	{
		AddCategoryButton_Click(sender, e);
	}

	private void TrackContextShare_Click(object sender, RoutedEventArgs e)
	{
		CopyFilesForSharing(GetSelectedTracks());
	}

	private void TrackContextCopyPath_Click(object sender, RoutedEventArgs e)
	{
		CopyText(string.Join(Environment.NewLine, from track in GetSelectedTracks()
			select track.FilePath), "已复制本地文件路径");
	}

	private void TrackContextCopyInfo_Click(object sender, RoutedEventArgs e)
	{
		CopyText(string.Join(Environment.NewLine, from track in GetSelectedTracks()
			select $"{track.Title} - {track.Artist} | {track.Album}{(string.IsNullOrWhiteSpace(track.Circle) ? "" : (" | " + track.Circle))}"), "已复制歌曲信息");
	}

	private void TrackContextOpenFolder_Click(object sender, RoutedEventArgs e)
	{
		OpenTrackFolder(GetSelectedTracks().FirstOrDefault());
	}

	private void TrackContextRemovePlaylist_Click(object sender, RoutedEventArgs e)
	{
		if (_currentPlaylist == null)
		{
			StatusText.Text = "当前页面不是歌单";
		}
		else
		{
			RemoveFromPlaylistButton_Click(sender, e);
		}
	}

	private void TrackContextOpenAlbum_Click(object sender, RoutedEventArgs e)
	{
		TrackModel track = GetSelectedTracks().FirstOrDefault();
		AlbumViewModel album = ((track == null) ? null : _albums.FirstOrDefault((AlbumViewModel item) => string.Equals(item.Key, AlbumIdentity.Create(track), StringComparison.OrdinalIgnoreCase)));
		if (album != null)
		{
			SelectAlbumTracks(album);
		}
	}

	private void TrackContextOpenCircle_Click(object sender, RoutedEventArgs e)
	{
		TrackModel track = GetSelectedTracks().FirstOrDefault();
		if (track == null || string.IsNullOrWhiteSpace(track.Circle))
		{
			StatusText.Text = "这首歌尚未识别社团，可用右键“设置/修正社团”";
			return;
		}
		CircleViewModel circle = _circles.FirstOrDefault((CircleViewModel item) => string.Equals(item.Key, CircleIdentity.Create(track), StringComparison.OrdinalIgnoreCase));
		if (circle != null)
		{
			SelectCircleTracks(circle);
		}
	}

	private async void TrackContextSetCircle_Click(object sender, RoutedEventArgs e)
	{
		List<TrackModel> tracks = GetSelectedTracks();
		if (tracks.Count == 0)
		{
			return;
		}
		string initial = ((tracks.Select((TrackModel track) => track.Circle).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).Count() == 1) ? tracks[0].Circle : "");
		string circle = Prompt("设置社团", $"为选中的 {tracks.Count:N0} 首歌曲设置社团名称", initial);
		if (circle == null)
		{
			return;
		}
		foreach (TrackModel item in tracks)
		{
			item.Circle = circle.Trim();
			item.CircleIsManual = true;
		}
		await _store.SaveAsync(_state);
		RefreshNavigation();
		if (!IsAlbumPage && !IsCirclePage)
		{
			ApplyFilter();
		}
		TrackGrid.Items.Refresh();
		StatusText.Text = $"已为 {tracks.Count:N0} 首歌曲设置社团“{circle.Trim()}”";
	}

	private async void TrackContextClearCircle_Click(object sender, RoutedEventArgs e)
	{
		List<TrackModel> tracks = GetSelectedTracks();
		if (tracks.Count == 0)
		{
			return;
		}
		foreach (TrackModel item in tracks)
		{
			item.Circle = "";
			item.CircleIsManual = true;
		}
		await _store.SaveAsync(_state);
		RefreshNavigation();
		if (!IsAlbumPage && !IsCirclePage)
		{
			ApplyFilter();
		}
		TrackGrid.Items.Refresh();
		StatusText.Text = $"已清除 {tracks.Count:N0} 首歌曲的社团信息";
	}

	private void AlbumContextOpen_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album != null)
		{
			SelectAlbumTracks(album);
		}
	}

	private void AlbumContextPlay_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album != null)
		{
			PlayTracks(GetAlbumTracks(album));
		}
	}

	private void AlbumContextPlayNext_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album != null)
		{
			QueueTracksNext(GetAlbumTracks(album));
		}
	}

	private async void AlbumContextFavorite_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album != null)
		{
			if (IsFavoriteAlbum(album.Key))
			{
				RemoveFavoriteAlbum(album.Key);
			}
			else
			{
				AddFavoriteAlbum(album.Key);
			}
			await _store.SaveAsync(_state);
			RefreshNavigation();
			StatusText.Text = (IsFavoriteAlbum(album.Key) ? ("已收藏专辑“" + album.Title + "”") : ("已取消收藏专辑“" + album.Title + "”"));
		}
	}

	private async void AlbumContextSetCircle_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album == null)
		{
			return;
		}
		List<TrackModel> tracks = GetAlbumTracks(album);
		List<string> circles = (from track in tracks
			where !string.IsNullOrWhiteSpace(track.Circle)
			select track.Circle.Trim()).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).ToList();
		string initial = ((circles.Count == 1) ? circles[0] : "");
		string circle = Prompt("设置专辑社团", $"为专辑“{album.Title}”的 {tracks.Count:N0} 首歌曲设置社团", initial);
		if (circle == null)
		{
			return;
		}
		foreach (TrackModel item in tracks)
		{
			item.Circle = circle.Trim();
			item.CircleIsManual = true;
		}
		await _store.SaveAsync(_state);
		RefreshNavigation();
		if (!IsAlbumPage && !IsCirclePage)
		{
			ApplyFilter();
		}
		TrackGrid.Items.Refresh();
		StatusText.Text = ((circle.Trim().Length == 0) ? ("已清除专辑“" + album.Title + "”的社团信息") : $"已将专辑“{album.Title}”归入社团“{circle.Trim()}”");
	}

	private async void AlbumContextAddPlaylist_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album != null)
		{
			await AddTracksToPlaylistAsync(GetAlbumTracks(album));
		}
	}

	private void AlbumContextShare_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album != null)
		{
			CopyFilesForSharing(GetAlbumTracks(album));
		}
	}

	private void AlbumContextCopy_Click(object sender, RoutedEventArgs e)
	{
		AlbumViewModel album = GetContextItem<AlbumViewModel>(sender);
		if (album != null)
		{
			CopyText($"{album.Title} - {album.Artist} | {album.TrackCount:N0} 首", "已复制专辑信息");
		}
	}

	private void AlbumContextOpenFolder_Click(object sender, RoutedEventArgs e)
	{
		OpenTrackFolder(GetContextItem<AlbumViewModel>(sender)?.RepresentativeTrack);
	}

	private void CircleContextOpen_Click(object sender, RoutedEventArgs e)
	{
		CircleViewModel circle = GetContextItem<CircleViewModel>(sender);
		if (circle != null)
		{
			SelectCircleTracks(circle);
		}
	}

	private void CircleContextPlay_Click(object sender, RoutedEventArgs e)
	{
		CircleViewModel circle = GetContextItem<CircleViewModel>(sender);
		if (circle != null)
		{
			PlayTracks(GetCircleTracks(circle));
		}
	}

	private void CircleContextPlayNext_Click(object sender, RoutedEventArgs e)
	{
		CircleViewModel circle = GetContextItem<CircleViewModel>(sender);
		if (circle != null)
		{
			QueueTracksNext(GetCircleTracks(circle));
		}
	}

	private async void CircleContextAddPlaylist_Click(object sender, RoutedEventArgs e)
	{
		CircleViewModel circle = GetContextItem<CircleViewModel>(sender);
		if (circle != null)
		{
			await AddTracksToPlaylistAsync(GetCircleTracks(circle));
		}
	}

	private void CircleContextShare_Click(object sender, RoutedEventArgs e)
	{
		CircleViewModel circle = GetContextItem<CircleViewModel>(sender);
		if (circle != null)
		{
			CopyFilesForSharing(GetCircleTracks(circle));
		}
	}

	private void CircleContextCopy_Click(object sender, RoutedEventArgs e)
	{
		CircleViewModel circle = GetContextItem<CircleViewModel>(sender);
		if (circle != null)
		{
			CopyText(circle.Name, "已复制社团名称");
		}
	}

	private void CircleContextOpenFolder_Click(object sender, RoutedEventArgs e)
	{
		OpenTrackFolder(GetContextItem<CircleViewModel>(sender)?.RepresentativeTrack);
	}

	private bool IsFavoriteAlbum(string albumKey)
	{
		return _state.FavoriteAlbumKeys.Contains<string>(albumKey, StringComparer.OrdinalIgnoreCase);
	}

	private void AddFavoriteAlbum(string albumKey)
	{
		if (!IsFavoriteAlbum(albumKey))
		{
			_state.FavoriteAlbumKeys.Add(albumKey);
		}
	}

	private void RemoveFavoriteAlbum(string albumKey)
	{
		_state.FavoriteAlbumKeys.RemoveAll((string key) => string.Equals(key, albumKey, StringComparison.OrdinalIgnoreCase));
	}

	private string? Prompt(string title, string prompt, string initialValue = "")
	{
		if (!TryShowOwnedDialog(() => new TextPromptWindow(title, prompt, initialValue), title, out var window))
		{
			return null;
		}
		return window.Value;
	}

	private bool TryShowOwnedDialog<TWindow>(Func<TWindow> createDialog, string operationName, out TWindow dialog) where TWindow : Window
	{
		dialog = null!;
		try
		{
			dialog = createDialog();
			TWindow val = dialog;
			if (val.Owner == null)
			{
				Window window;
				Window owner = (window = this);
				val.Owner = owner;
			}
			return dialog.ShowDialog() == true;
		}
		catch (Exception exception) when (!App.IsFatalException(exception))
		{
			DiagnosticLog.Write("UI", operationName + " dialog failed", exception);
			StatusText.Text = operationName + "暂时无法完成，播放器已保持运行";
			try
			{
				System.Windows.MessageBox.Show(this, "“" + operationName + "”暂时无法完成，但播放器仍可继续使用。\n详细信息已写入日志。", "操作未完成", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			catch (Exception exception2)
			{
				DiagnosticLog.Write("UI", "Could not display dialog failure notice", exception2);
			}
			return false;
		}
	}

	internal void ReportRecoverableUiException()
	{
		StatusText.Text = "刚才的界面操作发生异常，播放器已保持运行";
		System.Windows.MessageBox.Show(this, "刚才的界面操作发生异常，但播放器仍可继续使用。\n详细信息已写入日志。", "操作未完成", MessageBoxButton.OK, MessageBoxImage.Exclamation);
	}

	private string MakeUniquePlaylistName(string requested, string? excludedId = null)
	{
		string candidate;
		string clean = (candidate = requested.Trim());
		int suffix = 2;
		while (_state.Playlists.Any((PlaylistModel playlist) => playlist.Id != excludedId && string.Equals(playlist.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
		{
			candidate = $"{clean} ({suffix++})";
		}
		return candidate;
	}

	private void TrackGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (TrackGrid.SelectedItem is TrackModel track)
		{
			if (string.Equals(_state.DoubleClickQueueMode, "Append", StringComparison.OrdinalIgnoreCase))
			{
				AppendToQueueAndPlay(track);
			}
			else
			{
				PlayFromVisibleTracks(track);
			}
		}
	}

	private void PlayTrackButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: TrackModel track })
		{
			PlayFromVisibleTracks(track);
			e.Handled = true;
		}
	}

	private async void AddTrackDestinationButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: TrackModel track })
		{
			await AddTracksToPlaylistAsync(new[] { track });
			e.Handled = true;
		}
	}

	private async void FavoriteTrackButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { DataContext: var dataContext } && dataContext is TrackModel track)
		{
			track.IsFavorite = !track.IsFavorite;
			_recommendationCache.Clear();
			await _store.SaveAsync(_state);
			if (_view == LibraryView.Favorites && !track.IsFavorite)
			{
				ApplyFilter();
			}
			else
			{
				TrackGrid.Items.Refresh();
			}
			StatusText.Text = (track.IsFavorite ? ("已收藏“" + track.Title + "”") : ("已取消收藏“" + track.Title + "”"));
			e.Handled = true;
		}
	}

	private void PlayFromVisibleTracks(TrackModel track)
	{
		_queue = _visibleTracks.ToList();
		_queueIndex = _queue.FindIndex((TrackModel item) => item.Id == track.Id);
		RefreshQueueLists();
		PlayTrack(track);
	}

	private void AppendToQueueAndPlay(TrackModel track)
	{
		int index = _queue.FindIndex((TrackModel item) => string.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			_queue.Add(track);
			index = _queue.Count - 1;
		}
		_queueIndex = index;
		RefreshQueueLists();
		QueueList.SelectedItem = track;
		PlayTrack(track);
	}

	private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		object selectedItem = QueueList.SelectedItem;
		TrackModel track = selectedItem as TrackModel;
		if (track != null)
		{
			_queueIndex = _queue.FindIndex((TrackModel item) => item.Id == track.Id);
			PlayTrack(track);
		}
	}

	private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_currentTrack == null && TrackGrid.SelectedItem is TrackModel track)
		{
			ShowTrackInformation(track, updateNowPlaying: false);
		}
	}

	private void PlayTrack(TrackModel track)
	{
		CancelRootWait();
		int requestVersion = Interlocked.Increment(ref _playTrackRequestVersion);
		DiagnosticLog.Observe(PlayTrackAsync(track, requestVersion), "PLAY", "Could not complete the asynchronous play request");
	}

	private async Task PlayTrackAsync(TrackModel track, int requestVersion)
	{
		if (track.IsEncryptedNcm)
		{
			StatusText.Text = "这首歌已在曲库和歌单中，但 NCM 加密文件需要先转换才能播放";
			System.Windows.MessageBox.Show(this, "该歌曲是网易云 NCM 加密文件。程序会把它计入曲库、搜索结果和导入歌单，但必须先转换为 FLAC、MP3 等普通音频格式才能播放。\n\n转换完成后点击“重新扫描”，歌单导入会优先匹配可播放版本。", "NCM 需要转换", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}

		LibraryRootState? root = LibraryRootCatalog.FindOwningRoot(_state.LibraryRoots, track.FilePath);
		TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(root?.ProbeTimeoutSeconds ?? _state.NasProbeTimeoutSeconds, 1, 15));
		if (root != null && (LibraryRootKinds.IsReconnectable(root.RootKind) || root.RootKind == LibraryRootKinds.Unknown))
		{
			LibraryRootProbeResult rootResult = await _rootHealthService.ProbeAsync(root, timeout, _lifetimeCancellation.Token);
			if (requestVersion != Volatile.Read(ref _playTrackRequestVersion) || _shuttingDown)
			{
				return;
			}
			ApplyRootProbeResult(rootResult);
			if (!rootResult.Reachable)
			{
				if (_state.WaitForOfflineRoots && (LibraryRootKinds.IsReconnectable(root.RootKind) || root.RootKind == LibraryRootKinds.Unknown))
				{
					await WaitForRootAndResumeAsync(track, root, Math.Max(0, _pendingSeekMs ?? 0), recoverExistingPlayer: false, requestVersion);
				}
				else
				{
					StatusText.Text = rootResult.Health == LibraryRootHealthStates.NeedsCredentials
						? "曲库目录需要 Windows 网络凭据，播放未开始"
						: "曲库目录当前离线，播放未开始";
				}
				return;
			}
		}

		PathAvailabilityResult availability = await _rootHealthService.ProbePathAsync(track.FilePath, timeout, _lifetimeCancellation.Token);
		if (requestVersion != Volatile.Read(ref _playTrackRequestVersion) || _shuttingDown)
		{
			return;
		}
		if (!availability.Reachable)
		{
			StatusText.Text = root != null && LibraryRootHealthStates.IsReachable(root.Health)
				? "根目录在线，但歌曲文件已移动或不存在；请重新扫描曲库"
				: "文件不存在或当前不可访问；请检查磁盘后重新扫描";
			return;
		}

		string? sidecarSubtitlePath = await FindSidecarSubtitleAsync(track, timeout, _lifetimeCancellation.Token);
		if (requestVersion != Volatile.Read(ref _playTrackRequestVersion) || _shuttingDown)
		{
			return;
		}
		StartAvailableTrack(track, _pendingSeekMs, sidecarSubtitlePath);
	}

	private void StartAvailableTrack(TrackModel track, long? resumeAtMilliseconds = null, string? sidecarSubtitlePath = null)
	{
		try
		{
			if (_currentTrack != null && !string.Equals(_currentTrack.Id, track.Id, StringComparison.OrdinalIgnoreCase))
			{
				RememberCurrentPlayback();
			}
			_waitingRootId = null;
			_waitingResumePositionMs = 0;
			if (resumeAtMilliseconds.HasValue && resumeAtMilliseconds.Value > 0)
			{
				_pendingSeekMs = resumeAtMilliseconds.Value;
				_pendingSeekTrackId = track.Id;
			}
			else
			{
				_pendingSeekMs = null;
				_pendingSeekTrackId = null;
			}
			if (_showingPlayerView || track.IsVideo || (!_state.SafePlaybackMode && !string.Equals(_state.VisualizationMode, "Off", StringComparison.OrdinalIgnoreCase)))
			{
				ShowPlayerView(showPlayer: true, track);
			}
			_playback.Play(track.FilePath, _state, track.IsVideo, sidecarSubtitlePath);
			_currentTrack = track;
			base.Title = track.Title + " - " + track.Artist + " · 本地音乐库";
			_taskbarHoverPreview?.UpdateTrack(track, null);
			_watchdogTrackId = track.Id;
			_watchdogRecoveryCount = 0;
			_watchdogRecoveryResumeAt = -1L;
			_playbackRecoveryInProgress = false;
			track.PlayCount++;
			track.LastPlayedAt = DateTime.Now;
			_recommendationCache.Clear();
			RememberRecentlyPlayed(track.Id);
			int loadVersion = ++_nowPlayingLoadVersion;
			ResetNowPlayingAssets(track);
			PlayPauseButton.Content = "Ⅱ";
			ImmersivePlayPauseButton.Content = "Ⅱ";
			_desktopLyrics?.UpdatePlayState(isPlaying: true);
			_previewPlayer?.UpdateTrack(track, null);
			UpdateAuxiliaryPlayerState();
			StatusText.Text = "正在播放，正在后台读取封面和歌词……";
			DesktopLyricsWindow? desktopLyrics = _desktopLyrics;
			if (desktopLyrics != null && desktopLyrics.IsVisible)
			{
				desktopLyrics.UpdateLyrics(track.Title, track.Artist);
			}
			DiagnosticLog.Observe(LoadNowPlayingAssetsAsync(track, loadVersion), "ASSET", "Could not finish loading now-playing assets");
			ScheduleStateSave();
			if (_view == LibraryView.History && !_showingPlayerView)
			{
				_ = base.Dispatcher.BeginInvoke(new Action(ApplyFilter));
			}
		}
		catch (Exception ex)
		{
			StatusText.Text = "播放失败";
			System.Windows.MessageBox.Show(this, ex.Message, "无法播放", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void ResetNowPlayingAssets(TrackModel track)
	{
		_lyrics = new List<LyricLine>();
		_currentLyricIndex = -1;
		RefreshLyricsLists();
		InPlayerOriginalText.Text = "";
		InPlayerTranslationText.Text = "";
		InPlayerTertiaryText.Text = "";
		VinylCurrentOriginalText.Text = "";
		VinylCurrentTranslationText.Text = "";
		VinylCurrentTertiaryText.Text = "";
		VinylLyricHintText.Text = "正在读取歌词……";
		InPlayerSubtitlePanel.Visibility = (!_state.InPlayerBilingualSubtitles) ? Visibility.Collapsed : Visibility.Visible;
		ShowTrackInformation(track, updateNowPlaying: true);
		PlayerMediaSummaryText.Text = $"{track.Title}  ·  {track.MediaTypeText} / {track.Format}";
	}

	private async Task WaitForRootAndResumeAsync(
		TrackModel track,
		LibraryRootState root,
		long resumeAtMilliseconds,
		bool recoverExistingPlayer,
		int requestVersion)
	{
		CancelRootWait();
		CancellationTokenSource waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
		_rootWaitCancellation = waitCancellation;
		_rootReconnectInProgress = true;
		_waitingRootId = root.RootId;
		_waitingResumePositionMs = Math.Max(0, resumeAtMilliseconds);
		int attempt = 0;
		int onlineButMissingCount = 0;
		try
		{
			if (!recoverExistingPlayer)
			{
				if (_currentTrack != null && !string.Equals(_currentTrack.Id, track.Id, StringComparison.OrdinalIgnoreCase))
				{
					RememberCurrentPlayback();
				}
				_playback.Stop();
				_currentTrack = track;
				ResetNowPlayingAssets(track);
				int loadVersion = ++_nowPlayingLoadVersion;
				DiagnosticLog.Observe(LoadNowPlayingAssetsAsync(track, loadVersion), "ASSET", "Could not load cached assets while waiting for a root");
			}
			else
			{
				_playback.Stop();
			}
			PlayPauseButton.Content = "▶";
			ImmersivePlayPauseButton.Content = "▶";
			RememberCurrentPlayback();
			ScheduleStateSave();

			while (!waitCancellation.IsCancellationRequested && !_shuttingDown)
			{
				TimeSpan delay = LibraryRootRetrySchedule.GetDelay(attempt);
				StatusText.Text = root.Health == LibraryRootHealthStates.NeedsCredentials
					? $"等待 Windows 恢复 NAS 凭据连接；{delay.TotalSeconds:0} 秒后重试"
					: $"曲库目录离线，保留队列与 {FormatTime(resumeAtMilliseconds)} 位置；{delay.TotalSeconds:0} 秒后重试";
				await Task.Delay(delay, waitCancellation.Token);
				LibraryRootProbeResult result = await _rootHealthService.ProbeAsync(
					root,
					TimeSpan.FromSeconds(Math.Clamp(root.ProbeTimeoutSeconds, 1, 15)),
					waitCancellation.Token);
				if (requestVersion != Volatile.Read(ref _playTrackRequestVersion))
				{
					return;
				}
				ApplyRootProbeResult(result);
				attempt++;
				if (!result.Reachable)
				{
					continue;
				}
				PathAvailabilityResult fileResult = await _rootHealthService.ProbePathAsync(
					track.FilePath,
					TimeSpan.FromSeconds(Math.Clamp(root.ProbeTimeoutSeconds, 1, 15)),
					waitCancellation.Token);
				if (!fileResult.Reachable)
				{
					if (fileResult.TimedOut)
					{
						continue;
					}
					onlineButMissingCount++;
					if (onlineButMissingCount < 3)
					{
						continue;
					}
					RememberCurrentPlayback();
					_waitingRootId = null;
					_waitingResumePositionMs = 0;
					_state.PlaybackSession.WaitingRootId = null;
					StatusText.Text = "NAS 已恢复，但歌曲文件已移动或删除；队列已保留，请重新扫描";
					return;
				}

				_waitingRootId = null;
				_waitingResumePositionMs = 0;
				string? sidecarSubtitlePath = await FindSidecarSubtitleAsync(
					track,
					TimeSpan.FromSeconds(Math.Clamp(root.ProbeTimeoutSeconds, 1, 15)),
					waitCancellation.Token);
				if (recoverExistingPlayer)
				{
					bool recovered = await _playback.RecoverAsync(track.FilePath, _state, track.IsVideo, Math.Max(0, resumeAtMilliseconds), sidecarSubtitlePath, waitCancellation.Token);
					StatusText.Text = recovered ? "NAS 已恢复，已从原位置继续播放" : "NAS 已恢复，但播放引擎恢复失败";
				}
				else
				{
					StartAvailableTrack(track, Math.Max(0, resumeAtMilliseconds), sidecarSubtitlePath);
					StatusText.Text = "曲库目录已恢复，已从保存位置继续播放";
				}
				RememberCurrentPlayback();
				ScheduleStateSave();
				return;
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (ReferenceEquals(_rootWaitCancellation, waitCancellation))
			{
				_rootWaitCancellation = null;
				_rootReconnectInProgress = false;
			}
			waitCancellation.Dispose();
		}
	}

	private void CancelRootWait()
	{
		CancellationTokenSource? cancellation = Interlocked.Exchange(ref _rootWaitCancellation, null);
		try
		{
			cancellation?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		if (cancellation != null)
		{
			_rootReconnectInProgress = false;
		}
	}

	private void ShowTrackInformation(TrackModel track, bool updateNowPlaying)
	{
		if (updateNowPlaying)
		{
			CoverImage.Source = null;
			MiniCoverImage.Source = null;
			ImmersiveBackgroundImage.Source = null;
			StandardVinylCoverImage.Source = null;
			VinylFocusCoverImage.Source = null;
			LyricsModeCoverImage.Source = null;
			ImmersiveMiniCoverImage.Source = null;
			CoverFallback.Visibility = Visibility.Visible;
			MiniCoverFallback.Visibility = Visibility.Visible;
			NowTitleText.Text = track.Title;
			NowArtistText.Text = track.Artist;
			BottomTitleText.Text = track.Title;
			BottomArtistText.Text = track.Artist;
			ImmersiveTitleText.Text = track.Title;
			ImmersiveAlbumText.Text = "专辑：" + DisplayValue(track.Album, "未知专辑");
			ImmersiveArtistText.Text = "歌手：" + DisplayValue(track.Artist, "未知艺术家");
			ImmersiveSourceText.Text = "来源：本地文件";
			VinylModeTitleText.Text = track.Title;
			VinylModeArtistText.Text = DisplayValue(track.Artist, "未知艺术家") + "  ·  " + DisplayValue(track.Album, "未知专辑");
			LyricsModeTitleText.Text = track.Title;
			LyricsModeArtistText.Text = DisplayValue(track.Artist, "未知艺术家") + "  ·  " + DisplayValue(track.Album, "未知专辑");
			ImmersiveBottomTitleText.Text = track.Title;
			ImmersiveBottomArtistText.Text = DisplayValue(track.Artist, "未知艺术家");
			PositionSlider.Maximum = Math.Max(1L, track.DurationMs);
			DurationText.Text = FormatTime(track.DurationMs);
			ImmersivePositionSlider.Maximum = PositionSlider.Maximum;
			ImmersivePositionSlider.Value = 0.0;
			ImmersiveElapsedText.Text = "0:00";
			ImmersiveDurationText.Text = DurationText.Text;
			IReadOnlyList<SimilarTrackSuggestion> suggestions = PlayerPageService.FindSimilarTracks(_state.Tracks, track, 10);
			ImmersiveSimilarList.ItemsSource = suggestions;
			ImmersiveSimilarEmptyText.Visibility = suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		}
		InfoTitleText.Text = track.Title;
		InfoArtistText.Text = track.Artist;
		InfoAlbumText.Text = track.Album;
		InfoCategoryText.Text = ((track.Categories.Count == 0) ? "未分类" : track.CategoryText);
		InfoPathText.Text = track.FilePath;
		MediaInfoText.Text = (track.IsVideo ? "正在读取视频分辨率、帧率、音轨和编码信息…" : "正在读取采样率、声道、码率和编码信息…");
		ImmersiveInfoSummaryText.Text = PlayerPageService.BuildLocalEncyclopediaSummary(track);
		ImmersiveInfoArtistValueText.Text = DisplayValue(track.Artist, "未知艺术家");
		ImmersiveInfoAlbumValueText.Text = DisplayValue(track.Album, "未知专辑");
		ImmersiveInfoSourceValueText.Text = Path.GetFileName(track.FilePath) + "  ·  " + DisplayValue(track.Format, "未知格式");
		ImmersiveInfoCategoryValueText.Text = track.Categories.Count == 0 ? "未分类" : track.CategoryText;
		ImmersiveTechnicalText.Text = track.IsVideo ? "正在读取视频流信息……" : "正在读取音频流信息……";
	}

	private async Task LoadNowPlayingAssetsAsync(TrackModel track, int loadVersion)
	{
		try
		{
			Task<BitmapSource?> coverTask = Task.Run(() => CoverService.LoadThumbnail(track, 720));
			Task<List<LyricLine>> lyricsTask = Task.Run(() => LyricsService.LoadForTrack(track));
			InlineArray2<Task> buffer = default(InlineArray2<Task>);
			buffer[0] = coverTask;
			buffer[1] = lyricsTask;
			await Task.WhenAll(buffer);
			if (loadVersion == _nowPlayingLoadVersion && string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
			{
				BitmapSource? cover = await coverTask;
				CoverImage.Source = cover;
				MiniCoverImage.Source = cover;
				ImmersiveBackgroundImage.Source = cover;
				StandardVinylCoverImage.Source = cover;
				VinylFocusCoverImage.Source = cover;
				LyricsModeCoverImage.Source = cover;
				ImmersiveMiniCoverImage.Source = cover;
				_previewPlayer?.UpdateTrack(track, cover);
				_taskbarHoverPreview?.UpdateTrack(track, cover);
				CoverFallback.Visibility = ((cover != null) ? Visibility.Collapsed : Visibility.Visible);
				MiniCoverFallback.Visibility = ((cover != null) ? Visibility.Collapsed : Visibility.Visible);
				_lyrics = await lyricsTask;
				_currentLyricIndex = -1;
				RefreshLyricsLists();
				VinylLyricHintText.Text = _lyrics.Count == 0 ? "暂未找到歌词" : "歌词会随播放自动滚动";
				StatusText.Text = ((_lyrics.Count == 0) ? "正在播放，未找到同名 LRC 歌词" : $"正在播放 · {_lyrics.Count} 行歌词");
			}
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("ASSET", "Could not load cover or lyrics for '" + track.FilePath + "'", exception);
			if (loadVersion == _nowPlayingLoadVersion)
			{
				StatusText.Text = "正在播放；封面或歌词读取失败，不影响音频播放";
			}
		}
	}

	private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
	{
		TogglePlayback();
	}

	private void BottomFavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		ToggleCurrentTrackFavorite();
	}

	private void OpenQueueButton_Click(object sender, RoutedEventArgs e)
	{
		PlayerInfoTabs.SelectedItem = QueueTabItem;
	}

	private async void AddCurrentTrackButton_Click(object sender, RoutedEventArgs e)
	{
		if (_currentTrack == null)
		{
			StatusText.Text = "请先选择或播放一首歌曲";
		}
		else
		{
			await AddTracksToPlaylistAsync(new[] { _currentTrack });
		}
	}

	private void TogglePlayback()
	{
		if (_currentTrack == null)
		{
			if (_visibleTracks.Count != 0)
			{
				_queue = _visibleTracks.ToList();
				_queueIndex = 0;
				RefreshQueueLists();
				PlayTrack(_queue[0]);
			}
		}
		else
		{
			_playback.TogglePause();
			PlayPauseButton.Content = (_playback.IsPlaying ? "Ⅱ" : "▶");
			ImmersivePlayPauseButton.Content = PlayPauseButton.Content;
			_desktopLyrics?.UpdatePlayState(_playback.IsPlaying);
			UpdateAuxiliaryPlayerState();
		}
	}

	private void PreviousButton_Click(object sender, RoutedEventArgs e)
	{
		GoPrevious();
	}

	private void GoPrevious()
	{
		if (_playback.Time > 4000)
		{
			_playback.Seek(0L);
		}
		else
		{
			PlayQueueOffset(-1, allowWrap: true);
		}
	}

	private void NextButton_Click(object sender, RoutedEventArgs e)
	{
		GoNext();
	}

	private void GoNext()
	{
		PlayQueueOffset(1, _state.RepeatMode == "All");
	}

	private void PlayQueueOffset(int offset, bool allowWrap)
	{
		if (_queue.Count == 0)
		{
			return;
		}
		if (_state.ShuffleMode != "Off" && _queue.Count > 1)
		{
			TrackModel selected = ShuffleService.Choose(_queue, _currentTrack, _state.ShuffleMode, _random, _recentTrackIds);
			_queueIndex = _queue.FindIndex((TrackModel track) => track.Id == selected.Id);
		}
		else
		{
			int next = _queueIndex + offset;
			if (next < 0 || next >= _queue.Count)
			{
				if (!allowWrap)
				{
					_playback.Stop();
					PlayPauseButton.Content = "▶";
					ImmersivePlayPauseButton.Content = "▶";
					UpdateAuxiliaryPlayerState();
					return;
				}
				next = (next + _queue.Count) % _queue.Count;
			}
			_queueIndex = next;
		}
		PlayTrack(_queue[_queueIndex]);
		QueueList.SelectedItem = _queue[_queueIndex];
		QueueList.ScrollIntoView(_queue[_queueIndex]);
		ImmersiveQueueList.SelectedItem = _queue[_queueIndex];
		ImmersiveQueueList.ScrollIntoView(_queue[_queueIndex]);
	}

	private void HandleTrackEnded()
	{
		if (_state.RepeatMode == "One" && _currentTrack != null)
		{
			PlayTrack(_currentTrack);
		}
		else
		{
			PlayQueueOffset(1, _state.RepeatMode == "All");
		}
	}

	private async void ShuffleButton_Click(object sender, RoutedEventArgs e)
	{
		int current = Array.IndexOf(ShuffleService.Modes, _state.ShuffleMode);
		_state.ShuffleMode = ShuffleService.Modes[(Math.Max(0, current) + 1) % ShuffleService.Modes.Length];
		_state.ShuffleEnabled = _state.ShuffleMode != "Off";
		UpdatePlaybackModeButtons();
		RememberCurrentPlayback();
		await _store.SaveAsync(_state);
	}

	private async void ShuffleModeMenuItem_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.MenuItem { Tag: string mode } && ShuffleService.Modes.Contains(mode))
		{
			_state.ShuffleMode = mode;
			_state.ShuffleEnabled = mode != "Off";
			UpdatePlaybackModeButtons();
			RememberCurrentPlayback();
			await _store.SaveAsync(_state);
		}
	}

	private async void RepeatButton_Click(object sender, RoutedEventArgs e)
	{
		AppState state = _state;
		string repeatMode = _state.RepeatMode;
		string repeatMode2 = ((repeatMode == "All") ? "One" : ((!(repeatMode == "One")) ? "All" : "Off"));
		state.RepeatMode = repeatMode2;
		UpdatePlaybackModeButtons();
		RememberCurrentPlayback();
		await _store.SaveAsync(_state);
	}

	private void UpdatePlaybackModeButtons()
	{
		ShuffleButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, _state.ShuffleMode != "Off" ? "SelectionBrush" : "AppBackgroundBrush");
		ImmersiveShuffleButton.Background = _state.ShuffleMode != "Off"
			? new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 201, 183, 255))
			: new SolidColorBrush(System.Windows.Media.Color.FromArgb(22, 255, 255, 255));
		System.Windows.Controls.Button shuffleButton = ShuffleButton;
		shuffleButton.ToolTip = _state.ShuffleMode switch
		{
			"Uniform" => "当前：均匀随机。队列中每首媒体概率相同；单击切换，右键直接选择模式。",
			"Smart" => "当前：智能随机。减少近期播放、同艺术家和同专辑连续出现；单击切换，右键选择。",
			"Album" => "当前：随机专辑。先随机选择其他专辑，再播放其中一首；单击切换，右键选择。",
			"Artist" => "当前：随机艺术家。先随机选择其他艺术家，再播放其作品；单击切换，右键选择。",
			"LeastPlayed" => "当前：优先未听。优先选择播放次数最少的媒体；单击切换，右键选择。",
			_ => "当前：关闭随机，按队列顺序播放；单击切换，右键直接选择模式。",
		};
		shuffleButton = RepeatButton;
		System.Windows.Controls.Button repeatButton = RepeatButton;
		string repeatMode = _state.RepeatMode;
		(string, string) tuple = ((repeatMode == "One") ? ("↻1", "单曲循环") : ((!(repeatMode == "Off")) ? ("↻", "列表循环") : ("→", "顺序播放，播完停止")));
		(string, string) tuple2 = tuple;
		object item = tuple2.Item1;
		object item2 = tuple2.Item2;
		shuffleButton.Content = item;
		repeatButton.ToolTip = item2;
		ImmersiveRepeatButton.Content = item;
		ImmersiveRepeatButton.ToolTip = item2;
	}

	private void PlayerTimer_Tick(object? sender, EventArgs e)
	{
		if (_currentTrack != null)
		{
			long current = _playback.Time;
			long duration = ((_playback.Length > 0) ? _playback.Length : _currentTrack.DurationMs);
			PositionSlider.Maximum = Math.Max(1L, duration);
			ImmersivePositionSlider.Maximum = PositionSlider.Maximum;
			if (!_seeking)
			{
				double position = Math.Clamp(current, 0.0, PositionSlider.Maximum);
				_syncingPositionControls = true;
				try
				{
					PositionSlider.Value = position;
					ImmersivePositionSlider.Value = position;
				}
				finally
				{
					_syncingPositionControls = false;
				}
			}
			ElapsedText.Text = FormatTime(current);
			DurationText.Text = FormatTime(duration);
			ImmersiveElapsedText.Text = ElapsedText.Text;
			ImmersiveDurationText.Text = DurationText.Text;
			_previewPlayer?.UpdateProgress(current, duration);
			PlayPauseButton.Content = (_playback.IsPlaying ? "Ⅱ" : "▶");
			ImmersivePlayPauseButton.Content = PlayPauseButton.Content;
			_desktopLyrics?.UpdatePlayState(_playback.IsPlaying);
			UpdateAuxiliaryPlayerState();
			UpdateCurrentLyric(current);
			UpdateImmersiveTonearm(current, duration);
			CheckPlaybackWatchdog(current, duration);
		}
	}

	private void CheckPlaybackWatchdog(long current, long duration)
	{
		if (_playback.IsPlaying && _watchdogRecoveryCount > 0 && PlaybackStabilityService.HasStableProgressAfterRecovery(current, _watchdogRecoveryResumeAt))
		{
			DiagnosticLog.Write("WATCHDOG", $"Playback remained stable after recovery; resetting recovery budget at {current} ms");
			_watchdogRecoveryCount = 0;
			_watchdogRecoveryResumeAt = -1L;
		}
		int timeoutSeconds = PlaybackStabilityService.EffectiveWatchdogTimeoutSeconds(_state, _watchdogRecoveryCount);
		if (_state.PlaybackRecoveryEnabled && _currentTrack != null && !_playbackRecoveryInProgress && !_rootReconnectInProgress && _playback.IsPlaying && (duration <= 0 || duration - current >= 2500) && !(DateTime.UtcNow - _playback.LastProgressUtc < TimeSpan.FromSeconds(timeoutSeconds)))
		{
			DiagnosticLog.Observe(RecoverPlaybackAsync($"播放时间超过 {timeoutSeconds} 秒没有前进"), "WATCHDOG", "Playback watchdog recovery task failed");
		}
	}

	private async Task RecoverPlaybackAsync(string reason)
	{
		TrackModel track = _currentTrack;
		if (track == null || _playbackRecoveryInProgress || _rootReconnectInProgress || _shuttingDown)
		{
			return;
		}
		int requestVersion = Volatile.Read(ref _playTrackRequestVersion);
		if (!string.Equals(_watchdogTrackId, track.Id, StringComparison.OrdinalIgnoreCase))
		{
			_watchdogTrackId = track.Id;
			_watchdogRecoveryCount = 0;
			_watchdogRecoveryResumeAt = -1L;
		}
		long resumeAt = Math.Max(0L, _playback.Time);
		LibraryRootState? root = LibraryRootCatalog.FindOwningRoot(_state.LibraryRoots, track.FilePath);
		if (root != null && (LibraryRootKinds.IsReconnectable(root.RootKind) || root.RootKind == LibraryRootKinds.Unknown))
		{
			_rootReconnectInProgress = true;
			try
			{
				TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(root.ProbeTimeoutSeconds, 1, 15));
				LibraryRootProbeResult rootResult = await _rootHealthService.ProbeAsync(root, timeout, _lifetimeCancellation.Token);
				if (requestVersion != Volatile.Read(ref _playTrackRequestVersion) || !string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				ApplyRootProbeResult(rootResult);
				if (!rootResult.Reachable)
				{
					if (_state.WaitForOfflineRoots && (LibraryRootKinds.IsReconnectable(root.RootKind) || root.RootKind == LibraryRootKinds.Unknown))
					{
						DiagnosticLog.Write("NAS", $"{reason}; root unavailable, entering WaitingForRoot without rebuilding LibVLC");
						await WaitForRootAndResumeAsync(track, root, resumeAt, recoverExistingPlayer: true, requestVersion);
					}
					else
					{
						_playback.Stop();
						StatusText.Text = "曲库目录离线；已停止播放，未重建解码器";
					}
					return;
				}
				PathAvailabilityResult fileResult = await _rootHealthService.ProbePathAsync(track.FilePath, timeout, _lifetimeCancellation.Token);
				if (requestVersion != Volatile.Read(ref _playTrackRequestVersion) || !string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				if (!fileResult.Reachable)
				{
					if (_state.WaitForOfflineRoots && fileResult.TimedOut)
					{
						await WaitForRootAndResumeAsync(track, root, resumeAt, recoverExistingPlayer: true, requestVersion);
					}
					else
					{
						_playback.Stop();
						StatusText.Text = "根目录在线，但当前歌曲文件不存在；未重建解码器";
					}
					return;
				}
			}
			finally
			{
				if (_rootWaitCancellation == null)
				{
					_rootReconnectInProgress = false;
				}
			}
		}
		int maximumAttempts = Math.Clamp(_state.PlaybackRecoveryAttempts, 1, 5);
		if (_watchdogRecoveryCount >= maximumAttempts)
		{
			DiagnosticLog.Write("WATCHDOG", $"Giving up after {maximumAttempts} recoveries: '{track.FilePath}'");
			if (_state.SkipTrackAfterRecoveryFailure)
			{
				StatusText.Text = "这首歌曲连续恢复失败，已跳到下一首";
				GoNext();
			}
			else
			{
				_playback.Stop();
				StatusText.Text = "这首歌曲连续恢复失败，播放已停止";
			}
			return;
		}
		_playbackRecoveryInProgress = true;
		_watchdogRecoveryCount++;
		_watchdogRecoveryResumeAt = resumeAt;
		StatusText.Text = $"检测到播放停滞，正在恢复（{_watchdogRecoveryCount}/{maximumAttempts}）……";
		DiagnosticLog.Write("WATCHDOG", $"{reason}; track='{track.FilePath}', time={resumeAt}");
		try
		{
			if (requestVersion != Volatile.Read(ref _playTrackRequestVersion) || !string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			string? sidecarSubtitlePath = await FindSidecarSubtitleAsync(
				track,
				TimeSpan.FromSeconds(Math.Clamp(root?.ProbeTimeoutSeconds ?? _state.NasProbeTimeoutSeconds, 1, 15)),
				_lifetimeCancellation.Token);
			if (requestVersion != Volatile.Read(ref _playTrackRequestVersion) || !string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			bool recovered = await _playback.RecoverAsync(track.FilePath, _state, track.IsVideo, resumeAt, sidecarSubtitlePath);
			if (string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
			{
				StatusText.Text = (recovered ? "播放已自动恢复并从原位置继续" : "播放恢复失败；界面仍可操作，将再次尝试");
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("WATCHDOG", "Playback recovery failed", exception);
			StatusText.Text = "播放恢复失败；界面仍可操作，将再次尝试";
		}
		finally
		{
			_playbackRecoveryInProgress = false;
		}
	}

	private void ScheduleStateSave()
	{
		_stateSaveTimer.Stop();
		_stateSaveTimer.Start();
	}

	private void CapturePlaybackSessionAndScheduleSave()
	{
		RememberCurrentPlayback();
		ScheduleStateSave();
	}

	private async void StateSaveTimer_Tick(object? sender, EventArgs e)
	{
		_stateSaveTimer.Stop();
		try
		{
			RememberCurrentPlayback();
			await _store.SaveAsync(_state);
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("STATE", "Deferred state save failed", exception);
		}
	}

	private void UpdateCurrentLyric(long currentTime)
	{
		int index = LyricsService.FindCurrentIndex(_lyrics, currentTime, _state.LyricOffsetMs);
		if (index == _currentLyricIndex)
		{
			return;
		}
		if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
		{
			_lyrics[_currentLyricIndex].IsCurrent = false;
		}
		_currentLyricIndex = index;
		if (index >= 0 && index < _lyrics.Count)
		{
			LyricLine line = _lyrics[index];
			line.IsCurrent = true;
			SelectAndRevealLyric(line);
			RenderLyricLine(line);
		}
	}

	private void RenderLyricLine(LyricLine line)
	{
		LyricsDisplayContent content = LyricsDisplayService.Resolve(line, _state.LyricsDisplayMode);
		InPlayerOriginalText.Text = content.Primary;
		InPlayerTranslationText.Text = content.Secondary;
		InPlayerTertiaryText.Text = content.Tertiary;
		ApplyInPlayerLyricStyles(content);
		InPlayerTranslationText.Visibility = string.IsNullOrWhiteSpace(content.Secondary) ? Visibility.Collapsed : Visibility.Visible;
		InPlayerTertiaryText.Visibility = string.IsNullOrWhiteSpace(content.Tertiary) ? Visibility.Collapsed : Visibility.Visible;
		InPlayerSubtitlePanel.Visibility = _state.InPlayerBilingualSubtitles ? Visibility.Visible : Visibility.Collapsed;
		VinylCurrentOriginalText.Text = content.Primary;
		VinylCurrentTranslationText.Text = content.Secondary;
		VinylCurrentTertiaryText.Text = content.Tertiary;
		ApplyVinylCurrentLyricStyles(content);
		VinylCurrentTranslationText.Visibility = string.IsNullOrWhiteSpace(content.Secondary) ? Visibility.Collapsed : Visibility.Visible;
		VinylCurrentTertiaryText.Visibility = string.IsNullOrWhiteSpace(content.Tertiary) ? Visibility.Collapsed : Visibility.Visible;
		VinylLyricHintText.Text = string.IsNullOrWhiteSpace(content.Primary) ? "歌词会随播放自动滚动" : "当前歌词";
		DesktopLyricsWindow? desktopLyrics = _desktopLyrics;
		if (desktopLyrics != null && desktopLyrics.IsVisible)
		{
			desktopLyrics.UpdateLyrics(line);
		}
	}

	private void ApplyInPlayerLyricStyles(LyricsDisplayContent content)
	{
		if (_state.InPlayerSubtitlesUseLyricsStyle)
		{
			ApplyInPlayerLyricStyle(InPlayerOriginalText, NormalizeLyricKind(content.PrimaryKind), allowOriginalGradient: true);
			ApplyInPlayerLyricStyle(InPlayerTranslationText, NormalizeLyricKind(content.SecondaryKind), allowOriginalGradient: false);
			ApplyInPlayerLyricStyle(InPlayerTertiaryText, NormalizeLyricKind(content.TertiaryKind), allowOriginalGradient: false);
			return;
		}
		InPlayerOriginalText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 255, 255));
		InPlayerTranslationText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 174, 232, 208));
		InPlayerTertiaryText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 201, 183, 255));
		InPlayerOriginalText.Stroke = System.Windows.Media.Brushes.Black;
		InPlayerTranslationText.Stroke = System.Windows.Media.Brushes.Black;
		InPlayerTertiaryText.Stroke = System.Windows.Media.Brushes.Black;
		InPlayerOriginalText.StrokeThickness = 2.0;
		InPlayerTranslationText.StrokeThickness = 1.6;
		InPlayerTertiaryText.StrokeThickness = 1.5;
	}

	private void ApplyInPlayerLyricStyle(OutlinedTextBlock textBlock, LyricTextKind kind, bool allowOriginalGradient)
	{
		textBlock.Foreground = LyricsStyleService.CreateForeground(_state, kind, allowOriginalGradient);
		textBlock.Stroke = LyricsStyleService.CreateStroke(_state);
		textBlock.StrokeThickness = LyricsStyleService.StrokeThickness(_state, textBlock.FontSize, kind);
	}

	private static LyricTextKind NormalizeLyricKind(LyricTextKind kind)
	{
		return kind == LyricTextKind.None ? LyricTextKind.Original : kind;
	}

	private void LyricsDisplayModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initialized || _syncingLyricsDisplayMode || LyricsDisplayModeCombo.SelectedItem is not ComboBoxItem selected)
		{
			return;
		}
		_state.LyricsDisplayMode = LyricsDisplayModes.Normalize(selected.Tag?.ToString());
		_desktopLyrics?.ApplySettings(_state);
		if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
		{
			RenderLyricLine(_lyrics[_currentLyricIndex]);
		}
		StatusText.Text = "歌词显示已切换为：" + selected.Content;
		DiagnosticLog.Observe(_store.SaveAsync(_state), "STATE", "Could not save the lyrics display mode");
	}

	private void LyricsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		System.Windows.Controls.ListBox source = sender as System.Windows.Controls.ListBox ?? LyricsList;
		if (source.SelectedItem is LyricLine line)
		{
			_playback.Seek(Math.Max(0L, line.TimeMs + _state.LyricOffsetMs));
			UpdateCurrentLyric(_playback.Time);
			e.Handled = true;
		}
	}

	private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_seeking = true;
	}

	private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		Slider source = sender as Slider ?? PositionSlider;
		_playback.Seek((long)source.Value);
		_syncingPositionControls = true;
		try
		{
			PositionSlider.Value = source.Value;
			ImmersivePositionSlider.Value = source.Value;
		}
		finally
		{
			_syncingPositionControls = false;
		}
		_seeking = false;
	}

	private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingPositionControls)
		{
			return;
		}
		if (_seeking)
		{
			_syncingPositionControls = true;
			try
			{
				if (!ReferenceEquals(sender, PositionSlider))
				{
					PositionSlider.Value = e.NewValue;
				}
				if (!ReferenceEquals(sender, ImmersivePositionSlider))
				{
					ImmersivePositionSlider.Value = e.NewValue;
				}
			}
			finally
			{
				_syncingPositionControls = false;
			}
			ElapsedText.Text = FormatTime((long)e.NewValue);
			ImmersiveElapsedText.Text = ElapsedText.Text;
		}
	}

	private void RememberRecentlyPlayed(string trackId)
	{
		if (_recentTrackIds.Contains(trackId))
		{
			string[] array = _recentTrackIds.Where((string text) => text != trackId).ToArray();
			_recentTrackIds.Clear();
			string[] array2 = array;
			foreach (string id in array2)
			{
				_recentTrackIds.Enqueue(id);
			}
		}
		_recentTrackIds.Enqueue(trackId);
		while (_recentTrackIds.Count > 25)
		{
			_recentTrackIds.Dequeue();
		}
	}

	private void LibraryViewButton_Click(object sender, RoutedEventArgs e)
	{
		ShowPlayerView(showPlayer: false);
	}

	private void PlayerViewButton_Click(object sender, RoutedEventArgs e)
	{
		ShowPlayerView(showPlayer: true);
	}

	private void ShowPlayerView(bool showPlayer, TrackModel? surfaceTrack = null)
	{
		_showingPlayerView = showPlayer;
		TrackModel? activeTrack = surfaceTrack ?? _currentTrack;
		bool showVideoSurface = showPlayer && ShouldUseVideoSurface(activeTrack);
		bool showImmersiveSurface = showPlayer && !showVideoSurface;
		VideoView.Visibility = showVideoSurface ? Visibility.Visible : Visibility.Collapsed;
		PlayerPanel.Visibility = showVideoSurface ? Visibility.Visible : Visibility.Collapsed;
		ImmersivePlayerHost.Visibility = showImmersiveSurface ? Visibility.Visible : Visibility.Collapsed;
		if (!showImmersiveSurface)
		{
			ImmersiveQueueDrawer.Visibility = Visibility.Collapsed;
		}
		AttachPlaybackSurface(activeTrack);
		UpdateVinylAnimationState();
		if (showPlayer)
		{
			LibraryToolbar.Visibility = Visibility.Collapsed;
			AlbumToolbar.Visibility = Visibility.Collapsed;
			CircleToolbar.Visibility = Visibility.Collapsed;
			RecommendationToolbar.Visibility = Visibility.Collapsed;
			TrackGrid.Visibility = Visibility.Collapsed;
			EmptyTrackPanel.Visibility = Visibility.Collapsed;
			AlbumPanel.Visibility = Visibility.Collapsed;
			CirclePanel.Visibility = Visibility.Collapsed;
			RecommendationPanel.Visibility = Visibility.Collapsed;
		}
		else
		{
			UpdateLibraryContentVisibility();
		}
		LibraryViewButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, showPlayer ? "AppBackgroundBrush" : "SelectionBrush");
		PlayerViewButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, showPlayer ? "SelectionBrush" : "AppBackgroundBrush");
	}

	private void AttachPlaybackSurface(TrackModel? surfaceTrack = null)
	{
		TrackModel? activeTrack = surfaceTrack ?? _currentTrack;
		VideoView.MediaPlayer = null;
		if (!_showingPlayerView)
		{
			return;
		}
		if (ShouldUseVideoSurface(activeTrack))
		{
			VideoView.MediaPlayer = _playback.Player;
		}
	}

	private bool ShouldUseVideoSurface(TrackModel? track)
	{
		return PlayerPageService.RequiresVideoSurface(_state, track);
	}

	private void InitializeVinylAnimations()
	{
		_standardVinylClock = CreateVinylClock(StandardVinylRotateTransform, 19.0);
		_vinylFocusClock = CreateVinylClock(VinylFocusRotateTransform, 17.0);
		_lyricsModeDiscClock = CreateVinylClock(LyricsModeDiscRotateTransform, 22.0);
	}

	private static AnimationClock CreateVinylClock(RotateTransform transform, double seconds)
	{
		DoubleAnimation animation = new DoubleAnimation(0.0, 360.0, TimeSpan.FromSeconds(seconds))
		{
			RepeatBehavior = RepeatBehavior.Forever
		};
		AnimationClock clock = (AnimationClock)animation.CreateClock(true);
		transform.ApplyAnimationClock(RotateTransform.AngleProperty, clock);
		clock.Controller?.Pause();
		return clock;
	}

	private void UpdateVinylAnimationState()
	{
		bool shouldRun = _showingPlayerView
			&& _currentTrack != null
			&& !_currentTrack.IsVideo
			&& _playback.IsPlaying
			&& ImmersivePlayerHost.Visibility == Visibility.Visible;
		if (shouldRun == _vinylAnimationRunning)
		{
			return;
		}
		_vinylAnimationRunning = shouldRun;
		foreach (AnimationClock? clock in new[] { _standardVinylClock, _vinylFocusClock, _lyricsModeDiscClock })
		{
			if (shouldRun)
			{
				clock?.Controller?.Resume();
			}
			else
			{
				clock?.Controller?.Pause();
			}
		}
	}

	private void DisposeVinylAnimations()
	{
		foreach ((AnimationClock? clock, RotateTransform transform) in new[]
		{
			(_standardVinylClock, StandardVinylRotateTransform),
			(_vinylFocusClock, VinylFocusRotateTransform),
			(_lyricsModeDiscClock, LyricsModeDiscRotateTransform)
		})
		{
			clock?.Controller?.Stop();
			transform.ApplyAnimationClock(RotateTransform.AngleProperty, null);
		}
		_standardVinylClock = null;
		_vinylFocusClock = null;
		_lyricsModeDiscClock = null;
		_vinylAnimationRunning = false;
	}

	private void UpdateImmersiveTonearm(long current, long duration)
	{
		double angle = PlayerPageService.TonearmAngle(current, duration);
		StandardTonearmTransform.Angle = angle;
		VinylFocusTonearmTransform.Angle = angle;
	}

	private async void PlayerPageModeRadio_Checked(object sender, RoutedEventArgs e)
	{
		if (_syncingPlayerPageMode || sender is not System.Windows.Controls.RadioButton { Tag: string mode })
		{
			return;
		}
		_state.PlayerPageMode = PlayerPageModes.Normalize(mode);
		ApplyPlayerPageMode(_state.PlayerPageMode);
		if (_initialized)
		{
			await _store.SaveAsync(_state);
		}
	}

	private void ApplyPlayerPageMode(string? mode)
	{
		string normalized = PlayerPageModes.Normalize(mode);
		_state.PlayerPageMode = normalized;
		_syncingPlayerPageMode = true;
		try
		{
			StandardPlayerModeRadio.IsChecked = normalized == PlayerPageModes.Standard;
			VinylPlayerModeRadio.IsChecked = normalized == PlayerPageModes.Vinyl;
			LyricsPlayerModeRadio.IsChecked = normalized == PlayerPageModes.Lyrics;
		}
		finally
		{
			_syncingPlayerPageMode = false;
		}
		StandardPlayerModePanel.Visibility = normalized == PlayerPageModes.Standard ? Visibility.Visible : Visibility.Collapsed;
		VinylPlayerModePanel.Visibility = normalized == PlayerPageModes.Vinyl ? Visibility.Visible : Visibility.Collapsed;
		LyricsPlayerModePanel.Visibility = normalized == PlayerPageModes.Lyrics ? Visibility.Visible : Visibility.Collapsed;
		ImmersiveModeDescriptionText.Text = normalized switch
		{
			PlayerPageModes.Vinyl => "唱片成为视觉中心",
			PlayerPageModes.Lyrics => "专注逐行歌词",
			_ => "唱片、资料与推荐并列"
		};
		ImmersiveQueueDrawer.Visibility = Visibility.Collapsed;
		if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
		{
			SelectAndRevealLyric(_lyrics[_currentLyricIndex]);
		}
	}

	private void ImmersiveQueueButton_Click(object sender, RoutedEventArgs e)
	{
		RefreshQueueLists();
		ImmersiveQueueDrawer.Visibility = ImmersiveQueueDrawer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
	}

	private void ImmersiveQueueCloseButton_Click(object sender, RoutedEventArgs e)
	{
		ImmersiveQueueDrawer.Visibility = Visibility.Collapsed;
	}

	private void ImmersiveQueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (ImmersiveQueueList.SelectedItem is TrackModel track)
		{
			_queueIndex = _queue.FindIndex(item => string.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase));
			PlayTrack(track);
			ImmersiveQueueDrawer.Visibility = Visibility.Collapsed;
		}
	}

	private void ImmersiveSimilarList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (ImmersiveSimilarList.SelectedItem is SimilarTrackSuggestion suggestion)
		{
			AppendToQueueAndPlay(suggestion.Track);
		}
	}

	private void RefreshQueueLists()
	{
		QueueList.ItemsSource = null;
		ImmersiveQueueList.ItemsSource = null;
		QueueList.ItemsSource = _queue;
		ImmersiveQueueList.ItemsSource = _queue;
		if (_currentTrack != null)
		{
			QueueList.SelectedItem = _currentTrack;
			ImmersiveQueueList.SelectedItem = _currentTrack;
		}
	}

	private void RefreshLyricsLists()
	{
		LyricsList.ItemsSource = _lyrics;
		ImmersiveLyricsList.ItemsSource = _lyrics;
		ImmersiveFocusLyricsList.ItemsSource = _lyrics;
		Visibility emptyVisibility = _lyrics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		ImmersiveLyricsEmptyText.Visibility = emptyVisibility;
		ImmersiveFocusLyricsEmptyText.Visibility = emptyVisibility;
	}

	private void SelectAndRevealLyric(LyricLine line)
	{
		foreach (System.Windows.Controls.ListBox list in new[] { LyricsList, ImmersiveLyricsList, ImmersiveFocusLyricsList })
		{
			list.SelectedItem = line;
			list.ScrollIntoView(line);
		}
	}

	private void ApplyImmersiveLyricStyles()
	{
		System.Windows.Media.Brush original;
		System.Windows.Media.Brush romanization;
		System.Windows.Media.Brush translation;
		System.Windows.Media.Brush stroke;
		if (_state.InPlayerSubtitlesUseLyricsStyle)
		{
			original = LyricsStyleService.CreateForeground(_state, LyricTextKind.Original, allowOriginalGradient: true);
			romanization = LyricsStyleService.CreateForeground(_state, LyricTextKind.Romanization, allowOriginalGradient: false);
			translation = LyricsStyleService.CreateForeground(_state, LyricTextKind.Translation, allowOriginalGradient: false);
			stroke = LyricsStyleService.CreateStroke(_state);
		}
		else
		{
			original = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 255, 255));
			romanization = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 201, 183, 255));
			translation = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 174, 232, 208));
			stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 5, 5, 9));
		}
		ImmersivePlayerPanel.Resources["ImmersiveOriginalBrush"] = original;
		ImmersivePlayerPanel.Resources["ImmersiveRomanizationBrush"] = romanization;
		ImmersivePlayerPanel.Resources["ImmersiveTranslationBrush"] = translation;
		ImmersivePlayerPanel.Resources["ImmersiveStrokeBrush"] = stroke;
		ImmersivePlayerPanel.Resources["ImmersiveOriginalStrokeThickness"] = ImmersiveStrokeThickness(16.0, LyricTextKind.Original, 1.0);
		ImmersivePlayerPanel.Resources["ImmersiveRomanizationStrokeThickness"] = ImmersiveStrokeThickness(12.0, LyricTextKind.Romanization, 0.8);
		ImmersivePlayerPanel.Resources["ImmersiveTranslationStrokeThickness"] = ImmersiveStrokeThickness(13.0, LyricTextKind.Translation, 0.85);
		ImmersivePlayerPanel.Resources["ImmersiveFocusOriginalStrokeThickness"] = ImmersiveStrokeThickness(26.0, LyricTextKind.Original, 1.6);
		ImmersivePlayerPanel.Resources["ImmersiveFocusRomanizationStrokeThickness"] = ImmersiveStrokeThickness(15.0, LyricTextKind.Romanization, 0.95);
		ImmersivePlayerPanel.Resources["ImmersiveFocusTranslationStrokeThickness"] = ImmersiveStrokeThickness(17.0, LyricTextKind.Translation, 1.0);
		if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
		{
			ApplyVinylCurrentLyricStyles(LyricsDisplayService.Resolve(_lyrics[_currentLyricIndex], _state.LyricsDisplayMode));
		}
	}

	private double ImmersiveStrokeThickness(double fontSize, LyricTextKind kind, double fallback)
	{
		return _state.InPlayerSubtitlesUseLyricsStyle
			? LyricsStyleService.StrokeThickness(_state, fontSize, kind)
			: fallback;
	}

	private void ApplyVinylCurrentLyricStyles(LyricsDisplayContent content)
	{
		if (_state.InPlayerSubtitlesUseLyricsStyle)
		{
			ApplyInPlayerLyricStyle(VinylCurrentOriginalText, NormalizeLyricKind(content.PrimaryKind), allowOriginalGradient: true);
			ApplyInPlayerLyricStyle(VinylCurrentTranslationText, NormalizeLyricKind(content.SecondaryKind), allowOriginalGradient: false);
			ApplyInPlayerLyricStyle(VinylCurrentTertiaryText, NormalizeLyricKind(content.TertiaryKind), allowOriginalGradient: false);
			return;
		}
		VinylCurrentOriginalText.Foreground = System.Windows.Media.Brushes.White;
		VinylCurrentTranslationText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 174, 232, 208));
		VinylCurrentTertiaryText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 201, 183, 255));
		VinylCurrentOriginalText.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 5, 5, 9));
		VinylCurrentTranslationText.Stroke = VinylCurrentOriginalText.Stroke;
		VinylCurrentTertiaryText.Stroke = VinylCurrentOriginalText.Stroke;
		VinylCurrentOriginalText.StrokeThickness = 2.0;
		VinylCurrentTranslationText.StrokeThickness = 1.5;
		VinylCurrentTertiaryText.StrokeThickness = 1.4;
	}

	private void ApplyImmersivePerformanceProfile()
	{
		ImmersiveBackgroundBlur.Radius = _state.SafePlaybackMode ? 0.0 : 38.0;
		ImmersiveBackgroundImage.Opacity = _state.SafePlaybackMode ? 0.12 : 0.22;
	}

	private static string DisplayValue(string? value, string fallback)
	{
		return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
	}

	private void OpenMediaButton_Click(object sender, RoutedEventArgs e)
	{
		Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "打开音频或视频",
			Filter = "支持的媒体|*.mp3;*.flac;*.m4a;*.mp4;*.ogg;*.opus;*.wav;*.wma;*.aac;*.ape;*.mkv;*.webm;*.avi;*.mov;*.m4v;*.ts;*.mpeg;*.mpg|所有文件|*.*",
			CheckFileExists = true
		};
		if (dialog.ShowDialog(this) == true)
		{
			TrackModel existing = _state.Tracks.FirstOrDefault((TrackModel trackModel) => string.Equals(trackModel.FilePath, dialog.FileName, StringComparison.OrdinalIgnoreCase));
			TrackModel track;
			try
			{
				track = existing ?? MusicLibraryService.ReadTrack(dialog.FileName);
			}
			catch
			{
				track = new TrackModel
				{
					Id = MusicLibraryService.CreateTrackId(dialog.FileName),
					FilePath = dialog.FileName,
					Title = Path.GetFileNameWithoutExtension(dialog.FileName),
					Artist = "未知艺术家",
					Album = (Path.GetFileName(Path.GetDirectoryName(dialog.FileName)) ?? "未知专辑"),
					Format = Path.GetExtension(dialog.FileName).TrimStart('.').ToUpperInvariant(),
					IsVideo = MusicLibraryService.VideoExtensions.Contains(Path.GetExtension(dialog.FileName))
				};
			}
			int num = 1;
			List<TrackModel> list = new List<TrackModel>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = track;
			_queue = list;
			_queueIndex = 0;
			RefreshQueueLists();
			PlayTrack(track);
		}
	}

	private async void VisualizationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initialized || !(VisualizationCombo.SelectedItem is ComboBoxItem { Tag: var tag }) || !(tag is string mode) || mode == _state.VisualizationMode)
		{
			return;
		}
		_state.VisualizationMode = mode;
		await _store.SaveAsync(_state);
		if (_state.SafePlaybackMode)
		{
			StatusText.Text = "可视化设置已保存；安全播放模式期间暂不启用";
			return;
		}
		if (_currentTrack != null && !_currentTrack.IsVideo)
		{
			long position = _playback.Time;
			bool wasPlaying = _playback.IsPlaying;
			_playback.Play(_currentTrack.FilePath, _state, isVideo: false);
			await Task.Delay(450);
			_playback.Seek(position);
			if (!wasPlaying)
			{
				_playback.TogglePause();
			}
			if (mode != "Off" || _showingPlayerView)
			{
				ShowPlayerView(showPlayer: true, _currentTrack);
			}
		}
	}

	private async void PlaybackRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_initialized && PlaybackRateCombo.SelectedItem is ComboBoxItem { Tag: var tag } && double.TryParse(tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
		{
			_state.PlaybackRate = rate;
			_playback.SetRate(rate);
			await _store.SaveAsync(_state);
			PlaybackRateCombo.ToolTip = $"当前播放速度：{rate:0.##}×";
		}
	}

	private async void EqualizerPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_initialized && !_syncingAudioEffectControls && EqualizerPresetCombo.SelectedItem is ComboBoxItem { Tag: var tag } && tag is string preset)
		{
			string preset2 = AudioEffectPresets.NormalizeEqualizer(preset);
			if (!string.Equals(preset2, _state.EqualizerPreset, StringComparison.OrdinalIgnoreCase))
			{
				_state.EqualizerPreset = preset2;
				if (!_state.SafePlaybackMode)
				{
					_playback.SetEqualizerPreset(preset2);
				}
				await _store.SaveAsync(_state);
				StatusText.Text = _state.SafePlaybackMode
					? "均衡器设置已保存；安全播放模式期间暂不启用"
					: ((preset2 == "Off") ? "均衡器已关闭" : "均衡器预设已应用");
			}
		}
	}

	private async void SpatialAudioCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initialized || _syncingAudioEffectControls || !(SpatialAudioCombo.SelectedItem is ComboBoxItem { Tag: var tag }) || !(tag is string mode))
		{
			return;
		}
		string mode2 = AudioEffectPresets.NormalizeSpatialAudio(mode);
		if (string.Equals(mode2, _state.SpatialAudioMode, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_state.SpatialAudioMode = mode2;
		SpatialAudioCombo.IsEnabled = false;
		try
		{
			await _store.SaveAsync(_state);
			if (_state.SafePlaybackMode)
			{
				StatusText.Text = "空间音效设置已保存；安全播放模式期间暂不启用";
			}
			else if (_currentTrack != null && !_currentTrack.IsVideo && _playback.IsPlaying)
			{
				_watchdogRecoveryCount = 0;
				await RecoverPlaybackAsync("空间音效已更改");
			}
			else
			{
				StatusText.Text = ((mode2 == "Off") ? "空间音效已关闭" : "空间音效将在下次播放时生效");
			}
		}
		finally
		{
			SpatialAudioCombo.IsEnabled = true;
		}
	}

	private void SyncAudioEffectControls()
	{
		_syncingAudioEffectControls = true;
		try
		{
			SelectComboByTag(EqualizerPresetCombo, AudioEffectPresets.NormalizeEqualizer(_state.EqualizerPreset));
			SelectComboByTag(SpatialAudioCombo, AudioEffectPresets.NormalizeSpatialAudio(_state.SpatialAudioMode));
		}
		finally
		{
			_syncingAudioEffectControls = false;
		}
	}

	private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_refreshingMediaControls && AudioTrackCombo.SelectedItem is MediaTrackOption track)
		{
			_playback.SetAudioTrack(track.Id);
		}
	}

	private void VideoTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_refreshingMediaControls && VideoTrackCombo.SelectedItem is MediaTrackOption track)
		{
			_playback.SetVideoTrack(track.Id);
		}
	}

	private void SubtitleTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_refreshingMediaControls && SubtitleTrackCombo.SelectedItem is MediaTrackOption track)
		{
			_playback.SetSubtitleTrack(track.Id);
		}
	}

	private async void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_refreshingMediaControls && AudioDeviceCombo.SelectedItem is AudioDeviceOption device)
		{
			_state.PreferredAudioDeviceId = device.Id;
			_playback.SetAudioDevice(device.Id);
			AudioDeviceCombo.ToolTip = "当前音频输出：" + device.Name;
			await _store.SaveAsync(_state);
		}
	}

	private void SubtitleDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (SubtitleDelayText != null)
		{
			SubtitleDelayText.Text = $"{e.NewValue:+0.00;-0.00;0.00} s";
			if (_initialized)
			{
				_playback.SetSubtitleDelay((long)(e.NewValue * 1000.0));
			}
		}
	}

	private async Task RefreshMediaControlsAsync()
	{
		if (_currentTrack == null || _refreshingMediaControls)
		{
			return;
		}
		_refreshingMediaControls = true;
		try
		{
			Task<MediaControlSnapshot> captureTask = Task.Run((Func<MediaControlSnapshot>)_playback.CaptureMediaControls);
			if (await Task.WhenAny(captureTask, Task.Delay(2500)) != captureTask)
			{
				DiagnosticLog.Write("MEDIA", "Media control enumeration exceeded 2.5 seconds and was skipped");
				return;
			}
			MediaControlSnapshot controls = await captureTask;
			SetTrackCombo(AudioTrackCombo, controls.AudioTracks, controls.SelectedAudioTrack);
			SetTrackCombo(VideoTrackCombo, controls.VideoTracks, controls.SelectedVideoTrack);
			SetTrackCombo(SubtitleTrackCombo, controls.SubtitleTracks, controls.SelectedSubtitleTrack);
			AudioDeviceCombo.ItemsSource = controls.AudioDevices;
			AudioDeviceCombo.SelectedItem = controls.AudioDevices.FirstOrDefault((AudioDeviceOption device) => device.Id == _state.PreferredAudioDeviceId) ?? controls.AudioDevices[0];
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("MEDIA", "Media control enumeration failed", exception);
		}
		finally
		{
			_refreshingMediaControls = false;
		}
	}

	private void UpdateMediaDetails(MediaDetails details)
	{
		_mediaDetails = details;
		List<string> lines = new List<string>();
		if (details.HasVideo)
		{
			lines.Add($"画面  {details.Width} × {details.Height}");
			lines.Add($"帧率  {details.FrameRate:0.###} FPS");
			lines.Add("视频编码  " + details.VideoCodec + "  " + FormatBitrate(details.VideoBitrate));
		}
		if (details.HasAudio)
		{
			lines.Add($"采样率  {details.SampleRate:N0} Hz");
			lines.Add($"声道  {details.Channels}");
			lines.Add("音频编码  " + details.AudioCodec + "  " + FormatBitrate(details.AudioBitrate));
		}
		MediaInfoText.Text = ((lines.Count == 0) ? "未读取到媒体流信息" : string.Join(Environment.NewLine, lines));
		ImmersiveTechnicalText.Text = ((lines.Count == 0) ? "未读取到媒体流信息" : string.Join(Environment.NewLine, lines));
		if (_currentTrack != null)
		{
			PlayerMediaSummaryText.Text = _currentTrack.Title + "  ·  " + (details.HasVideo ? $"{details.Width}×{details.Height} / {details.FrameRate:0.##} FPS" : $"{details.SampleRate:N0} Hz / {details.Channels} 声道");
		}
		DiagnosticLog.Observe(RefreshMediaControlsAsync(), "MEDIA", "Could not refresh media controls after media details changed");
	}

	private void SnapshotButton_Click(object sender, RoutedEventArgs e)
	{
		if (_currentTrack == null || (!_currentTrack.IsVideo && !_mediaDetails.HasVideo))
		{
			StatusText.Text = "当前媒体没有视频画面";
			return;
		}
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "本地音乐库截图");
		Directory.CreateDirectory(text);
		string safeTitle = string.Concat(_currentTrack.Title.Select((char character) => (!Path.GetInvalidFileNameChars().Contains(character)) ? character : '_'));
		string path = Path.Combine(text, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
		StatusText.Text = (_playback.TakeSnapshot(path) ? ("截图已保存：" + path) : "截图失败：当前视频画面尚未就绪");
	}

	private static void SetTrackCombo(System.Windows.Controls.ComboBox comboBox, IReadOnlyList<MediaTrackOption> tracks, int selectedId)
	{
		comboBox.ItemsSource = tracks;
		comboBox.SelectedItem = tracks.FirstOrDefault((MediaTrackOption track) => track.Id == selectedId) ?? tracks.FirstOrDefault();
		comboBox.IsEnabled = tracks.Count > 1;
	}

	private static void SelectComboByTag(System.Windows.Controls.ComboBox comboBox, string tag)
	{
		comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>().FirstOrDefault((ComboBoxItem item) => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
		if (comboBox.SelectedIndex < 0)
		{
			comboBox.SelectedIndex = 0;
		}
	}

	private static string FormatBitrate(int bitrate)
	{
		if (bitrate > 0)
		{
			return $"{bitrate / 1000:N0} kb/s";
		}
		return "";
	}

	private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_syncingVolumeControls)
		{
			return;
		}
		_syncingVolumeControls = true;
		try
		{
			if (VolumeSlider != null && !ReferenceEquals(sender, VolumeSlider))
			{
				VolumeSlider.Value = e.NewValue;
			}
			if (ImmersiveVolumeSlider != null && !ReferenceEquals(sender, ImmersiveVolumeSlider))
			{
				ImmersiveVolumeSlider.Value = e.NewValue;
			}
		}
		finally
		{
			_syncingVolumeControls = false;
		}
		_previewPlayer?.UpdateVolume((int)Math.Round(e.NewValue));
		if (_initialized)
		{
			_playback.Volume = (int)e.NewValue;
			_state.Volume = (int)e.NewValue;
		}
	}

	private void DesktopLyricsCheckBox_Changed(object sender, RoutedEventArgs e)
	{
		if (_syncingDesktopLyricsControls)
		{
			return;
		}
		bool enabled = (sender as System.Windows.Controls.CheckBox)?.IsChecked == true;
		_syncingDesktopLyricsControls = true;
		try
		{
			if (DesktopLyricsCheckBox != null)
			{
				DesktopLyricsCheckBox.IsChecked = enabled;
			}
			if (ImmersiveDesktopLyricsCheckBox != null)
			{
				ImmersiveDesktopLyricsCheckBox.IsChecked = enabled;
			}
		}
		finally
		{
			_syncingDesktopLyricsControls = false;
		}
		if (!_initialized || _shuttingDown)
		{
			return;
		}
		_state.DesktopLyricsEnabled = enabled;
		if (enabled)
		{
			if (_desktopLyrics == null)
			{
				_desktopLyrics = CreateDesktopLyricsWindow();
			}
			if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
			{
				LyricLine line = _lyrics[_currentLyricIndex];
				_desktopLyrics.UpdateLyrics(line);
			}
			else if (_currentTrack != null)
			{
				_desktopLyrics.UpdateLyrics(_currentTrack.Title, _currentTrack.Artist);
			}
			_desktopLyrics.UpdatePlayState(_playback.IsPlaying);
			_desktopLyrics.UpdateOffset(_state.LyricOffsetMs);
			_desktopLyrics.Show();
		}
		else
		{
			_desktopLyrics?.Hide();
		}
		DiagnosticLog.Observe(_store.SaveAsync(_state), "STATE", "Could not save desktop lyrics visibility");
	}

	private DesktopLyricsWindow CreateDesktopLyricsWindow()
	{
		DesktopLyricsWindow window = new DesktopLyricsWindow(_state);
		window.Dismissed += delegate
		{
			DesktopLyricsCheckBox.IsChecked = false;
		};
		window.ActivateMainRequested += delegate
		{
			base.ShowInTaskbar = true;
			if (!base.IsVisible)
			{
				Show();
			}
			if (base.WindowState == WindowState.Minimized)
			{
				base.WindowState = WindowState.Normal;
			}
			Activate();
		};
		window.PreviousRequested += delegate
		{
			GoPrevious();
		};
		window.PlayPauseRequested += delegate
		{
			TogglePlayback();
		};
		window.NextRequested += delegate
		{
			GoNext();
		};
		window.SettingsRequested += delegate
		{
			ShowSettings();
		};
		window.OffsetChangeRequested += ChangeLyricOffset;
		window.LockChanged += delegate(bool locked)
		{
			_state.DesktopLyricsLocked = locked;
			DiagnosticLog.Observe(_store.SaveAsync(_state), "STATE", "Could not save desktop lyrics lock state");
		};
		window.LyricsDisplayModeChanged += delegate(string mode)
		{
			_state.LyricsDisplayMode = LyricsDisplayModes.Normalize(mode);
			_syncingLyricsDisplayMode = true;
			SelectComboByTag(LyricsDisplayModeCombo, _state.LyricsDisplayMode);
			_syncingLyricsDisplayMode = false;
			if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
			{
				RenderLyricLine(_lyrics[_currentLyricIndex]);
			}
			StatusText.Text = "桌面歌词显示：" + LyricsDisplayModes.CompactLabel(_state.LyricsDisplayMode);
			DiagnosticLog.Observe(_store.SaveAsync(_state), "STATE", "Could not save desktop lyrics display mode");
		};
		window.PositionChangedByUser += delegate
		{
			_state.DesktopLyricsLeft = window.Left;
			_state.DesktopLyricsTop = window.Top;
		};
		return window;
	}

	private void PreserveDesktopLyricsWhenMainWindowIsHidden()
	{
		if (!_initialized || _shuttingDown || !_state.DesktopLyricsEnabled)
		{
			return;
		}
		_ = base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (_shuttingDown || !_state.DesktopLyricsEnabled)
			{
				return;
			}
			_desktopLyrics ??= CreateDesktopLyricsWindow();
			if (!_desktopLyrics.IsVisible)
			{
				_desktopLyrics.Show();
			}
			_desktopLyrics.Topmost = _state.DesktopLyricsTopmost;
		}, DispatcherPriority.Background);
	}

	private void ChangeLyricOffset(int deltaMilliseconds)
	{
		if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
		{
			_lyrics[_currentLyricIndex].IsCurrent = false;
		}
		_currentLyricIndex = -1;
		_state.LyricOffsetMs = Math.Clamp(_state.LyricOffsetMs + deltaMilliseconds, -5000, 5000);
		_desktopLyrics?.UpdateOffset(_state.LyricOffsetMs);
		UpdateCurrentLyric(_playback.Time);
		StatusText.Text = ((_state.LyricOffsetMs == 0) ? "歌词时间偏移已重置" : $"歌词时间偏移：{(double)_state.LyricOffsetMs / 1000.0:+0.0;-0.0} 秒");
		DiagnosticLog.Observe(_store.SaveAsync(_state), "STATE", "Could not save the lyric timing offset");
	}

	private async void SettingsButton_Click(object sender, RoutedEventArgs e)
	{
		await ShowSettingsAsync();
	}

	private void ApplyUiFont()
	{
		try
		{
			base.FontFamily = new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(_state.UiFontFamily) ? "Microsoft YaHei UI" : _state.UiFontFamily);
		}
		catch
		{
			base.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
		}
	}

	private void ApplyPersonalization()
	{
		ThemeService.Apply(_state);
		string title = string.IsNullOrWhiteSpace(_state.AppTitleText) ? "本地音乐库" : _state.AppTitleText.Trim();
		base.Title = title;
		AppTitleTextBlock.Text = title;
		MainRoot.Background = ThemeService.CreateWindowBackground(_state);
	}

	private void ApplyStartupPage()
	{
		string page = _state.StartPage switch
		{
			"All" => "All",
			"Favorites" => "Favorites",
			"Albums" => "Albums",
			"Recent" => "Recent",
			_ => "Discover"
		};
		SelectLibrarySidebarItem(page);
	}

	private void ConfigureTrayIcon()
	{
		if (_trayIcon == null)
		{
			ContextMenuStrip menu = new ContextMenuStrip();
			ToolStripMenuItem showItem = new ToolStripMenuItem("打开本地音乐库");
			showItem.Click += delegate
			{
				base.Dispatcher.Invoke(RestoreFromTray);
			};
			ToolStripMenuItem playPauseItem = new ToolStripMenuItem("播放 / 暂停");
			playPauseItem.Click += delegate
			{
				base.Dispatcher.Invoke(TogglePlayback);
			};
			ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
			exitItem.Click += delegate
			{
				base.Dispatcher.Invoke(delegate
				{
					_forceExit = true;
					Close();
				});
			};
			menu.Items.Add(showItem);
			menu.Items.Add(playPauseItem);
			menu.Items.Add(new ToolStripSeparator());
			menu.Items.Add(exitItem);
			Icon icon;
			try
			{
				icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? SystemIcons.Application;
			}
			catch
			{
				icon = SystemIcons.Application;
			}
			_trayIcon = new NotifyIcon
			{
				Text = "本地音乐库",
				Icon = icon,
				ContextMenuStrip = menu
			};
			_trayIcon.DoubleClick += delegate
			{
				base.Dispatcher.Invoke(RestoreFromTray);
			};
		}
		_trayIcon.Visible = string.Equals(_state.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase);
	}

	private void HideToTray()
	{
		ConfigureTrayIcon();
		if (_trayIcon != null)
		{
			_trayIcon.Visible = true;
		}
		base.ShowInTaskbar = false;
		Hide();
		PreserveDesktopLyricsWhenMainWindowIsHidden();
		if (_state.FloatingMiniPlayerEnabled)
		{
			_ = base.Dispatcher.BeginInvoke(new Action(ShowPreviewPlayer), DispatcherPriority.Background);
		}
		else
		{
			_previewPlayer?.Hide();
		}
	}

	private void RestoreFromTray()
	{
		_previewDismissedWhileMinimized = false;
		_previewPlayer?.Hide();
		base.ShowInTaskbar = true;
		Show();
		base.WindowState = WindowState.Normal;
		Activate();
	}

	private void ApplyStartupWindowState()
	{
		if (!_state.StartMinimized)
		{
			return;
		}
		_ = base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (string.Equals(_state.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase))
			{
				HideToTray();
			}
			else
			{
				base.WindowState = WindowState.Minimized;
			}
			PreserveDesktopLyricsWhenMainWindowIsHidden();
		});
	}

	private void ConfigureAutoCloseTimer()
	{
		_autoCloseTimer.Stop();
		_autoCloseAt = null;
		if (_state.AutoCloseEnabled && _state.AutoCloseMinutes > 0)
		{
			_autoCloseAt = DateTime.Now.AddMinutes(_state.AutoCloseMinutes);
			_autoCloseTimer.Start();
		}
	}

	private void AutoCloseTimer_Tick(object? sender, EventArgs e)
	{
		DateTime? autoCloseAt = _autoCloseAt;
		if (autoCloseAt.HasValue && !(DateTime.Now < autoCloseAt.Value))
		{
			_autoCloseTimer.Stop();
			_forceExit = true;
			Close();
		}
	}

	private async Task MonitorLibraryRootsAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				LibraryRootState[] roots = _state.LibraryRoots.ToArray();
				if (roots.Length == 0)
				{
					await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
					continue;
				}
				foreach (LibraryRootState root in roots)
				{
					if (root.Health == LibraryRootHealthStates.Unknown)
					{
						root.Health = LibraryRootHealthStates.Checking;
					}
				}
				IReadOnlyList<LibraryRootProbeResult> results = await _rootHealthService.ProbeManyAsync(roots, 2, cancellationToken);
				bool changed = false;
				foreach (LibraryRootProbeResult result in results)
				{
					changed |= ApplyRootProbeResult(result);
				}
				if (changed)
				{
					ScheduleStateSave();
				}
				bool anyUnavailable = _state.LibraryRoots.Any(root => !LibraryRootHealthStates.IsReachable(root.Health));
				await Task.Delay(anyUnavailable ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(1), cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				DiagnosticLog.Write("NAS", "Library root health probe failed; monitoring will continue", ex);
				await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
			}
		}
	}

	private bool ApplyRootProbeResult(LibraryRootProbeResult result)
	{
		LibraryRootState? root = _state.LibraryRoots.FirstOrDefault(item => string.Equals(item.RootId, result.RootId, StringComparison.OrdinalIgnoreCase));
		if (root == null)
		{
			return false;
		}
		bool changed = !string.Equals(root.Health, result.Health, StringComparison.Ordinal) ||
			!string.Equals(root.RootKind, result.RootKind, StringComparison.Ordinal) ||
			!string.Equals(root.LastError, result.Error ?? "", StringComparison.Ordinal);
		root.ApplyProbeResult(result);
		if (changed)
		{
			DiagnosticLog.Write("NAS", $"Root '{root.Path}' is now {root.Health} ({root.LastLatencyMs ?? 0} ms): {root.LastError}");
		}
		return changed;
	}

	private void SynchronizeLibraryRootsFromFolders()
	{
		_state.LibraryRoots = LibraryRootCatalog.Synchronize(_state.LibraryFolders, _state.LibraryRoots);
		foreach (LibraryRootState root in _state.LibraryRoots)
		{
			root.ProbeTimeoutSeconds = Math.Clamp(_state.NasProbeTimeoutSeconds, 1, 15);
		}
		_state.LibraryFolders = _state.LibraryRoots.Select(root => root.Path).ToList();
	}

	private bool AreAllRootsKnownReachable()
	{
		return _state.LibraryRoots.Count > 0 && _state.LibraryRoots.All(root => LibraryRootHealthStates.IsReachable(root.Health));
	}

	private void MarkReachableRootsScanned()
	{
		DateTime now = DateTime.UtcNow;
		foreach (LibraryRootState root in _state.LibraryRoots.Where(root => LibraryRootHealthStates.IsReachable(root.Health)))
		{
			root.LastSuccessfulScanUtc = now;
		}
	}

	private void ApplyStartupPlayback()
	{
		if (_startupPlaybackApplied)
		{
			return;
		}
		_startupPlaybackApplied = true;
		RestoredPlaybackSession restored = PlaybackSessionService.Restore(_state);
		_queue = restored.Queue.ToList();
		_queueIndex = _queue.Count == 0 ? -1 : Math.Clamp(restored.CurrentIndex, 0, _queue.Count - 1);
		_recentTrackIds.Clear();
		foreach (string trackId in restored.RecentShuffleIds.TakeLast(25))
		{
			_recentTrackIds.Enqueue(trackId);
		}
		if (_queue.Count > 0)
		{
			RefreshQueueLists();
			QueueList.SelectedItem = _queue[_queueIndex];
			ImmersiveQueueList.SelectedItem = _queue[_queueIndex];
		}
		if (!_state.AutoPlayOnStartup)
		{
			return;
		}
		TrackModel? track = _queueIndex >= 0 && _queueIndex < _queue.Count && !_queue[_queueIndex].IsEncryptedNcm
			? _queue[_queueIndex]
			: _state.Tracks.FirstOrDefault(item => string.Equals(item.Id, _state.LastTrackId, StringComparison.OrdinalIgnoreCase) && !item.IsEncryptedNcm)
				?? _state.Tracks.FirstOrDefault(item => !item.IsEncryptedNcm);
		if (track != null)
		{
			if (_queue.Count == 0)
			{
				_queue = new List<TrackModel> { track };
				_queueIndex = 0;
				RefreshQueueLists();
			}
			_pendingSeekMs = _state.RememberPlaybackProgress ? Math.Max(0, restored.PositionMs) : null;
			_pendingSeekTrackId = _pendingSeekMs.HasValue ? track.Id : null;
			PlayTrack(track);
		}
	}

	private void RememberCurrentPlayback()
	{
		long position = _waitingRootId != null
			? Math.Max(0, _waitingResumePositionMs)
			: _currentTrack == null
			? Math.Max(0, _state.PlaybackSession?.PositionMs ?? 0)
			: Math.Max(0, _playback.Time);
		PlaybackSessionService.Capture(
			_state,
			_queue,
			_queueIndex,
			_currentTrack,
			position,
			_recentTrackIds,
			_waitingRootId);
	}

	private void ShowSettings()
	{
		DiagnosticLog.Observe(ShowSettingsAsync(), "UI", "Could not complete the settings dialog operation");
	}

	private async Task ShowSettingsAsync()
	{
		if (TryShowOwnedDialog(() => new SettingsWindow(_state), "打开设置", out var dialog))
		{
			string previousAudioBackend = _state.AudioBackend;
			string previousSpatialAudio = _state.SpatialAudioMode;
			string previousVisualization = _state.VisualizationMode;
			string previousHardwareDecoding = _state.HardwareDecoding;
			string previousVideoOutput = _state.VideoOutput;
			bool previousSafePlaybackMode = _state.SafePlaybackMode;
			dialog.ApplyTo(_state);
			SynchronizeLibraryRootsFromFolders();
			PersistentAssetCache.Configure(
				_store.AssetCacheDirectory,
				_state.PersistentAssetCacheEnabled,
				_state.PersistentAssetCacheMaxMegabytes);
			ApplyUiFont();
			ApplyPersonalization();
			ApplyImmersivePerformanceProfile();
			ApplyImmersiveLyricStyles();
			RefreshSidebarNavigation();
			try
			{
				StartupRegistrationService.Apply(_state.RunAtStartup);
			}
			catch (Exception ex)
			{
				System.Windows.MessageBox.Show(this, ex.Message, "无法更新开机启动设置", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			VolumeSlider.Value = _state.Volume;
			_playback.Volume = _state.Volume;
			SelectComboByTag(VisualizationCombo, _state.VisualizationMode);
			SelectComboByTag(PlaybackRateCombo, _state.PlaybackRate.ToString(CultureInfo.InvariantCulture));
			SelectComboByTag(LyricsDisplayModeCombo, _state.LyricsDisplayMode);
			SyncAudioEffectControls();
			_playback.SetEqualizerPreset(_state.SafePlaybackMode ? "Off" : _state.EqualizerPreset);
			InPlayerSubtitlePanel.Visibility = ((!_state.InPlayerBilingualSubtitles) ? Visibility.Collapsed : Visibility.Visible);
			_desktopLyrics?.ApplySettings(_state);
			if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
			{
				RenderLyricLine(_lyrics[_currentLyricIndex]);
			}
			else
			{
				ApplyInPlayerLyricStyles(new LyricsDisplayContent
				{
					PrimaryKind = LyricTextKind.Original,
					SecondaryKind = LyricTextKind.Romanization,
					TertiaryKind = LyricTextKind.Translation
				});
			}
			if (_showingPlayerView)
			{
				ShowPlayerView(showPlayer: true, _currentTrack);
			}
			if (_desktopLyrics != null && !_state.DesktopLyricsLeft.HasValue && !_state.DesktopLyricsTop.HasValue)
			{
				_desktopLyrics.ResetPosition();
			}
			ConfigureHotkeys();
			ConfigureTrayIcon();
			ConfigureAutoCloseTimer();
			if (!_state.FloatingMiniPlayerEnabled)
			{
				_previewPlayer?.Hide();
			}
			else if (base.WindowState == WindowState.Minimized || !base.IsVisible)
			{
				ShowPreviewPlayer();
			}
			await _store.SaveAsync(_state);
			bool audioEngineChanged = !string.Equals(previousAudioBackend, _state.AudioBackend, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(previousSpatialAudio, _state.SpatialAudioMode, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(previousVisualization, _state.VisualizationMode, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(previousHardwareDecoding, _state.HardwareDecoding, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(previousVideoOutput, _state.VideoOutput, StringComparison.OrdinalIgnoreCase)
				|| previousSafePlaybackMode != _state.SafePlaybackMode;
			if (_currentTrack != null && _playback.IsPlaying && audioEngineChanged)
			{
				_watchdogRecoveryCount = 0;
				await RecoverPlaybackAsync("音频输出或空间音效已更改");
			}
			if (dialog.RescanRequested)
			{
				await ScanLibraryAsync();
			}
			else
			{
				StatusText.Text = "设置已保存";
			}
		}
	}

	private void ConfigureHotkeys()
	{
		_hotkeys.Configure(this, _state.GlobalHotkeysEnabled, _state.SystemMediaKeysEnabled);
		if (_hotkeys.FailedRegistrations > 0)
		{
			StatusText.Text = $"{_hotkeys.FailedRegistrations} 个全局快捷键已被其它程序占用，本窗口快捷键仍可使用";
		}
	}

	private void Window_HandledKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (Keyboard.Modifiers == ModifierKeys.Control)
		{
			HotkeyAction? action = e.Key switch
			{
				Key.P => HotkeyAction.PlayPause,
				Key.Left => HotkeyAction.Previous,
				Key.Right => HotkeyAction.Next,
				Key.Up => HotkeyAction.VolumeUp,
				Key.Down => HotkeyAction.VolumeDown,
				Key.D => HotkeyAction.ToggleLyrics,
				Key.L => HotkeyAction.ToggleFavorite,
				Key.M => HotkeyAction.ToggleMiniMode,
				_ => null,
			};
			if (action.HasValue)
			{
				HandleHotkey(action.Value);
				e.Handled = true;
			}
		}
	}

	private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == Key.Escape && _showingPlayerView)
		{
			ShowPlayerView(showPlayer: false);
			e.Handled = true;
			return;
		}
		ModifierKeys modifiers = Keyboard.Modifiers;
		bool flag = modifiers == ModifierKeys.None;
		if (flag)
		{
			object originalSource = e.OriginalSource;
			bool flag2 = ((originalSource is System.Windows.Controls.TextBox || originalSource is System.Windows.Controls.ComboBox) ? true : false);
			flag = flag2;
		}
		if (!flag)
		{
			HotkeyAction? action = null;
			if (modifiers == ModifierKeys.None)
			{
				action = e.Key switch
				{
					Key.Space => HotkeyAction.PlayPause,
					Key.MediaPlayPause => HotkeyAction.PlayPause,
					Key.MediaPreviousTrack => HotkeyAction.Previous,
					Key.MediaNextTrack => HotkeyAction.Next,
					_ => null,
				};
			}
			if (action.HasValue)
			{
				HandleHotkey(action.Value);
				e.Handled = true;
			}
		}
	}

	private void HandleHotkey(HotkeyAction action)
	{
		switch (action)
		{
		case HotkeyAction.PlayPause:
			TogglePlayback();
			break;
		case HotkeyAction.Previous:
			GoPrevious();
			break;
		case HotkeyAction.Next:
			GoNext();
			break;
		case HotkeyAction.VolumeUp:
			VolumeSlider.Value = Math.Min(100.0, VolumeSlider.Value + 5.0);
			break;
		case HotkeyAction.VolumeDown:
			VolumeSlider.Value = Math.Max(0.0, VolumeSlider.Value - 5.0);
			break;
		case HotkeyAction.ToggleLyrics:
			DesktopLyricsCheckBox.IsChecked = DesktopLyricsCheckBox.IsChecked != true;
			break;
		case HotkeyAction.ToggleFavorite:
			ToggleCurrentTrackFavorite();
			break;
		case HotkeyAction.ToggleMiniMode:
			ToggleMiniMode();
			break;
		}
	}

	private async void ToggleCurrentTrackFavorite()
	{
		TrackModel track = _currentTrack ?? (TrackGrid.SelectedItem as TrackModel);
		if (track != null)
		{
			track.IsFavorite = !track.IsFavorite;
			_recommendationCache.Clear();
			await _store.SaveAsync(_state);
			TrackGrid.Items.Refresh();
			UpdateAuxiliaryPlayerState();
			StatusText.Text = (track.IsFavorite ? ("已收藏“" + track.Title + "”") : ("已取消收藏“" + track.Title + "”"));
		}
	}

	private void ConfigureTaskbarHoverPlayer()
	{
		try
		{
			TaskbarPlaybackControls controls = new TaskbarPlaybackControls();
			controls.PreviousRequested += delegate
			{
				GoPrevious();
			};
			controls.PlayPauseRequested += delegate
			{
				TogglePlayback();
			};
			controls.NextRequested += delegate
			{
				GoNext();
			};
			controls.FavoriteRequested += delegate
			{
				ToggleCurrentTrackFavorite();
			};
			_taskbarControls = controls;
			base.TaskbarItemInfo = controls.ItemInfo;
			_taskbarHoverPreview = new TaskbarPreviewService(this, controls.ItemInfo);
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("TASKBAR", "Could not initialize hover mini player", exception);
		}
	}

	private void ConfigurePreviewPlayer()
	{
		PreviewPlayerWindow preview = new PreviewPlayerWindow();
		preview.ActivateMainRequested += delegate
		{
			RestoreMainFromPreview();
		};
		preview.PreviousRequested += delegate
		{
			GoPrevious();
		};
		preview.PlayPauseRequested += delegate
		{
			TogglePlayback();
		};
		preview.NextRequested += delegate
		{
			GoNext();
		};
		preview.FavoriteRequested += delegate
		{
			ToggleCurrentTrackFavorite();
		};
		preview.MuteRequested += delegate
		{
			ToggleMiniPlayerMute();
		};
		preview.VolumeDeltaRequested += delegate(int delta)
		{
			VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0.0, 100.0);
		};
		preview.QueueTrackRequested += delegate(TrackModel track)
		{
			int index = _queue.FindIndex(item =>
				string.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
			{
				return;
			}
			_queueIndex = index;
			QueueList.SelectedItem = track;
			QueueList.ScrollIntoView(track);
			PlayTrack(track);
		};
		preview.Dismissed += delegate
		{
			_previewDismissedWhileMinimized = true;
		};
		_previewPlayer = preview;
		UpdateAuxiliaryPlayerState();
	}

	private void ToggleMiniPlayerMute()
	{
		if (VolumeSlider.Value <= 0.5)
		{
			VolumeSlider.Value = Math.Clamp(_volumeBeforeMiniMute, 1, 100);
			return;
		}
		_volumeBeforeMiniMute = Math.Max(1, (int)Math.Round(VolumeSlider.Value));
		VolumeSlider.Value = 0;
	}

	private void Window_StateChanged(object? sender, EventArgs e)
	{
		if (_shuttingDown)
		{
			return;
		}
		if (MaximizeRestoreButton != null)
		{
			MaximizeRestoreButton.Content = base.WindowState == WindowState.Maximized ? "❐" : "□";
			MaximizeRestoreButton.ToolTip = base.WindowState == WindowState.Maximized ? "还原" : "最大化";
		}
		if (base.WindowState == WindowState.Minimized)
		{
			PreserveDesktopLyricsWhenMainWindowIsHidden();
			if (_state.FloatingMiniPlayerEnabled)
			{
				_ = base.Dispatcher.BeginInvoke(new Action(ShowPreviewPlayer), DispatcherPriority.Background);
			}
			else
			{
				_previewPlayer?.Hide();
			}
		}
		else
		{
			_previewDismissedWhileMinimized = false;
			_previewPlayer?.Hide();
		}
	}

	private void ShowPreviewPlayer()
	{
		if (!_shuttingDown && _state.FloatingMiniPlayerEnabled && !_previewDismissedWhileMinimized && _previewPlayer != null)
		{
			_previewPlayer.UpdateTrack(_currentTrack, MiniCoverImage.Source);
			_previewPlayer.UpdateQueue(_queue, _currentTrack);
			_previewPlayer.UpdateVolume((int)Math.Round(VolumeSlider.Value));
			UpdateAuxiliaryPlayerState();
			_previewPlayer.ShowNearDesktopEdge(closeRestoresMain: false, activate: false);
		}
	}

	private void UpdateAuxiliaryPlayerState()
	{
		bool isFavorite = _currentTrack?.IsFavorite ?? false;
		if (BottomFavoriteButton != null)
		{
			BottomFavoriteButton.Content = (isFavorite ? "♥" : "♡");
			BottomFavoriteButton.ToolTip = (isFavorite ? "取消收藏当前歌曲" : "收藏当前歌曲");
		}
		if (ImmersiveFavoriteButton != null)
		{
			ImmersiveFavoriteButton.Content = (isFavorite ? "♥" : "♡");
			ImmersiveFavoriteButton.ToolTip = (isFavorite ? "取消收藏当前歌曲" : "收藏当前歌曲");
		}
		if (_currentTrack != null && ImmersiveQueueList.SelectedItem != _currentTrack)
		{
			ImmersiveQueueList.SelectedItem = _currentTrack;
		}
		UpdateVinylAnimationState();
		_previewPlayer?.UpdatePlaybackState(_playback.IsPlaying, isFavorite);
		_previewPlayer?.UpdateVolume((int)Math.Round(VolumeSlider.Value));
		_previewPlayer?.UpdateQueue(_queue, _currentTrack);
		_taskbarControls?.Update(_currentTrack != null, _queue.Count > 0, _playback.IsPlaying, isFavorite);
	}

	private void RestoreMainFromPreview()
	{
		_previewPlayer?.Hide();
		_previewDismissedWhileMinimized = false;
		base.ShowInTaskbar = true;
		if (!base.IsVisible)
		{
			Show();
		}
		if (base.WindowState == WindowState.Minimized)
		{
			base.WindowState = WindowState.Normal;
		}
		Activate();
	}

	private void ToggleMiniMode()
	{
		if (!base.IsVisible || base.WindowState == WindowState.Minimized)
		{
			RestoreMainFromPreview();
		}
		else
		{
			EnterMiniMode();
		}
	}

	private void EnterMiniMode()
	{
		if (_shuttingDown || _previewPlayer == null)
		{
			return;
		}
		_previewDismissedWhileMinimized = false;
		_previewPlayer.UpdateTrack(_currentTrack, MiniCoverImage.Source);
		_previewPlayer.UpdateQueue(_queue, _currentTrack);
		_previewPlayer.UpdateVolume((int)Math.Round(VolumeSlider.Value));
		UpdateAuxiliaryPlayerState();
		base.ShowInTaskbar = false;
		Hide();
		PreserveDesktopLyricsWhenMainWindowIsHidden();
		_previewPlayer.ShowNearDesktopEdge(closeRestoresMain: true, activate: true);
	}

	private void MiniModeButton_Click(object sender, RoutedEventArgs e)
	{
		EnterMiniMode();
	}

	private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = WindowState.Minimized;
	}

	private void MaximizeRestoreWindowButton_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = base.WindowState == WindowState.Maximized
			? WindowState.Normal
			: WindowState.Maximized;
	}

	private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OpenCurrentFolderButton_Click(object sender, RoutedEventArgs e)
	{
		OpenTrackFolder(_currentTrack);
	}

	private static string FormatTime(long milliseconds)
	{
		TimeSpan time = TimeSpan.FromMilliseconds(Math.Max(0L, milliseconds));
		if (!(time.TotalHours >= 1.0))
		{
			return time.ToString("m\\:ss");
		}
		return time.ToString("h\\:mm\\:ss");
	}

}
