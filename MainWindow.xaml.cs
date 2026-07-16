using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OfflineMusicLibrary;

public partial class MainWindow : Window
{
    private readonly AppStore _store = new();
    private readonly MusicLibraryService _libraryService = new();
    private readonly NetEasePlaylistService _netEaseService = new();
    private readonly PlaybackService _playback = new();
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _autoCloseTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _stateSaveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Random _random = new();
    private readonly ObservableCollection<string> _categories = [];
    private readonly ObservableCollection<AlbumViewModel> _albums = [];
    private readonly ObservableCollection<AlbumViewModel> _albumCards = [];
    private readonly ObservableCollection<CircleViewModel> _circles = [];
    private readonly ObservableCollection<CircleViewModel> _circleCards = [];
    private readonly Queue<string> _recentTrackIds = new();
    private readonly SemaphoreSlim _albumCoverLoadGate = new(4, 4);

    private AppState _state = new();
    private List<TrackModel> _visibleTracks = [];
    private List<AlbumViewModel> _filteredAlbums = [];
    private List<CircleViewModel> _filteredCircles = [];
    private List<TrackModel> _queue = [];
    private List<LyricLine> _lyrics = [];
    private TrackModel? _currentTrack;
    private PlaylistModel? _currentPlaylist;
    private string? _currentCategory;
    private string? _currentAlbumKey;
    private string? _currentCircleKey;
    private LibraryView _view = LibraryView.All;
    private DesktopLyricsWindow? _desktopLyrics;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private int _queueIndex = -1;
    private int _currentLyricIndex = -1;
    private bool _initialized;
    private bool _isScanning;
    private bool _seeking;
    private bool _suppressNavigation;
    private bool _shuttingDown;
    private bool _refreshingMediaControls;
    private bool _showingPlayerView;
    private bool _forceExit;
    private bool _startupPlaybackApplied;
    private long? _pendingSeekMs;
    private DateTime? _autoCloseAt;
    private int _albumCardLoadVersion;
    private int _circleCardLoadVersion;
    private int _playlistCoverLoadVersion;
    private int _nowPlayingLoadVersion;
    private int _watchdogRecoveryCount;
    private bool _playbackRecoveryInProgress;
    private string? _watchdogTrackId;
    private MediaDetails _mediaDetails = new();
    private string _trackSortKey = "ArtistAlbum";
    private ListSortDirection _trackSortDirection = ListSortDirection.Ascending;
    private bool _syncingSortCombo;

    private const int AlbumCardBatchSize = 64;
    private const int CircleCardBatchSize = 64;

    public MainWindow()
    {
        InitializeComponent();
        VideoView.MediaPlayer = _playback.Player;
        AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(Window_HandledKeyDown), handledEventsToo: true);
        CategoryList.ItemsSource = _categories;
        AlbumList.ItemsSource = _albums;
        AlbumGridList.ItemsSource = _albumCards;
        CircleGridList.ItemsSource = _circleCards;
        _playerTimer.Tick += PlayerTimer_Tick;
        _autoCloseTimer.Tick += AutoCloseTimer_Tick;
        _stateSaveTimer.Tick += StateSaveTimer_Tick;
        _playback.Ended += (_, _) => Dispatcher.BeginInvoke(HandleTrackEnded);
        _playback.PlaybackError += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = "播放引擎报告异常，正在尝试恢复……";
            _ = RecoverPlaybackAsync("底层播放错误");
        });
        _playback.PlaybackReady += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(350);
            if (_pendingSeekMs is { } pendingSeek)
            {
                _playback.Seek(pendingSeek);
                _pendingSeekMs = null;
            }
            await RefreshMediaControlsAsync();
        });
        _playback.PlayerChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            VideoView.MediaPlayer = null;
            VideoView.MediaPlayer = _showingPlayerView ? _playback.Player : null;
        });
        _playback.MediaDetailsChanged += details => Dispatcher.BeginInvoke(() => UpdateMediaDetails(details));
        _hotkeys.Invoked += action => Dispatcher.BeginInvoke(() => HandleHotkey(action));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _state = await _store.LoadAsync();
            ApplyUiFont();
            try { StartupRegistrationService.Apply(_state.RunAtStartup); } catch { }
            if (_state.ShuffleEnabled && _state.ShuffleMode == "Off")
                _state.ShuffleMode = "Uniform";
            _state.ShuffleEnabled = _state.ShuffleMode != "Off";
            VolumeSlider.Value = _state.Volume;
            _playback.Volume = _state.Volume;
            SelectComboByTag(PlaybackRateCombo, _state.PlaybackRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SelectComboByTag(VisualizationCombo, _state.VisualizationMode);
            UpdatePlaybackModeButtons();
            ShowPlayerView(false);
            RefreshNavigation();
            _initialized = true;
            DesktopLyricsCheckBox.IsChecked = _state.DesktopLyricsEnabled;

            if (_state.Tracks.Count == 0 || _state.ScanOnStartup)
                await ScanLibraryAsync();
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
            ApplyStartupWindowState();
        }
        catch (Exception exception)
        {
            _initialized = true;
            StatusText.Text = "曲库载入失败";
            MessageBox.Show(this, exception.Message, "无法载入曲库", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        RememberCurrentPlayback();
        if (!_forceExit && string.Equals(_state.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            HideToTray();
            _ = _store.SaveAsync(_state);
            return;
        }
        if (!_forceExit && string.Equals(_state.CloseBehavior, "Minimize", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            _ = _store.SaveAsync(_state);
            return;
        }

        _shuttingDown = true;
        _playerTimer.Stop();
        _autoCloseTimer.Stop();
        _stateSaveTimer.Stop();
        _state.Volume = (int)VolumeSlider.Value;
        try
        {
            Task.Run(() => _store.SaveAsync(_state)).GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown should continue even if the settings file is temporarily locked.
        }
        _desktopLyrics?.Close();
        _hotkeys.Dispose();
        _playback.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
    }

    private async Task ScanLibraryAsync()
    {
        if (_isScanning)
            return;
        if (_state.LibraryFolders.Count == 0)
        {
            StatusText.Text = "请先添加音乐文件夹";
            return;
        }

        _isScanning = true;
        RescanButton.IsEnabled = false;
        AddFolderButton.IsEnabled = false;
        var progress = new Progress<ScanProgress>(value =>
        {
            StatusText.Text = $"正在扫描 {value.Scanned:N0} / {value.Total:N0}，元数据回退 {value.Errors:N0}";
            TrackCountText.Text = Path.GetFileName(value.CurrentFile);
        });

        try
        {
            _state.Tracks = await _libraryService.ScanAsync(_state.LibraryFolders, _state.Tracks, progress);
            await _store.SaveAsync(_state);
            RefreshNavigation();
            ApplyFilter();
            StatusText.Text = $"扫描完成，共 {_state.Tracks.Count:N0} 首本地歌曲";
        }
        catch (Exception exception)
        {
            StatusText.Text = "扫描未完成";
            MessageBox.Show(this, exception.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isScanning = false;
            RescanButton.IsEnabled = true;
            AddFolderButton.IsEnabled = true;
        }
    }

    private void RefreshNavigation()
    {
        PopulateCircleFallbacks();
        _categories.Clear();
        foreach (var category in _state.Tracks.SelectMany(track => track.Categories)
                     .Where(category => !string.IsNullOrWhiteSpace(category))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase))
            _categories.Add(category);

        var selectedAlbumKey = _currentAlbumKey;
        _albums.Clear();
        foreach (var group in _state.Tracks
                     .GroupBy(AlbumIdentity.Create, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => GetAlbumTitle(group), StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(GetAlbumArtist, StringComparer.CurrentCultureIgnoreCase))
        {
            var representative = group
                .OrderBy(track => track.TrackNumber)
                .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                .First();
            var isFavorite = IsFavoriteAlbum(group.Key) || group.Any(track => IsFavoriteAlbum(AlbumIdentity.FolderScopedFallback(track)));
            if (isFavorite)
                AddFavoriteAlbum(group.Key);
            _albums.Add(new AlbumViewModel
            {
                Key = group.Key,
                Title = GetAlbumTitle(group),
                Artist = GetAlbumArtist(group),
                CircleNames = string.Join(" ", group.Select(track => track.Circle)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)),
                TrackCount = group.Count(),
                IsFavorite = isFavorite,
                RepresentativeTrack = representative
            });
        }

        _circles.Clear();
        foreach (var group in _state.Tracks
                     .Where(track => !string.IsNullOrWhiteSpace(track.Circle))
                     .GroupBy(CircleIdentity.Create, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => GetCircleName(group), StringComparer.CurrentCultureIgnoreCase))
        {
            var representative = group
                .OrderByDescending(track => track.HasCover)
                .ThenBy(track => track.Album, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(track => track.TrackNumber)
                .First();
            _circles.Add(new CircleViewModel
            {
                Key = group.Key,
                Name = GetCircleName(group),
                AlbumCount = group.Select(AlbumIdentity.Create).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TrackCount = group.Count(),
                RepresentativeTrack = representative
            });
        }

        if (selectedAlbumKey is not null)
            AlbumList.SelectedItem = _albums.FirstOrDefault(album => album.Key == selectedAlbumKey);

        if (IsAlbumPage)
            ApplyAlbumFilter();
        else if (IsCirclePage)
            ApplyCircleFilter();

        var selectedId = _currentPlaylist?.Id;
        PlaylistList.ItemsSource = null;
        PlaylistList.ItemsSource = _state.Playlists;
        if (selectedId is not null)
            PlaylistList.SelectedItem = _state.Playlists.FirstOrDefault(playlist => playlist.Id == selectedId);
        QueuePlaylistCoverLoads();
        UpdatePlaylistHeader();
    }

    private void QueuePlaylistCoverLoads()
    {
        var version = ++_playlistCoverLoadVersion;
        foreach (var playlist in _state.Playlists.Where(playlist => playlist.CoverThumbnail is null))
            _ = LoadPlaylistCoverAsync(playlist, version);
    }

    private async Task LoadPlaylistCoverAsync(PlaylistModel playlist, int version)
    {
        var customCoverPath = playlist.CoverPath;
        var trackMap = _state.Tracks
            .GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var candidates = playlist.TrackIds
            .Select(id => trackMap.GetValueOrDefault(id))
            .Where(track => track is not null)
            .Cast<TrackModel>()
            .Take(24)
            .ToList();

        await _albumCoverLoadGate.WaitAsync();
        try
        {
            var cover = await Task.Run(() =>
            {
                var custom = CoverService.LoadImageFile(customCoverPath, 360);
                if (custom is not null)
                    return custom;

                foreach (var track in candidates)
                {
                    var automatic = CoverService.LoadThumbnail(track, 360);
                    if (automatic is not null)
                        return automatic;
                }
                return null;
            });
            if (version != _playlistCoverLoadVersion || !_state.Playlists.Contains(playlist))
                return;
            playlist.CoverThumbnail = cover;
            if (string.Equals(_currentPlaylist?.Id, playlist.Id, StringComparison.OrdinalIgnoreCase))
                PlaylistHeaderCoverImage.Source = cover;
        }
        finally
        {
            _albumCoverLoadGate.Release();
        }
    }

    private static string GetAlbumTitle(IEnumerable<TrackModel> tracks) =>
        tracks.Select(track => string.IsNullOrWhiteSpace(track.Album) ? "未知专辑" : track.Album.Trim())
            .GroupBy(title => title, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "未知专辑";

    private static string GetCircleName(IEnumerable<TrackModel> tracks) =>
        tracks.Select(track => track.Circle.Trim())
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "未识别社团";

    private void PopulateCircleFallbacks()
    {
        foreach (var albumGroup in _state.Tracks.GroupBy(AlbumIdentity.Create, StringComparer.OrdinalIgnoreCase))
        {
            var unresolved = albumGroup
                .Where(track => !track.CircleIsManual && string.IsNullOrWhiteSpace(track.Circle))
                .ToList();
            if (unresolved.Count == 0)
                continue;

            var knownCircles = albumGroup
                .Where(track => !string.IsNullOrWhiteSpace(track.Circle))
                .GroupBy(track => CircleIdentity.Create(track), StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Key = group.Key,
                    Name = group.Select(track => track.Circle.Trim())
                        .GroupBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                        .OrderByDescending(values => values.Count())
                        .Select(values => values.Key)
                        .First()
                })
                .ToList();
            if (knownCircles.Count > 1)
                continue;

            var albumArtists = albumGroup.Select(track => track.AlbumArtist?.Trim() ?? "")
                .Where(IsUsableCircleCandidate)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var performers = albumGroup.Select(track => track.Artist?.Trim() ?? "")
                .Where(IsUsableCircleCandidate)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var candidate = knownCircles.Count == 1
                ? knownCircles[0].Name
                : albumArtists.Count == 1
                    ? albumArtists[0]
                    : performers.Count == 1
                        ? performers[0]
                        : null;
            if (candidate is null)
                continue;
            foreach (var track in unresolved)
                track.Circle = candidate;
        }
    }

    private static bool IsUsableCircleCandidate(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !IsUnknownArtist(value) &&
        !string.Equals(value, "V.A.", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "VA", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "Various Artists", StringComparison.OrdinalIgnoreCase);

    private static string GetAlbumArtist(IEnumerable<TrackModel> tracks)
    {
        var list = tracks.ToList();
        var albumArtists = list.Select(track => track.AlbumArtist?.Trim() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value) && !IsUnknownArtist(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (albumArtists.Count == 1)
            return albumArtists[0];
        if (albumArtists.Count > 1)
            return "V.A.";

        var artists = list.Select(track => track.Artist?.Trim() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value) && !IsUnknownArtist(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return artists.Count switch
        {
            0 => "未知艺术家",
            1 => artists[0],
            _ => "V.A."
        };
    }

    private static bool IsUnknownArtist(string value) =>
        string.Equals(value, "未知艺术家", StringComparison.CurrentCultureIgnoreCase) ||
        string.Equals(value, "unknown artist", StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter()
    {
        if (!_initialized)
            return;

        IEnumerable<TrackModel> tracks = _state.Tracks;
        switch (_view)
        {
            case LibraryView.Favorites:
                tracks = tracks.Where(track => track.IsFavorite);
                break;
            case LibraryView.Recent:
                tracks = tracks.Where(track => track.AddedAt >= DateTime.Now.AddDays(-30));
                break;
            case LibraryView.History:
                tracks = tracks.Where(track => track.LastPlayedAt is not null);
                break;
            case LibraryView.Category when _currentCategory is not null:
                tracks = tracks.Where(track => track.Categories.Contains(_currentCategory, StringComparer.CurrentCultureIgnoreCase));
                break;
            case LibraryView.Album when _currentAlbumKey is not null:
                tracks = tracks.Where(track => string.Equals(AlbumIdentity.Create(track), _currentAlbumKey, StringComparison.OrdinalIgnoreCase));
                break;
            case LibraryView.Circle when _currentCircleKey is not null:
                tracks = tracks.Where(track => string.Equals(CircleIdentity.Create(track), _currentCircleKey, StringComparison.OrdinalIgnoreCase));
                break;
            case LibraryView.FavoriteAlbums:
                var albumKeys = _state.FavoriteAlbumKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                tracks = tracks.Where(track => albumKeys.Contains(AlbumIdentity.Create(track)));
                break;
            case LibraryView.Playlist when _currentPlaylist is not null:
                var ids = _currentPlaylist.TrackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                tracks = tracks.Where(track => ids.Contains(track.Id));
                break;
        }

        var query = SearchBox.Text.Trim().ToLowerInvariant();
        if (query.Length > 0)
            tracks = tracks.Where(track => track.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        tracks = SortTracks(tracks);

        _visibleTracks = tracks.ToList();
        TrackGrid.ItemsSource = _visibleTracks;
        UpdateTrackSortIndicator();
        TrackCountText.Text = $"{_visibleTracks.Count:N0} 首";
        LastPlayedColumn.Visibility = _view == LibraryView.History ? Visibility.Visible : Visibility.Collapsed;
        PlayCountColumn.Visibility = _view == LibraryView.History ? Visibility.Visible : Visibility.Collapsed;
        RemoveFromPlaylistButton.Visibility = _view == LibraryView.Playlist
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePlaylistHeader();
    }

    private bool IsAlbumPage => _view is LibraryView.Albums or LibraryView.FavoriteAlbums;
    private bool IsCirclePage => _view == LibraryView.Circles;

    private void ApplyAlbumFilter()
    {
        IEnumerable<AlbumViewModel> albums = _albums;
        if (_view == LibraryView.FavoriteAlbums)
            albums = albums.Where(album => album.IsFavorite);

        var query = SearchBox.Text.Trim().ToLowerInvariant();
        if (query.Length > 0)
            albums = albums.Where(album => album.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        _filteredAlbums = albums
            .OrderBy(album => album.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(album => album.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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
            return;

        var version = _albumCardLoadVersion;
        var next = _filteredAlbums.Skip(_albumCards.Count).Take(AlbumCardBatchSize).ToList();
        foreach (var album in next)
            _albumCards.Add(album);

        TrackCountText.Text = $"{_albumCards.Count:N0} / {_filteredAlbums.Count:N0} 张专辑";
        QueueAlbumCoverLoads(next, version);
    }

    private void QueueAlbumCoverLoads(IEnumerable<AlbumViewModel> albums, int version)
    {
        foreach (var album in albums)
            _ = LoadAlbumCoverAsync(album, version);
    }

    private async Task LoadAlbumCoverAsync(AlbumViewModel album, int version)
    {
        var track = album.RepresentativeTrack;
        if (track is null || album.CoverThumbnail is not null)
            return;

        await _albumCoverLoadGate.WaitAsync();
        try
        {
            var cover = await Task.Run(() => CoverService.LoadThumbnail(track, 260));
            if (version != _albumCardLoadVersion)
                return;
            album.CoverThumbnail = cover;
        }
        finally
        {
            _albumCoverLoadGate.Release();
        }
    }

    private void AlbumGridList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsAlbumPage || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 420)
            return;
        AddAlbumCardBatch();
    }

    private void ApplyCircleFilter()
    {
        IEnumerable<CircleViewModel> circles = _circles;
        var query = SearchBox.Text.Trim().ToLowerInvariant();
        if (query.Length > 0)
            circles = circles.Where(circle => circle.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        _filteredCircles = circles
            .OrderBy(circle => circle.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _circleCardLoadVersion++;
        _circleCards.Clear();
        AddCircleCardBatch();
        var unidentified = _state.Tracks.Count(track => string.IsNullOrWhiteSpace(track.Circle));
        CircleHintText.Text = unidentified > 0
            ? $"按分组与专辑艺术家识别；另有 {unidentified:N0} 首未识别，可在歌曲右键菜单中设置"
            : "按分组与专辑艺术家识别；可在歌曲右键菜单中修正";
        UpdateLibraryContentVisibility();
    }

    private void AddCircleCardBatch()
    {
        if (_circleCards.Count >= _filteredCircles.Count)
            return;

        var version = _circleCardLoadVersion;
        var next = _filteredCircles.Skip(_circleCards.Count).Take(CircleCardBatchSize).ToList();
        foreach (var circle in next)
            _circleCards.Add(circle);

        TrackCountText.Text = $"{_circleCards.Count:N0} / {_filteredCircles.Count:N0} 个社团";
        foreach (var circle in next)
            _ = LoadCircleCoverAsync(circle, version);
    }

    private async Task LoadCircleCoverAsync(CircleViewModel circle, int version)
    {
        var track = circle.RepresentativeTrack;
        if (track is null || circle.CoverThumbnail is not null)
            return;

        await _albumCoverLoadGate.WaitAsync();
        try
        {
            var cover = await Task.Run(() => CoverService.LoadThumbnail(track, 260));
            if (version != _circleCardLoadVersion)
                return;
            circle.CoverThumbnail = cover;
        }
        finally
        {
            _albumCoverLoadGate.Release();
        }
    }

    private void CircleGridList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!IsCirclePage || e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 420)
            return;
        AddCircleCardBatch();
    }

    private void UpdateAlbumFilterButtons()
    {
        if (AllAlbumsFilterButton is null || FavoriteAlbumsFilterButton is null)
            return;

        AllAlbumsFilterButton.Background = _view == LibraryView.Albums
            ? (System.Windows.Media.Brush)FindResource("SelectionBrush")
            : (System.Windows.Media.Brush)FindResource("AppBackgroundBrush");
        FavoriteAlbumsFilterButton.Background = _view == LibraryView.FavoriteAlbums
            ? (System.Windows.Media.Brush)FindResource("SelectionBrush")
            : (System.Windows.Media.Brush)FindResource("AppBackgroundBrush");
    }

    private void UpdatePlaylistHeader()
    {
        var showPlaylist = _view == LibraryView.Playlist && _currentPlaylist is not null;
        StandardHeaderPanel.Visibility = showPlaylist ? Visibility.Collapsed : Visibility.Visible;
        PlaylistHeaderPanel.Visibility = showPlaylist ? Visibility.Visible : Visibility.Collapsed;
        if (!showPlaylist || _currentPlaylist is null)
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
        if (_showingPlayerView)
            return;

        UpdatePlaylistHeader();
        var albumPage = IsAlbumPage;
        var circlePage = IsCirclePage;
        AlbumToolbar.Visibility = albumPage ? Visibility.Visible : Visibility.Collapsed;
        AlbumPanel.Visibility = albumPage ? Visibility.Visible : Visibility.Collapsed;
        CircleToolbar.Visibility = circlePage ? Visibility.Visible : Visibility.Collapsed;
        CirclePanel.Visibility = circlePage ? Visibility.Visible : Visibility.Collapsed;
        LibraryToolbar.Visibility = albumPage || circlePage ? Visibility.Collapsed : Visibility.Visible;
        TrackGrid.Visibility = albumPage || circlePage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SelectLibraryView(LibraryView view, string title)
    {
        _suppressNavigation = true;
        CategoryList.SelectedItem = null;
        AlbumList.SelectedItem = null;
        AlbumGridList.SelectedItem = null;
        CircleGridList.SelectedItem = null;
        PlaylistList.SelectedItem = null;
        _suppressNavigation = false;
        _view = view;
        _currentCategory = null;
        _currentAlbumKey = null;
        _currentCircleKey = null;
        _currentPlaylist = null;
        CurrentViewTitle.Text = title;
        ApplyFilter();
        ShowPlayerView(false);
    }

    private void AllMusicButton_Click(object sender, RoutedEventArgs e) => SelectLibraryView(LibraryView.All, "全部音乐");
    private void FavoritesButton_Click(object sender, RoutedEventArgs e) => SelectLibraryView(LibraryView.Favorites, "我的收藏");
    private void AlbumsButton_Click(object sender, RoutedEventArgs e) => SelectAlbumPage(showFavoritesOnly: false);
    private void FavoriteAlbumsButton_Click(object sender, RoutedEventArgs e) => SelectAlbumPage(showFavoritesOnly: true);
    private void CirclesButton_Click(object sender, RoutedEventArgs e) => SelectCirclePage();
    private void RecentButton_Click(object sender, RoutedEventArgs e) => SelectLibraryView(LibraryView.Recent, "最近 30 天添加");
    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _trackSortKey = "LastPlayedAt";
        _trackSortDirection = ListSortDirection.Descending;
        SyncSortCombo(_trackSortKey);
        SelectLibraryView(LibraryView.History, "播放历史");
    }

    private void SelectAlbumPage(bool showFavoritesOnly)
    {
        _suppressNavigation = true;
        CategoryList.SelectedItem = null;
        AlbumList.SelectedItem = null;
        AlbumGridList.SelectedItem = null;
        CircleGridList.SelectedItem = null;
        PlaylistList.SelectedItem = null;
        _suppressNavigation = false;
        _view = showFavoritesOnly ? LibraryView.FavoriteAlbums : LibraryView.Albums;
        _currentCategory = null;
        _currentAlbumKey = null;
        _currentCircleKey = null;
        _currentPlaylist = null;
        CurrentViewTitle.Text = showFavoritesOnly ? "收藏专辑" : "专辑";
        ApplyAlbumFilter();
        ShowPlayerView(false);
    }

    private void SelectCirclePage()
    {
        _suppressNavigation = true;
        CategoryList.SelectedItem = null;
        AlbumList.SelectedItem = null;
        AlbumGridList.SelectedItem = null;
        CircleGridList.SelectedItem = null;
        PlaylistList.SelectedItem = null;
        _suppressNavigation = false;
        _view = LibraryView.Circles;
        _currentCategory = null;
        _currentAlbumKey = null;
        _currentCircleKey = null;
        _currentPlaylist = null;
        CurrentViewTitle.Text = "社团";
        ApplyCircleFilter();
        ShowPlayerView(false);
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation || CategoryList.SelectedItem is not string category)
            return;
        _suppressNavigation = true;
        AlbumList.SelectedItem = null;
        AlbumGridList.SelectedItem = null;
        CircleGridList.SelectedItem = null;
        PlaylistList.SelectedItem = null;
        _suppressNavigation = false;
        _view = LibraryView.Category;
        _currentCategory = category;
        _currentAlbumKey = null;
        _currentCircleKey = null;
        _currentPlaylist = null;
        CurrentViewTitle.Text = category;
        ApplyFilter();
    }

    private void AlbumList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation || AlbumList.SelectedItem is not AlbumViewModel album)
            return;
        SelectAlbumTracks(album);
    }

    private void AlbumGridList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation || AlbumGridList.SelectedItem is not AlbumViewModel album)
            return;
        SelectAlbumTracks(album);
    }

    private void CircleGridList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation || CircleGridList.SelectedItem is not CircleViewModel circle)
            return;
        SelectCircleTracks(circle);
    }

    private void SelectAlbumTracks(AlbumViewModel album)
    {
        _suppressNavigation = true;
        CategoryList.SelectedItem = null;
        AlbumList.SelectedItem = _albums.FirstOrDefault(item => item.Key == album.Key);
        AlbumGridList.SelectedItem = null;
        CircleGridList.SelectedItem = null;
        PlaylistList.SelectedItem = null;
        _suppressNavigation = false;
        _view = LibraryView.Album;
        _currentAlbumKey = album.Key;
        _currentCategory = null;
        _currentCircleKey = null;
        _currentPlaylist = null;
        CurrentViewTitle.Text = album.DisplayName;
        ApplyFilter();
        ShowPlayerView(false);
    }

    private void SelectCircleTracks(CircleViewModel circle)
    {
        _suppressNavigation = true;
        CategoryList.SelectedItem = null;
        AlbumList.SelectedItem = null;
        AlbumGridList.SelectedItem = null;
        CircleGridList.SelectedItem = null;
        PlaylistList.SelectedItem = null;
        _suppressNavigation = false;
        _view = LibraryView.Circle;
        _currentCircleKey = circle.Key;
        _currentCategory = null;
        _currentAlbumKey = null;
        _currentPlaylist = null;
        CurrentViewTitle.Text = $"社团 · {circle.Name}";
        ApplyFilter();
        ShowPlayerView(false);
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation || PlaylistList.SelectedItem is not PlaylistModel playlist)
            return;
        _suppressNavigation = true;
        CategoryList.SelectedItem = null;
        AlbumList.SelectedItem = null;
        AlbumGridList.SelectedItem = null;
        CircleGridList.SelectedItem = null;
        _suppressNavigation = false;
        _view = LibraryView.Playlist;
        _currentPlaylist = playlist;
        _currentCategory = null;
        _currentAlbumKey = null;
        _currentCircleKey = null;
        CurrentViewTitle.Text = playlist.Name;
        UpdatePlaylistHeader();
        ApplyFilter();
        ShowPlayerView(false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null)
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (IsAlbumPage)
            ApplyAlbumFilter();
        else if (IsCirclePage)
            ApplyCircleFilter();
        else
            ApplyFilter();
    }
    private void AllAlbumsFilterButton_Click(object sender, RoutedEventArgs e) => SelectAlbumPage(showFavoritesOnly: false);
    private void FavoriteAlbumsFilterButton_Click(object sender, RoutedEventArgs e) => SelectAlbumPage(showFavoritesOnly: true);
    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSortCombo || SortCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string sortKey)
            return;

        _trackSortKey = sortKey;
        _trackSortDirection = DefaultSortDirection(sortKey);
        ApplyFilter();
    }

    private void TrackGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var sortKey = e.Column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(sortKey))
            return;

        e.Handled = true;
        _trackSortDirection = string.Equals(_trackSortKey, sortKey, StringComparison.Ordinal)
            ? (_trackSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending)
            : DefaultSortDirection(sortKey);
        _trackSortKey = sortKey;
        SyncSortCombo(sortKey);
        ApplyFilter();

        var directionText = _trackSortDirection == ListSortDirection.Ascending ? "升序" : "降序";
        StatusText.Text = $"按“{e.Column.Header}”{directionText}排列";
    }

    private IEnumerable<TrackModel> SortTracks(IEnumerable<TrackModel> tracks)
    {
        var descending = _trackSortDirection == ListSortDirection.Descending;
        var textComparer = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<TrackModel> ordered;

        if (_trackSortKey == "TrackNumber")
        {
            var numbered = tracks.OrderBy(track => track.TrackNumber == 0);
            ordered = descending
                ? numbered.ThenByDescending(track => track.TrackNumber)
                : numbered.ThenBy(track => track.TrackNumber);
        }
        else if (_trackSortKey == "ArtistAlbum")
        {
            ordered = OrderByDirection(tracks, track => track.Artist, descending, textComparer);
            ordered = descending
                ? ordered.ThenByDescending(track => track.Album, textComparer)
                    .ThenByDescending(track => track.TrackNumber)
                : ordered.ThenBy(track => track.Album, textComparer)
                    .ThenBy(track => track.TrackNumber);
        }
        else
        {
            ordered = _trackSortKey switch
            {
                "Title" => OrderByDirection(tracks, track => track.Title, descending, textComparer),
                "AddedAt" => OrderByDirection(tracks, track => track.AddedAt, descending),
                "LastPlayedAt" => OrderByDirection(tracks, track => track.LastPlayedAt, descending),
                "PlayCount" => OrderByDirection(tracks, track => track.PlayCount, descending),
                "IsFavorite" => OrderByDirection(tracks, track => track.IsFavorite, descending),
                "Artist" => OrderByDirection(tracks, track => track.Artist, descending, textComparer),
                "Album" => OrderByDirection(tracks, track => track.Album, descending, textComparer),
                "CircleText" => OrderByDirection(tracks, track => track.CircleText, descending, textComparer),
                "CategoryText" => OrderByDirection(tracks, track => track.CategoryText, descending, textComparer),
                "DurationMs" => OrderByDirection(tracks, track => track.DurationMs, descending),
                "Format" => OrderByDirection(tracks, track => track.Format, descending, textComparer),
                _ => OrderByDirection(tracks, track => track.Title, descending, textComparer)
            };
        }

        return ordered
            .ThenBy(track => track.Artist, textComparer)
            .ThenBy(track => track.Album, textComparer)
            .ThenBy(track => track.TrackNumber)
            .ThenBy(track => track.Title, textComparer)
            .ThenBy(track => track.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static IOrderedEnumerable<TrackModel> OrderByDirection<TKey>(
        IEnumerable<TrackModel> tracks,
        Func<TrackModel, TKey> selector,
        bool descending,
        IComparer<TKey>? comparer = null) =>
        descending ? tracks.OrderByDescending(selector, comparer) : tracks.OrderBy(selector, comparer);

    private static ListSortDirection DefaultSortDirection(string sortKey) =>
        sortKey is "AddedAt" or "LastPlayedAt" or "PlayCount" or "IsFavorite" or "DurationMs"
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

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
        if (TrackGrid is null)
            return;

        foreach (var column in TrackGrid.Columns)
            column.SortDirection = null;

        var indicatorKey = _trackSortKey == "ArtistAlbum" ? "Artist" : _trackSortKey;
        var columnToMark = TrackGrid.Columns.FirstOrDefault(column =>
            string.Equals(column.SortMemberPath, indicatorKey, StringComparison.Ordinal));
        if (columnToMark is not null)
            columnToMark.SortDirection = _trackSortDirection;
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择本地音乐总文件夹",
            InitialDirectory = _state.LibraryFolders.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dialog.ShowDialog(this) != true)
            return;
        if (!_state.LibraryFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
            _state.LibraryFolders.Add(dialog.FolderName);
        await ScanLibraryAsync();
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e) => await ScanLibraryAsync();

    private async void ReidentifyCirclesButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanLibraryAsync();
        if (IsCirclePage)
            ApplyCircleFilter();
    }

    private async void CreatePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var playlist = new PlaylistModel { Name = MakeUniquePlaylistName("新歌单") };
        var dialog = new PlaylistDetailsWindow(playlist, null) { Owner = this };
        if (dialog.ShowDialog() != true || !ApplyPlaylistDetails(playlist, dialog))
            return;
        _state.Playlists.Add(playlist);
        _currentPlaylist = playlist;
        await _store.SaveAsync(_state);
        RefreshNavigation();
        PlaylistList.SelectedItem = playlist;
        StatusText.Text = $"已创建歌单“{playlist.Name}”";
    }

    private async void RenamePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var playlist = PlaylistList.SelectedItem as PlaylistModel ?? _currentPlaylist;
        if (playlist is null)
            return;
        await EditPlaylistAsync(playlist);
    }

    private async void EditPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is not null)
            await EditPlaylistAsync(_currentPlaylist);
    }

    private async Task EditPlaylistAsync(PlaylistModel playlist)
    {
        var dialog = new PlaylistDetailsWindow(playlist, playlist.CoverThumbnail) { Owner = this };
        if (dialog.ShowDialog() != true || !ApplyPlaylistDetails(playlist, dialog))
            return;

        await _store.SaveAsync(_state);
        RefreshNavigation();
        PlaylistList.SelectedItem = playlist;
        CurrentViewTitle.Text = playlist.Name;
        UpdatePlaylistHeader();
        StatusText.Text = $"已保存歌单“{playlist.Name}”的资料";
    }

    private bool ApplyPlaylistDetails(PlaylistModel playlist, PlaylistDetailsWindow dialog)
    {
        var coverPath = playlist.CoverPath;
        try
        {
            if (dialog.RemoveCustomCover)
            {
                coverPath = "";
            }
            else if (!string.IsNullOrWhiteSpace(dialog.SelectedCoverFile))
            {
                Directory.CreateDirectory(_store.PlaylistArtworkDirectory);
                var extension = Path.GetExtension(dialog.SelectedCoverFile).ToLowerInvariant();
                if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".bmp")
                    extension = ".image";
                var destination = Path.Combine(_store.PlaylistArtworkDirectory, $"{playlist.Id}{extension}");
                if (!string.Equals(Path.GetFullPath(dialog.SelectedCoverFile), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    File.Copy(dialog.SelectedCoverFile, destination, true);
                coverPath = destination;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, exception.Message, "无法保存歌单封面", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        playlist.Name = MakeUniquePlaylistName(dialog.PlaylistName, playlist.Id);
        playlist.Description = dialog.Description;
        playlist.Tags = dialog.Tags.ToList();
        playlist.CoverPath = coverPath;
        if (playlist.CreatedAt == default)
            playlist.CreatedAt = DateTime.Now;
        playlist.UpdatedAt = DateTime.Now;
        playlist.InvalidateCover();
        playlist.NotifyMetadataChanged();
        return true;
    }

    private void PlaylistPlayAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is not null)
            PlayTracks(GetPlaylistTracks(_currentPlaylist));
    }

    private async void DeletePlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not PlaylistModel playlist)
            return;
        var answer = MessageBox.Show(this, $"删除歌单“{playlist.Name}”？\n不会删除任何音乐文件。", "删除歌单",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        _state.Playlists.Remove(playlist);
        SelectLibraryView(LibraryView.All, "全部音乐");
        await _store.SaveAsync(_state);
        RefreshNavigation();
    }

    private async void AddToPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedTracks();
        await AddTracksToPlaylistAsync(selected);
    }

    private async Task AddTracksToPlaylistAsync(IReadOnlyCollection<TrackModel> selected)
    {
        if (selected.Count == 0)
            return;

        var tracks = selected.DistinctBy(track => track.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var dialog = new TrackDestinationWindow(_state.Playlists, tracks.Count) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var destinations = dialog.SelectedPlaylistIds
            .Select(id => _state.Playlists.FirstOrDefault(playlist =>
                string.Equals(playlist.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(playlist => playlist is not null)
            .Cast<PlaylistModel>()
            .ToList();
        var createdPlaylist = false;
        if (dialog.NewPlaylistName.Length > 0)
        {
            var playlist = _state.Playlists.FirstOrDefault(item =>
                string.Equals(item.Name, dialog.NewPlaylistName, StringComparison.CurrentCultureIgnoreCase));
            if (playlist is null)
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

        var addedEntries = 0;
        var changedPlaylists = new List<PlaylistModel>();
        foreach (var playlist in destinations.DistinctBy(playlist => playlist.Id, StringComparer.OrdinalIgnoreCase))
        {
            var before = playlist.TrackIds.Count;
            foreach (var track in tracks)
                if (!playlist.TrackIds.Contains(track.Id, StringComparer.OrdinalIgnoreCase))
                    playlist.TrackIds.Add(track.Id);
            var added = playlist.TrackIds.Count - before;
            if (added == 0)
                continue;
            addedEntries += added;
            playlist.UpdatedAt = DateTime.Now;
            playlist.InvalidateCover();
            playlist.NotifyMetadataChanged();
            changedPlaylists.Add(playlist);
        }

        if (changedPlaylists.Count > 0 || createdPlaylist)
        {
            await _store.SaveAsync(_state);
            RefreshNavigation();
            if (_view == LibraryView.Playlist)
                ApplyFilter();
        }

        if (dialog.PlayNext)
            QueueTracksNext(tracks);
        var queued = dialog.AppendToQueue ? AppendTracksToQueue(tracks) : 0;

        var results = new List<string>();
        if (destinations.Count > 0)
            results.Add($"{destinations.DistinctBy(item => item.Id).Count():N0} 个歌单新增 {addedEntries:N0} 条");
        if (dialog.PlayNext)
            results.Add("已安排下一首");
        if (dialog.AppendToQueue)
            results.Add($"队列新增 {queued:N0} 首");
        StatusText.Text = results.Count == 0 ? "所选歌曲已在目标中" : string.Join(" · ", results);
    }

    private async void RemoveFromPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is null)
            return;
        var selected = GetSelectedTracks();
        if (selected.Count == 0)
            return;
        var ids = selected.Select(track => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _currentPlaylist.TrackIds.RemoveAll(ids.Contains);
        _currentPlaylist.UpdatedAt = DateTime.Now;
        _currentPlaylist.InvalidateCover();
        _currentPlaylist.NotifyMetadataChanged();
        await _store.SaveAsync(_state);
        RefreshNavigation();
        ApplyFilter();
        StatusText.Text = $"已从歌单移出 {selected.Count:N0} 首歌曲";
    }

    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedTracks();
        if (selected.Count == 0)
            return;
        var category = Prompt("歌曲分类", "分类名称");
        if (category is null)
            return;
        foreach (var track in selected)
            if (!track.Categories.Contains(category, StringComparer.CurrentCultureIgnoreCase))
                track.Categories.Add(category);
        await _store.SaveAsync(_state);
        RefreshNavigation();
        TrackGrid.Items.Refresh();
        StatusText.Text = $"已为 {selected.Count} 首歌曲添加分类“{category}”";
    }

    private async void RemoveCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedTracks();
        if (selected.Count == 0)
            return;
        var category = _currentCategory ?? Prompt("移除分类", "要移除的分类名称");
        if (category is null)
            return;
        foreach (var track in selected)
            track.Categories.RemoveAll(value => string.Equals(value, category, StringComparison.CurrentCultureIgnoreCase));
        await _store.SaveAsync(_state);
        RefreshNavigation();
        if (_view == LibraryView.Category)
            SelectLibraryView(LibraryView.All, "全部音乐");
        else
            TrackGrid.Items.Refresh();
        StatusText.Text = $"已从 {selected.Count} 首歌曲移除分类“{category}”";
    }

    private async void ToggleFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedTracks();
        if (selected.Count == 0)
            return;
        var favorite = !selected.All(track => track.IsFavorite);
        foreach (var track in selected)
            track.IsFavorite = favorite;
        await _store.SaveAsync(_state);
        if (_view == LibraryView.Favorites)
            ApplyFilter();
        else
            TrackGrid.Items.Refresh();
        StatusText.Text = favorite ? $"已收藏 {selected.Count} 首歌曲" : $"已取消收藏 {selected.Count} 首歌曲";
    }

    private async void ToggleAlbumFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        var albumKeys = GetSelectedTracks()
            .Select(AlbumIdentity.Create)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (albumKeys.Count == 0 && _currentAlbumKey is not null)
            albumKeys.Add(_currentAlbumKey);
        if (albumKeys.Count == 0)
            return;

        var favorite = !albumKeys.All(IsFavoriteAlbum);
        foreach (var key in albumKeys)
        {
            if (favorite)
                AddFavoriteAlbum(key);
            else
                RemoveFavoriteAlbum(key);
        }

        await _store.SaveAsync(_state);
        RefreshNavigation();
        StatusText.Text = favorite ? $"已收藏 {albumKeys.Count} 张专辑" : $"已取消收藏 {albumKeys.Count} 张专辑";
    }

    private async void FavoriteAlbumCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AlbumViewModel album })
            return;

        var favorite = !IsFavoriteAlbum(album.Key);
        if (favorite)
            AddFavoriteAlbum(album.Key);
        else
            RemoveFavoriteAlbum(album.Key);

        await _store.SaveAsync(_state);
        RefreshNavigation();
        StatusText.Text = favorite ? $"已收藏专辑“{album.Title}”" : $"已取消收藏专辑“{album.Title}”";
        e.Handled = true;
    }

    private async void ImportNetEaseButton_Click(object sender, RoutedEventArgs e)
    {
        var source = Prompt("导入网易云歌单", "粘贴公开歌单链接或歌单 ID");
        if (source is null)
            return;

        ImportNetEaseButton.IsEnabled = false;
        try
        {
            await SyncLibraryBeforeImportAsync();
            StatusText.Text = "正在读取网易云歌单并匹配本地歌曲…";
            var result = await _netEaseService.ImportAsync(source, _state.Tracks);
            var playlist = _state.Playlists.FirstOrDefault(item => item.CloudPlaylistId == result.PlaylistId);
            if (playlist is null)
            {
                playlist = new PlaylistModel
                {
                    Name = MakeUniquePlaylistName(result.PlaylistName),
                    Source = "netease",
                    CloudPlaylistId = result.PlaylistId,
                    Description = $"从网易云歌单 {result.PlaylistId} 导入",
                    Tags = ["网易云"]
                };
                _state.Playlists.Add(playlist);
            }
            else
            {
                playlist.Name = result.PlaylistName;
            }
            var importedTrackIds = result.Matched
                .Select(track => track.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!result.HasCompleteRemoteDetails && playlist.TrackIds.Count > 0)
            {
                // A temporary NetEase detail failure must never erase entries imported successfully before.
                importedTrackIds = importedTrackIds
                    .Concat(playlist.TrackIds)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            playlist.TrackIds = importedTrackIds;
            playlist.UpdatedAt = DateTime.Now;
            playlist.InvalidateCover();
            playlist.NotifyMetadataChanged();
            await _store.SaveAsync(_state);
            RefreshNavigation();
            PlaylistList.SelectedItem = playlist;

            var missingPreview = string.Join("\n", result.Missing.Take(8).Select(track =>
                string.IsNullOrWhiteSpace(track.Title)
                    ? $"• 网易云歌曲 ID {track.Id}（详情暂未返回）"
                    : $"• {track.Title} - {track.Artist}"));
            var ncmCount = _state.Tracks.Count(track => track.IsEncryptedNcm);
            var message = $"歌单：{result.PlaylistName}\n" +
                          $"网易云声明歌曲：{result.DeclaredTrackCount}\n" +
                          $"已取得完整歌曲 ID：{result.TrackIdCount}\n" +
                          $"已读取歌曲详情：{result.ResolvedTrackCount}\n\n" +
                          $"已同步本地文件：{_state.Tracks.Count}\n" +
                          $"其中 NCM 文件：{ncmCount}\n" +
                          $"云 ID 精确匹配：{result.ExactMatchCount}\n" +
                          $"名称/艺术家/专辑匹配：{result.FuzzyMatchCount}\n" +
                          $"已匹配本地文件：{result.Matched.Count}\n" +
                          $"本地确实缺少或未匹配：{result.Missing.Count}";
            if (!result.HasCompleteTrackIds)
                message += "\n\n警告：网易云没有返回完整歌曲 ID；本次已保留原歌单内容，避免临时接口异常造成歌曲丢失。";
            else if (result.UnresolvedTrackIds.Count > 0)
                message += $"\n\n提示：有 {result.UnresolvedTrackIds.Count} 首暂未取得详情；已有云 ID 的本地文件仍可导入，并已保留原歌单内容。";
            if (missingPreview.Length > 0)
                message += $"\n\n部分未匹配歌曲：\n{missingPreview}";
            MessageBox.Show(this, message, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = $"已导入“{result.PlaylistName}”，匹配 {result.Matched.Count} / {result.DeclaredTrackCount} 首";
        }
        catch (Exception exception)
        {
            StatusText.Text = "网易云歌单导入失败";
            MessageBox.Show(this, exception.Message, "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ImportNetEaseButton.IsEnabled = true;
        }
    }

    private async Task SyncLibraryBeforeImportAsync()
    {
        if (_state.LibraryFolders.Count == 0)
            return;
        if (_isScanning)
            throw new InvalidOperationException("曲库正在扫描，请等待当前扫描完成后再导入歌单。");

        _isScanning = true;
        RescanButton.IsEnabled = false;
        AddFolderButton.IsEnabled = false;
        var progress = new Progress<ScanProgress>(value =>
        {
            StatusText.Text = $"导入前同步音乐文件夹 {value.Scanned:N0} / {value.Total:N0}，元数据回退 {value.Errors:N0}";
            TrackCountText.Text = Path.GetFileName(value.CurrentFile);
        });

        try
        {
            _state.Tracks = await _libraryService.ScanAsync(_state.LibraryFolders, _state.Tracks, progress);
            await _store.SaveAsync(_state);
            RefreshNavigation();
            if (!IsAlbumPage && !IsCirclePage)
                ApplyFilter();
        }
        finally
        {
            _isScanning = false;
            RescanButton.IsEnabled = true;
            AddFolderButton.IsEnabled = true;
        }
    }

    private List<TrackModel> GetSelectedTracks()
    {
        var selected = TrackGrid.SelectedItems.Cast<TrackModel>().ToList();
        if (selected.Count == 0 && TrackGrid.SelectedItem is TrackModel track)
            selected.Add(track);
        return selected;
    }

    private void TrackGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        var row = FindVisualParent<DataGridRow>(source);
        if (row is null)
            return;
        if (!row.IsSelected)
        {
            TrackGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
        row.Focus();
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
                return target;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static T? GetContextItem<T>(object sender) where T : class
    {
        if (sender is not MenuItem menuItem || menuItem.Parent is not ContextMenu contextMenu)
            return null;
        return (contextMenu.PlacementTarget as FrameworkElement)?.DataContext as T;
    }

    private List<TrackModel> GetAlbumTracks(AlbumViewModel album) => _state.Tracks
        .Where(track => string.Equals(AlbumIdentity.Create(track), album.Key, StringComparison.OrdinalIgnoreCase))
        .OrderBy(track => track.TrackNumber)
        .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private List<TrackModel> GetCircleTracks(CircleViewModel circle) => _state.Tracks
        .Where(track => string.Equals(CircleIdentity.Create(track), circle.Key, StringComparison.OrdinalIgnoreCase))
        .OrderBy(track => track.Album, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(track => track.TrackNumber)
        .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private List<TrackModel> GetPlaylistTracks(PlaylistModel playlist)
    {
        var tracksById = _state.Tracks
            .GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return playlist.TrackIds
            .Select(id => tracksById.GetValueOrDefault(id))
            .Where(track => track is not null)
            .Cast<TrackModel>()
            .DistinctBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void PlayTracks(IReadOnlyCollection<TrackModel> tracks)
    {
        _queue = tracks.DistinctBy(track => track.Id, StringComparer.OrdinalIgnoreCase).ToList();
        if (_queue.Count == 0)
            return;
        _queueIndex = 0;
        QueueList.ItemsSource = null;
        QueueList.ItemsSource = _queue;
        QueueList.SelectedItem = _queue[0];
        PlayTrack(_queue[0]);
    }

    private void QueueTracksNext(IReadOnlyCollection<TrackModel> tracks)
    {
        var additions = tracks
            .Where(track => _currentTrack is null || !string.Equals(track.Id, _currentTrack.Id, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (additions.Count == 0)
            return;

        var additionIds = additions.Select(track => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = _queue.Where(track => !additionIds.Contains(track.Id)).ToList();
        var currentIndex = _currentTrack is null
            ? -1
            : remaining.FindIndex(track => string.Equals(track.Id, _currentTrack.Id, StringComparison.OrdinalIgnoreCase));
        if (_currentTrack is not null && currentIndex < 0)
        {
            remaining.Insert(0, _currentTrack);
            currentIndex = 0;
        }

        remaining.InsertRange(currentIndex + 1, additions);
        _queue = remaining;
        _queueIndex = currentIndex;
        QueueList.ItemsSource = null;
        QueueList.ItemsSource = _queue;
        StatusText.Text = $"已将 {additions.Count:N0} 首歌曲安排为下一首播放";
    }

    private int AppendTracksToQueue(IReadOnlyCollection<TrackModel> tracks)
    {
        var existingIds = _queue.Select(track => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = tracks
            .Where(track => existingIds.Add(track.Id))
            .ToList();
        if (additions.Count == 0)
            return 0;

        _queue.AddRange(additions);
        QueueList.ItemsSource = null;
        QueueList.ItemsSource = _queue;
        StatusText.Text = $"已向播放队列末尾添加 {additions.Count:N0} 首歌曲";
        return additions.Count;
    }

    private async Task ToggleTrackFavoritesAsync(IReadOnlyCollection<TrackModel> tracks)
    {
        if (tracks.Count == 0)
            return;
        var favorite = !tracks.All(track => track.IsFavorite);
        foreach (var track in tracks)
            track.IsFavorite = favorite;
        await _store.SaveAsync(_state);
        if (_view == LibraryView.Favorites)
            ApplyFilter();
        else
            TrackGrid.Items.Refresh();
        StatusText.Text = favorite ? $"已收藏 {tracks.Count:N0} 首歌曲" : $"已取消收藏 {tracks.Count:N0} 首歌曲";
    }

    private void CopyText(string text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = successMessage;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法写入剪贴板", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyFilesForSharing(IEnumerable<TrackModel> tracks)
    {
        var paths = tracks.Select(track => track.FilePath).Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
            return;
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            data.SetText(string.Join(Environment.NewLine, paths));
            Clipboard.SetDataObject(data, true);
            StatusText.Text = $"已复制 {paths.Length:N0} 个歌曲文件，可直接粘贴分享";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法复制歌曲文件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenTrackFolder(TrackModel? track)
    {
        if (track is null || !File.Exists(track.FilePath))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{track.FilePath}\"") { UseShellExecute = true });
    }

    private void TrackContextPlay_Click(object sender, RoutedEventArgs e)
    {
        var tracks = GetSelectedTracks();
        if (tracks.Count > 0)
            PlayFromVisibleTracks(tracks[0]);
    }

    private void TrackContextPlayNext_Click(object sender, RoutedEventArgs e) => QueueTracksNext(GetSelectedTracks());
    private void TrackContextAppendQueue_Click(object sender, RoutedEventArgs e) => AppendTracksToQueue(GetSelectedTracks());
    private async void TrackContextFavorite_Click(object sender, RoutedEventArgs e) => await ToggleTrackFavoritesAsync(GetSelectedTracks());
    private async void TrackContextAddPlaylist_Click(object sender, RoutedEventArgs e) => await AddTracksToPlaylistAsync(GetSelectedTracks());
    private void TrackContextAddCategory_Click(object sender, RoutedEventArgs e) => AddCategoryButton_Click(sender, e);
    private void TrackContextShare_Click(object sender, RoutedEventArgs e) => CopyFilesForSharing(GetSelectedTracks());
    private void TrackContextCopyPath_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, GetSelectedTracks().Select(track => track.FilePath)), "已复制本地文件路径");
    private void TrackContextCopyInfo_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join(Environment.NewLine, GetSelectedTracks().Select(track =>
            $"{track.Title} - {track.Artist} | {track.Album}{(string.IsNullOrWhiteSpace(track.Circle) ? "" : $" | {track.Circle}")}")), "已复制歌曲信息");
    private void TrackContextOpenFolder_Click(object sender, RoutedEventArgs e) => OpenTrackFolder(GetSelectedTracks().FirstOrDefault());
    private void TrackContextRemovePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is null)
        {
            StatusText.Text = "当前页面不是歌单";
            return;
        }
        RemoveFromPlaylistButton_Click(sender, e);
    }

    private void TrackContextOpenAlbum_Click(object sender, RoutedEventArgs e)
    {
        var track = GetSelectedTracks().FirstOrDefault();
        var album = track is null ? null : _albums.FirstOrDefault(item =>
            string.Equals(item.Key, AlbumIdentity.Create(track), StringComparison.OrdinalIgnoreCase));
        if (album is not null)
            SelectAlbumTracks(album);
    }

    private void TrackContextOpenCircle_Click(object sender, RoutedEventArgs e)
    {
        var track = GetSelectedTracks().FirstOrDefault();
        if (track is null || string.IsNullOrWhiteSpace(track.Circle))
        {
            StatusText.Text = "这首歌尚未识别社团，可用右键“设置/修正社团”";
            return;
        }
        var circle = _circles.FirstOrDefault(item =>
            string.Equals(item.Key, CircleIdentity.Create(track), StringComparison.OrdinalIgnoreCase));
        if (circle is not null)
            SelectCircleTracks(circle);
    }

    private async void TrackContextSetCircle_Click(object sender, RoutedEventArgs e)
    {
        var tracks = GetSelectedTracks();
        if (tracks.Count == 0)
            return;
        var initial = tracks.Select(track => track.Circle).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == 1
            ? tracks[0].Circle
            : "";
        var circle = Prompt("设置社团", $"为选中的 {tracks.Count:N0} 首歌曲设置社团名称", initial);
        if (circle is null)
            return;
        foreach (var track in tracks)
        {
            track.Circle = circle.Trim();
            track.CircleIsManual = true;
        }
        await _store.SaveAsync(_state);
        RefreshNavigation();
        if (!IsAlbumPage && !IsCirclePage)
            ApplyFilter();
        TrackGrid.Items.Refresh();
        StatusText.Text = $"已为 {tracks.Count:N0} 首歌曲设置社团“{circle.Trim()}”";
    }

    private async void TrackContextClearCircle_Click(object sender, RoutedEventArgs e)
    {
        var tracks = GetSelectedTracks();
        if (tracks.Count == 0)
            return;
        foreach (var track in tracks)
        {
            track.Circle = "";
            track.CircleIsManual = true;
        }
        await _store.SaveAsync(_state);
        RefreshNavigation();
        if (!IsAlbumPage && !IsCirclePage)
            ApplyFilter();
        TrackGrid.Items.Refresh();
        StatusText.Text = $"已清除 {tracks.Count:N0} 首歌曲的社团信息";
    }

    private void AlbumContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is { } album)
            SelectAlbumTracks(album);
    }

    private void AlbumContextPlay_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is { } album)
            PlayTracks(GetAlbumTracks(album));
    }

    private void AlbumContextPlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is { } album)
            QueueTracksNext(GetAlbumTracks(album));
    }

    private async void AlbumContextFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is not { } album)
            return;
        if (IsFavoriteAlbum(album.Key))
            RemoveFavoriteAlbum(album.Key);
        else
            AddFavoriteAlbum(album.Key);
        await _store.SaveAsync(_state);
        RefreshNavigation();
        StatusText.Text = IsFavoriteAlbum(album.Key) ? $"已收藏专辑“{album.Title}”" : $"已取消收藏专辑“{album.Title}”";
    }

    private async void AlbumContextSetCircle_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is not { } album)
            return;
        var tracks = GetAlbumTracks(album);
        var circles = tracks.Where(track => !string.IsNullOrWhiteSpace(track.Circle))
            .Select(track => track.Circle.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var initial = circles.Count == 1 ? circles[0] : "";
        var circle = Prompt("设置专辑社团", $"为专辑“{album.Title}”的 {tracks.Count:N0} 首歌曲设置社团", initial);
        if (circle is null)
            return;

        foreach (var track in tracks)
        {
            track.Circle = circle.Trim();
            track.CircleIsManual = true;
        }
        await _store.SaveAsync(_state);
        RefreshNavigation();
        if (!IsAlbumPage && !IsCirclePage)
            ApplyFilter();
        TrackGrid.Items.Refresh();
        StatusText.Text = circle.Trim().Length == 0
            ? $"已清除专辑“{album.Title}”的社团信息"
            : $"已将专辑“{album.Title}”归入社团“{circle.Trim()}”";
    }

    private async void AlbumContextAddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is { } album)
            await AddTracksToPlaylistAsync(GetAlbumTracks(album));
    }

    private void AlbumContextShare_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is { } album)
            CopyFilesForSharing(GetAlbumTracks(album));
    }

    private void AlbumContextCopy_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<AlbumViewModel>(sender) is { } album)
            CopyText($"{album.Title} - {album.Artist} | {album.TrackCount:N0} 首", "已复制专辑信息");
    }

    private void AlbumContextOpenFolder_Click(object sender, RoutedEventArgs e) =>
        OpenTrackFolder(GetContextItem<AlbumViewModel>(sender)?.RepresentativeTrack);

    private void CircleContextOpen_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<CircleViewModel>(sender) is { } circle)
            SelectCircleTracks(circle);
    }

    private void CircleContextPlay_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<CircleViewModel>(sender) is { } circle)
            PlayTracks(GetCircleTracks(circle));
    }

    private void CircleContextPlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<CircleViewModel>(sender) is { } circle)
            QueueTracksNext(GetCircleTracks(circle));
    }

    private async void CircleContextAddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<CircleViewModel>(sender) is { } circle)
            await AddTracksToPlaylistAsync(GetCircleTracks(circle));
    }

    private void CircleContextShare_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<CircleViewModel>(sender) is { } circle)
            CopyFilesForSharing(GetCircleTracks(circle));
    }

    private void CircleContextCopy_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem<CircleViewModel>(sender) is { } circle)
            CopyText(circle.Name, "已复制社团名称");
    }

    private void CircleContextOpenFolder_Click(object sender, RoutedEventArgs e) =>
        OpenTrackFolder(GetContextItem<CircleViewModel>(sender)?.RepresentativeTrack);

    private bool IsFavoriteAlbum(string albumKey) =>
        _state.FavoriteAlbumKeys.Contains(albumKey, StringComparer.OrdinalIgnoreCase);

    private void AddFavoriteAlbum(string albumKey)
    {
        if (!IsFavoriteAlbum(albumKey))
            _state.FavoriteAlbumKeys.Add(albumKey);
    }

    private void RemoveFavoriteAlbum(string albumKey) =>
        _state.FavoriteAlbumKeys.RemoveAll(key => string.Equals(key, albumKey, StringComparison.OrdinalIgnoreCase));

    private string? Prompt(string title, string prompt, string initialValue = "")
    {
        var window = new TextPromptWindow(title, prompt, initialValue) { Owner = this };
        return window.ShowDialog() == true ? window.Value : null;
    }

    private string MakeUniquePlaylistName(string requested, string? excludedId = null)
    {
        var clean = requested.Trim();
        var candidate = clean;
        var suffix = 2;
        while (_state.Playlists.Any(playlist => playlist.Id != excludedId &&
               string.Equals(playlist.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
            candidate = $"{clean} ({suffix++})";
        return candidate;
    }

    private void TrackGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackGrid.SelectedItem is not TrackModel track)
            return;
        if (string.Equals(_state.DoubleClickQueueMode, "Append", StringComparison.OrdinalIgnoreCase))
            AppendToQueueAndPlay(track);
        else
            PlayFromVisibleTracks(track);
    }

    private void PlayTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrackModel track })
            return;
        PlayFromVisibleTracks(track);
        e.Handled = true;
    }

    private async void AddTrackDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrackModel track })
            return;
        await AddTracksToPlaylistAsync([track]);
        e.Handled = true;
    }

    private async void FavoriteTrackButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TrackModel track })
            return;
        track.IsFavorite = !track.IsFavorite;
        await _store.SaveAsync(_state);
        if (_view == LibraryView.Favorites && !track.IsFavorite)
            ApplyFilter();
        else
            TrackGrid.Items.Refresh();
        StatusText.Text = track.IsFavorite ? $"已收藏“{track.Title}”" : $"已取消收藏“{track.Title}”";
        e.Handled = true;
    }

    private void PlayFromVisibleTracks(TrackModel track)
    {
        _queue = _visibleTracks.ToList();
        _queueIndex = _queue.FindIndex(item => item.Id == track.Id);
        QueueList.ItemsSource = _queue;
        PlayTrack(track);
    }

    private void AppendToQueueAndPlay(TrackModel track)
    {
        var index = _queue.FindIndex(item => string.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _queue.Add(track);
            index = _queue.Count - 1;
        }
        _queueIndex = index;
        QueueList.ItemsSource = null;
        QueueList.ItemsSource = _queue;
        QueueList.SelectedItem = track;
        PlayTrack(track);
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is not TrackModel track)
            return;
        _queueIndex = _queue.FindIndex(item => item.Id == track.Id);
        PlayTrack(track);
    }

    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentTrack is null && TrackGrid.SelectedItem is TrackModel track)
            ShowTrackInformation(track, updateNowPlaying: false);
    }

    private void PlayTrack(TrackModel track)
    {
        if (!File.Exists(track.FilePath))
        {
            StatusText.Text = "文件不存在，请重新扫描曲库";
            return;
        }
        if (track.IsEncryptedNcm)
        {
            StatusText.Text = "这首歌已在曲库和歌单中，但 NCM 加密文件需要先转换才能播放";
            MessageBox.Show(this,
                "该歌曲是网易云 NCM 加密文件。程序会把它计入曲库、搜索结果和导入歌单，但必须先转换为 FLAC、MP3 等普通音频格式才能播放。\n\n转换完成后点击“重新扫描”，歌单导入会优先匹配可播放版本。",
                "NCM 需要转换", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_currentTrack is not null && !string.Equals(_currentTrack.Id, track.Id, StringComparison.OrdinalIgnoreCase))
                RememberCurrentPlayback();
            if (track.IsVideo || !string.Equals(_state.VisualizationMode, "Off", StringComparison.OrdinalIgnoreCase))
                ShowPlayerView(true);
            _playback.Play(track.FilePath, _state, track.IsVideo);
            _currentTrack = track;
            _watchdogTrackId = track.Id;
            _watchdogRecoveryCount = 0;
            _playbackRecoveryInProgress = false;
            track.PlayCount++;
            track.LastPlayedAt = DateTime.Now;
            RememberRecentlyPlayed(track.Id);
            var loadVersion = ++_nowPlayingLoadVersion;
            _lyrics = [];
            _currentLyricIndex = -1;
            LyricsList.ItemsSource = _lyrics;
            InPlayerOriginalText.Text = "";
            InPlayerTranslationText.Text = "";
            InPlayerSubtitlePanel.Visibility = _state.InPlayerBilingualSubtitles ? Visibility.Visible : Visibility.Collapsed;
            ShowTrackInformation(track, updateNowPlaying: true);
            PlayerMediaSummaryText.Text = $"{track.Title}  ·  {track.MediaTypeText} / {track.Format}";
            PlayPauseButton.Content = "Ⅱ";
            _desktopLyrics?.UpdatePlayState(true);
            StatusText.Text = "正在播放，正在后台读取封面和歌词……";
            if (_desktopLyrics?.IsVisible == true)
                _desktopLyrics.UpdateLyrics(track.Title, track.Artist);
            _ = LoadNowPlayingAssetsAsync(track, loadVersion);
            ScheduleStateSave();
            if (_view == LibraryView.History && !_showingPlayerView)
                Dispatcher.BeginInvoke(ApplyFilter);
        }
        catch (Exception exception)
        {
            StatusText.Text = "播放失败";
            MessageBox.Show(this, exception.Message, "无法播放", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowTrackInformation(TrackModel track, bool updateNowPlaying)
    {
        if (updateNowPlaying)
        {
            CoverImage.Source = null;
            MiniCoverImage.Source = null;
            CoverFallback.Visibility = Visibility.Visible;
            MiniCoverFallback.Visibility = Visibility.Visible;
            NowTitleText.Text = track.Title;
            NowArtistText.Text = track.Artist;
            BottomTitleText.Text = track.Title;
            BottomArtistText.Text = track.Artist;
            PositionSlider.Maximum = Math.Max(1, track.DurationMs);
            DurationText.Text = FormatTime(track.DurationMs);
        }

        InfoTitleText.Text = track.Title;
        InfoArtistText.Text = track.Artist;
        InfoAlbumText.Text = track.Album;
        InfoCategoryText.Text = track.Categories.Count == 0 ? "未分类" : track.CategoryText;
        InfoPathText.Text = track.FilePath;
        MediaInfoText.Text = track.IsVideo
            ? "正在读取视频分辨率、帧率、音轨和编码信息…"
            : "正在读取采样率、声道、码率和编码信息…";
    }

    private async Task LoadNowPlayingAssetsAsync(TrackModel track, int loadVersion)
    {
        try
        {
            var coverTask = Task.Run(() => CoverService.LoadThumbnail(track, 720));
            var lyricsTask = Task.Run(() => LyricsService.LoadForTrack(track.FilePath));
            await Task.WhenAll(coverTask, lyricsTask);
            if (loadVersion != _nowPlayingLoadVersion ||
                !string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
                return;

            var cover = await coverTask;
            CoverImage.Source = cover;
            MiniCoverImage.Source = cover;
            CoverFallback.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;
            MiniCoverFallback.Visibility = cover is null ? Visibility.Visible : Visibility.Collapsed;

            _lyrics = await lyricsTask;
            _currentLyricIndex = -1;
            LyricsList.ItemsSource = _lyrics;
            StatusText.Text = _lyrics.Count == 0
                ? "正在播放，未找到同名 LRC 歌词"
                : $"正在播放 · {_lyrics.Count} 行歌词";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("ASSET", $"Could not load cover or lyrics for '{track.FilePath}'", exception);
            if (loadVersion == _nowPlayingLoadVersion)
                StatusText.Text = "正在播放；封面或歌词读取失败，不影响音频播放";
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (_currentTrack is null)
        {
            if (_visibleTracks.Count == 0)
                return;
            _queue = _visibleTracks.ToList();
            _queueIndex = 0;
            QueueList.ItemsSource = _queue;
            PlayTrack(_queue[0]);
            return;
        }
        _playback.TogglePause();
        PlayPauseButton.Content = _playback.IsPlaying ? "Ⅱ" : "▶";
        _desktopLyrics?.UpdatePlayState(_playback.IsPlaying);
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => GoPrevious();

    private void GoPrevious()
    {
        if (_playback.Time > 4000)
        {
            _playback.Seek(0);
            return;
        }
        PlayQueueOffset(-1, allowWrap: true);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => GoNext();

    private void GoNext() => PlayQueueOffset(1, allowWrap: _state.RepeatMode == "All");

    private void PlayQueueOffset(int offset, bool allowWrap)
    {
        if (_queue.Count == 0)
            return;
        if (_state.ShuffleMode != "Off" && _queue.Count > 1)
        {
            var selected = ShuffleService.Choose(_queue, _currentTrack, _state.ShuffleMode, _random, _recentTrackIds);
            _queueIndex = _queue.FindIndex(track => track.Id == selected.Id);
        }
        else
        {
            var next = _queueIndex + offset;
            if (next < 0 || next >= _queue.Count)
            {
                if (!allowWrap)
                {
                    _playback.Stop();
                    PlayPauseButton.Content = "▶";
                    return;
                }
                next = (next + _queue.Count) % _queue.Count;
            }
            _queueIndex = next;
        }
        PlayTrack(_queue[_queueIndex]);
        QueueList.SelectedItem = _queue[_queueIndex];
        QueueList.ScrollIntoView(_queue[_queueIndex]);
    }

    private void HandleTrackEnded()
    {
        if (_state.RepeatMode == "One" && _currentTrack is not null)
            PlayTrack(_currentTrack);
        else
            PlayQueueOffset(1, allowWrap: _state.RepeatMode == "All");
    }

    private async void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        var current = Array.IndexOf(ShuffleService.Modes, _state.ShuffleMode);
        _state.ShuffleMode = ShuffleService.Modes[(Math.Max(0, current) + 1) % ShuffleService.Modes.Length];
        _state.ShuffleEnabled = _state.ShuffleMode != "Off";
        UpdatePlaybackModeButtons();
        await _store.SaveAsync(_state);
    }

    private async void ShuffleModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode } || !ShuffleService.Modes.Contains(mode))
            return;
        _state.ShuffleMode = mode;
        _state.ShuffleEnabled = mode != "Off";
        UpdatePlaybackModeButtons();
        await _store.SaveAsync(_state);
    }

    private async void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        _state.RepeatMode = _state.RepeatMode switch
        {
            "All" => "One",
            "One" => "Off",
            _ => "All"
        };
        UpdatePlaybackModeButtons();
        await _store.SaveAsync(_state);
    }

    private void UpdatePlaybackModeButtons()
    {
        ShuffleButton.Background = _state.ShuffleMode != "Off"
            ? (System.Windows.Media.Brush)FindResource("SelectionBrush")
            : (System.Windows.Media.Brush)FindResource("AppBackgroundBrush");
        ShuffleButton.ToolTip = _state.ShuffleMode switch
        {
            "Uniform" => "当前：均匀随机。队列中每首媒体概率相同；单击切换，右键直接选择模式。",
            "Smart" => "当前：智能随机。减少近期播放、同艺术家和同专辑连续出现；单击切换，右键选择。",
            "Album" => "当前：随机专辑。先随机选择其他专辑，再播放其中一首；单击切换，右键选择。",
            "Artist" => "当前：随机艺术家。先随机选择其他艺术家，再播放其作品；单击切换，右键选择。",
            "LeastPlayed" => "当前：优先未听。优先选择播放次数最少的媒体；单击切换，右键选择。",
            _ => "当前：关闭随机，按队列顺序播放；单击切换，右键直接选择模式。"
        };
        (RepeatButton.Content, RepeatButton.ToolTip) = _state.RepeatMode switch
        {
            "One" => ("↻1", "单曲循环"),
            "Off" => ("→", "顺序播放，播完停止"),
            _ => ("↻", "列表循环")
        };
    }

    private void PlayerTimer_Tick(object? sender, EventArgs e)
    {
        if (_currentTrack is null)
            return;
        var current = _playback.Time;
        var duration = _playback.Length > 0 ? _playback.Length : _currentTrack.DurationMs;
        PositionSlider.Maximum = Math.Max(1, duration);
        if (!_seeking)
            PositionSlider.Value = Math.Clamp(current, 0, PositionSlider.Maximum);
        ElapsedText.Text = FormatTime(current);
        DurationText.Text = FormatTime(duration);
        PlayPauseButton.Content = _playback.IsPlaying ? "Ⅱ" : "▶";
        _desktopLyrics?.UpdatePlayState(_playback.IsPlaying);
        UpdateCurrentLyric(current);
        CheckPlaybackWatchdog(current, duration);
    }

    private void CheckPlaybackWatchdog(long current, long duration)
    {
        if (_currentTrack is null || _playbackRecoveryInProgress || !_playback.IsPlaying)
            return;
        if (duration > 0 && duration - current < 2500)
            return;
        if (DateTime.UtcNow - _playback.LastProgressUtc < TimeSpan.FromSeconds(12))
            return;
        _ = RecoverPlaybackAsync("播放时间超过 12 秒没有前进");
    }

    private async Task RecoverPlaybackAsync(string reason)
    {
        var track = _currentTrack;
        if (track is null || _playbackRecoveryInProgress || _shuttingDown)
            return;

        if (!string.Equals(_watchdogTrackId, track.Id, StringComparison.OrdinalIgnoreCase))
        {
            _watchdogTrackId = track.Id;
            _watchdogRecoveryCount = 0;
        }
        if (_watchdogRecoveryCount >= 3)
        {
            DiagnosticLog.Write("WATCHDOG", $"Giving up after three recoveries: '{track.FilePath}'");
            StatusText.Text = "这首歌曲连续恢复失败，已跳到下一首";
            GoNext();
            return;
        }

        _playbackRecoveryInProgress = true;
        _watchdogRecoveryCount++;
        var resumeAt = Math.Max(0, _playback.Time);
        StatusText.Text = $"检测到播放停滞，正在恢复（{_watchdogRecoveryCount}/3）……";
        DiagnosticLog.Write("WATCHDOG", $"{reason}; track='{track.FilePath}', time={resumeAt}");
        try
        {
            var recovered = await _playback.RecoverAsync(track.FilePath, _state, track.IsVideo, resumeAt);
            if (!string.Equals(_currentTrack?.Id, track.Id, StringComparison.OrdinalIgnoreCase))
                return;
            StatusText.Text = recovered
                ? "播放已自动恢复并从原位置继续"
                : "播放恢复失败；界面仍可操作，将再次尝试";
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
        var index = LyricsService.FindCurrentIndex(_lyrics, currentTime, _state.LyricOffsetMs);
        if (index == _currentLyricIndex)
            return;
        if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
            _lyrics[_currentLyricIndex].IsCurrent = false;
        _currentLyricIndex = index;
        if (index < 0 || index >= _lyrics.Count)
            return;
        var line = _lyrics[index];
        line.IsCurrent = true;
        LyricsList.SelectedItem = line;
        LyricsList.ScrollIntoView(line);
        if (_state.InPlayerBilingualSubtitles)
        {
            InPlayerOriginalText.Text = line.Original;
            InPlayerTranslationText.Text = line.Translation;
        }
        if (_desktopLyrics?.IsVisible == true)
            _desktopLyrics.UpdateLyrics(line.Original, line.Translation);
    }

    private void LyricsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (LyricsList.SelectedItem is not LyricLine line)
            return;
        _playback.Seek(Math.Max(0, line.TimeMs + _state.LyricOffsetMs));
        UpdateCurrentLyric(_playback.Time);
        e.Handled = true;
    }

    private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _playback.Seek((long)PositionSlider.Value);
        _seeking = false;
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seeking)
            ElapsedText.Text = FormatTime((long)e.NewValue);
    }

    private void RememberRecentlyPlayed(string trackId)
    {
        if (_recentTrackIds.Contains(trackId))
        {
            var remaining = _recentTrackIds.Where(id => id != trackId).ToArray();
            _recentTrackIds.Clear();
            foreach (var id in remaining)
                _recentTrackIds.Enqueue(id);
        }
        _recentTrackIds.Enqueue(trackId);
        while (_recentTrackIds.Count > 25)
            _recentTrackIds.Dequeue();
    }

    private void LibraryViewButton_Click(object sender, RoutedEventArgs e) => ShowPlayerView(false);

    private void PlayerViewButton_Click(object sender, RoutedEventArgs e) => ShowPlayerView(true);

    private void ShowPlayerView(bool showPlayer)
    {
        _showingPlayerView = showPlayer;
        VideoView.MediaPlayer = showPlayer ? _playback.Player : null;
        VideoView.Visibility = showPlayer ? Visibility.Visible : Visibility.Collapsed;
        PlayerPanel.Visibility = showPlayer ? Visibility.Visible : Visibility.Collapsed;
        if (showPlayer)
        {
            LibraryToolbar.Visibility = Visibility.Collapsed;
            AlbumToolbar.Visibility = Visibility.Collapsed;
            CircleToolbar.Visibility = Visibility.Collapsed;
            TrackGrid.Visibility = Visibility.Collapsed;
            AlbumPanel.Visibility = Visibility.Collapsed;
            CirclePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateLibraryContentVisibility();
        }
        LibraryViewButton.Background = showPlayer
            ? (System.Windows.Media.Brush)FindResource("AppBackgroundBrush")
            : (System.Windows.Media.Brush)FindResource("SelectionBrush");
        PlayerViewButton.Background = showPlayer
            ? (System.Windows.Media.Brush)FindResource("SelectionBrush")
            : (System.Windows.Media.Brush)FindResource("AppBackgroundBrush");
    }

    private void OpenMediaButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开音频或视频",
            Filter = "支持的媒体|*.mp3;*.flac;*.m4a;*.mp4;*.ogg;*.opus;*.wav;*.wma;*.aac;*.ape;*.mkv;*.webm;*.avi;*.mov;*.m4v;*.ts;*.mpeg;*.mpg|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var existing = _state.Tracks.FirstOrDefault(track =>
            string.Equals(track.FilePath, dialog.FileName, StringComparison.OrdinalIgnoreCase));
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
                Album = Path.GetFileName(Path.GetDirectoryName(dialog.FileName)) ?? "未知专辑",
                Format = Path.GetExtension(dialog.FileName).TrimStart('.').ToUpperInvariant(),
                IsVideo = MusicLibraryService.VideoExtensions.Contains(Path.GetExtension(dialog.FileName))
            };
        }

        _queue = [track];
        _queueIndex = 0;
        QueueList.ItemsSource = _queue;
        PlayTrack(track);
    }

    private async void VisualizationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || VisualizationCombo.SelectedItem is not ComboBoxItem { Tag: string mode } ||
            mode == _state.VisualizationMode)
            return;
        _state.VisualizationMode = mode;
        await _store.SaveAsync(_state);
        if (_currentTrack is null || _currentTrack.IsVideo)
            return;

        var position = _playback.Time;
        var wasPlaying = _playback.IsPlaying;
        _playback.Play(_currentTrack.FilePath, _state, false);
        await Task.Delay(450);
        _playback.Seek(position);
        if (!wasPlaying)
            _playback.TogglePause();
        if (mode != "Off")
            ShowPlayerView(true);
    }

    private async void PlaybackRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || PlaybackRateCombo.SelectedItem is not ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rate))
            return;
        _state.PlaybackRate = rate;
        _playback.SetRate(rate);
        await _store.SaveAsync(_state);
        PlaybackRateCombo.ToolTip = $"当前播放速度：{rate:0.##}×";
    }

    private void AudioTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingMediaControls && AudioTrackCombo.SelectedItem is MediaTrackOption track)
            _playback.SetAudioTrack(track.Id);
    }

    private void VideoTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingMediaControls && VideoTrackCombo.SelectedItem is MediaTrackOption track)
            _playback.SetVideoTrack(track.Id);
    }

    private void SubtitleTrackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingMediaControls && SubtitleTrackCombo.SelectedItem is MediaTrackOption track)
            _playback.SetSubtitleTrack(track.Id);
    }

    private async void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingMediaControls || AudioDeviceCombo.SelectedItem is not AudioDeviceOption device)
            return;
        _state.PreferredAudioDeviceId = device.Id;
        _playback.SetAudioDevice(device.Id);
        AudioDeviceCombo.ToolTip = $"当前音频输出：{device.Name}";
        await _store.SaveAsync(_state);
    }

    private void SubtitleDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SubtitleDelayText is null)
            return;
        SubtitleDelayText.Text = $"{e.NewValue:+0.00;-0.00;0.00} s";
        if (_initialized)
            _playback.SetSubtitleDelay((long)(e.NewValue * 1000));
    }

    private async Task RefreshMediaControlsAsync()
    {
        if (_currentTrack is null || _refreshingMediaControls)
            return;
        _refreshingMediaControls = true;
        try
        {
            var captureTask = Task.Run(_playback.CaptureMediaControls);
            if (await Task.WhenAny(captureTask, Task.Delay(2500)) != captureTask)
            {
                DiagnosticLog.Write("MEDIA", "Media control enumeration exceeded 2.5 seconds and was skipped");
                return;
            }

            var controls = await captureTask;
            SetTrackCombo(AudioTrackCombo, controls.AudioTracks, controls.SelectedAudioTrack);
            SetTrackCombo(VideoTrackCombo, controls.VideoTracks, controls.SelectedVideoTrack);
            SetTrackCombo(SubtitleTrackCombo, controls.SubtitleTracks, controls.SelectedSubtitleTrack);
            AudioDeviceCombo.ItemsSource = controls.AudioDevices;
            AudioDeviceCombo.SelectedItem = controls.AudioDevices.FirstOrDefault(device =>
                device.Id == _state.PreferredAudioDeviceId) ?? controls.AudioDevices[0];
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
        var lines = new List<string>();
        if (details.HasVideo)
        {
            lines.Add($"画面  {details.Width} × {details.Height}");
            lines.Add($"帧率  {details.FrameRate:0.###} FPS");
            lines.Add($"视频编码  {details.VideoCodec}  {FormatBitrate(details.VideoBitrate)}");
        }
        if (details.HasAudio)
        {
            lines.Add($"采样率  {details.SampleRate:N0} Hz");
            lines.Add($"声道  {details.Channels}");
            lines.Add($"音频编码  {details.AudioCodec}  {FormatBitrate(details.AudioBitrate)}");
        }
        MediaInfoText.Text = lines.Count == 0 ? "未读取到媒体流信息" : string.Join(Environment.NewLine, lines);
        if (_currentTrack is not null)
            PlayerMediaSummaryText.Text = $"{_currentTrack.Title}  ·  " +
                (details.HasVideo ? $"{details.Width}×{details.Height} / {details.FrameRate:0.##} FPS" :
                    $"{details.SampleRate:N0} Hz / {details.Channels} 声道");
        _ = RefreshMediaControlsAsync();
    }

    private void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack is null || (!_currentTrack.IsVideo && !_mediaDetails.HasVideo))
        {
            StatusText.Text = "当前媒体没有视频画面";
            return;
        }
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "本地音乐库截图");
        Directory.CreateDirectory(folder);
        var safeTitle = string.Concat(_currentTrack.Title.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var path = Path.Combine(folder, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        StatusText.Text = _playback.TakeSnapshot(path) ? $"截图已保存：{path}" : "截图失败：当前视频画面尚未就绪";
    }

    private static void SetTrackCombo(ComboBox comboBox, IReadOnlyList<MediaTrackOption> tracks, int selectedId)
    {
        comboBox.ItemsSource = tracks;
        comboBox.SelectedItem = tracks.FirstOrDefault(track => track.Id == selectedId) ?? tracks.FirstOrDefault();
        comboBox.IsEnabled = tracks.Count > 1;
    }

    private static void SelectComboByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (comboBox.SelectedIndex < 0)
            comboBox.SelectedIndex = 0;
    }

    private static string FormatBitrate(int bitrate) => bitrate <= 0 ? "" : $"{bitrate / 1000:N0} kb/s";

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized)
            return;
        _playback.Volume = (int)e.NewValue;
        _state.Volume = (int)e.NewValue;
    }

    private void DesktopLyricsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _shuttingDown)
            return;
        var enabled = DesktopLyricsCheckBox.IsChecked == true;
        _state.DesktopLyricsEnabled = enabled;
        if (enabled)
        {
            if (_desktopLyrics is null)
            {
                _desktopLyrics = CreateDesktopLyricsWindow();
            }
            if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
            {
                var line = _lyrics[_currentLyricIndex];
                _desktopLyrics.UpdateLyrics(line.Original, line.Translation);
            }
            else if (_currentTrack is not null)
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
        _ = _store.SaveAsync(_state);
    }

    private DesktopLyricsWindow CreateDesktopLyricsWindow()
    {
        var window = new DesktopLyricsWindow(_state) { Owner = this };
        window.Dismissed += (_, _) => DesktopLyricsCheckBox.IsChecked = false;
        window.ActivateMainRequested += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Show();
            Activate();
        };
        window.PreviousRequested += (_, _) => GoPrevious();
        window.PlayPauseRequested += (_, _) => TogglePlayback();
        window.NextRequested += (_, _) => GoNext();
        window.SettingsRequested += (_, _) => ShowSettings();
        window.OffsetChangeRequested += ChangeLyricOffset;
        window.LockChanged += locked =>
        {
            _state.DesktopLyricsLocked = locked;
            _ = _store.SaveAsync(_state);
        };
        window.PositionChangedByUser += (_, _) =>
        {
            _state.DesktopLyricsLeft = window.Left;
            _state.DesktopLyricsTop = window.Top;
        };
        return window;
    }

    private void ChangeLyricOffset(int deltaMilliseconds)
    {
        if (_currentLyricIndex >= 0 && _currentLyricIndex < _lyrics.Count)
            _lyrics[_currentLyricIndex].IsCurrent = false;
        _currentLyricIndex = -1;
        _state.LyricOffsetMs = Math.Clamp(_state.LyricOffsetMs + deltaMilliseconds, -5000, 5000);
        _desktopLyrics?.UpdateOffset(_state.LyricOffsetMs);
        UpdateCurrentLyric(_playback.Time);
        StatusText.Text = _state.LyricOffsetMs == 0
            ? "歌词时间偏移已重置"
            : $"歌词时间偏移：{_state.LyricOffsetMs / 1000d:+0.0;-0.0} 秒";
        _ = _store.SaveAsync(_state);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private void ApplyUiFont()
    {
        try
        {
            FontFamily = new System.Windows.Media.FontFamily(
                string.IsNullOrWhiteSpace(_state.UiFontFamily) ? "Microsoft YaHei UI" : _state.UiFontFamily);
        }
        catch
        {
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        }
    }

    private void ConfigureTrayIcon()
    {
        if (_trayIcon is null)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var showItem = new System.Windows.Forms.ToolStripMenuItem("打开本地音乐库");
            showItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
            var playPauseItem = new System.Windows.Forms.ToolStripMenuItem("播放 / 暂停");
            playPauseItem.Click += (_, _) => Dispatcher.Invoke(TogglePlayback);
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
            exitItem.Click += (_, _) => Dispatcher.Invoke(() =>
            {
                _forceExit = true;
                Close();
            });
            menu.Items.Add(showItem);
            menu.Items.Add(playPauseItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            System.Drawing.Icon icon;
            try
            {
                icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
                    ?? System.Drawing.SystemIcons.Application;
            }
            catch
            {
                icon = System.Drawing.SystemIcons.Application;
            }

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "本地音乐库",
                Icon = icon,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
        _trayIcon.Visible = string.Equals(_state.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase);
    }

    private void HideToTray()
    {
        ConfigureTrayIcon();
        if (_trayIcon is not null)
            _trayIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ApplyStartupWindowState()
    {
        if (!_state.StartMinimized)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (string.Equals(_state.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase))
                HideToTray();
            else
                WindowState = WindowState.Minimized;
        });
    }

    private void ConfigureAutoCloseTimer()
    {
        _autoCloseTimer.Stop();
        _autoCloseAt = null;
        if (!_state.AutoCloseEnabled || _state.AutoCloseMinutes <= 0)
            return;
        _autoCloseAt = DateTime.Now.AddMinutes(_state.AutoCloseMinutes);
        _autoCloseTimer.Start();
    }

    private void AutoCloseTimer_Tick(object? sender, EventArgs e)
    {
        if (_autoCloseAt is null || DateTime.Now < _autoCloseAt.Value)
            return;
        _autoCloseTimer.Stop();
        _forceExit = true;
        Close();
    }

    private void ApplyStartupPlayback()
    {
        if (_startupPlaybackApplied || !_state.AutoPlayOnStartup)
            return;
        _startupPlaybackApplied = true;
        var track = _state.Tracks.FirstOrDefault(item =>
                        string.Equals(item.Id, _state.LastTrackId, StringComparison.OrdinalIgnoreCase) && !item.IsEncryptedNcm)
                    ?? _state.Tracks.FirstOrDefault(item => !item.IsEncryptedNcm);
        if (track is null)
            return;
        _pendingSeekMs = _state.RememberPlaybackProgress &&
                         string.Equals(track.Id, _state.LastTrackId, StringComparison.OrdinalIgnoreCase)
            ? Math.Max(0, _state.LastPlaybackPositionMs)
            : null;
        if (_visibleTracks.Any(item => string.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase)))
            PlayFromVisibleTracks(track);
        else
            PlayTracks([track]);
    }

    private void RememberCurrentPlayback()
    {
        if (_currentTrack is null)
            return;
        _state.LastTrackId = _currentTrack.Id;
        _state.LastPlaybackPositionMs = _state.RememberPlaybackProgress ? Math.Max(0, _playback.Time) : 0;
    }

    private void ShowSettings() => _ = ShowSettingsAsync();

    private async Task ShowSettingsAsync()
    {
        var dialog = new SettingsWindow(_state) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var previousAudioBackend = _state.AudioBackend;
        dialog.ApplyTo(_state);
        ApplyUiFont();
        try
        {
            StartupRegistrationService.Apply(_state.RunAtStartup);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法更新开机启动设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        VolumeSlider.Value = _state.Volume;
        _playback.Volume = _state.Volume;
        SelectComboByTag(VisualizationCombo, _state.VisualizationMode);
        SelectComboByTag(PlaybackRateCombo, _state.PlaybackRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        InPlayerSubtitlePanel.Visibility = _state.InPlayerBilingualSubtitles ? Visibility.Visible : Visibility.Collapsed;
        _desktopLyrics?.ApplySettings(_state);
        if (_desktopLyrics is not null && _state.DesktopLyricsLeft is null && _state.DesktopLyricsTop is null)
            _desktopLyrics.ResetPosition();
        ConfigureHotkeys();
        ConfigureTrayIcon();
        ConfigureAutoCloseTimer();
        await _store.SaveAsync(_state);
        if (_currentTrack is not null && _playback.IsPlaying &&
            !string.Equals(previousAudioBackend, _state.AudioBackend, StringComparison.OrdinalIgnoreCase))
        {
            _watchdogRecoveryCount = 0;
            await RecoverPlaybackAsync("音频输出后端已更改");
        }
        if (dialog.RescanRequested)
            await ScanLibraryAsync();
        else
            StatusText.Text = "设置已保存";
    }

    private void ConfigureHotkeys()
    {
        _hotkeys.Configure(this, _state.GlobalHotkeysEnabled, _state.SystemMediaKeysEnabled);
        if (_hotkeys.FailedRegistrations > 0)
            StatusText.Text = $"{_hotkeys.FailedRegistrations} 个全局快捷键已被其它程序占用，本窗口快捷键仍可使用";
    }

    private void Window_HandledKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        var action = e.Key switch
        {
            Key.P => HotkeyAction.PlayPause,
            Key.Left => HotkeyAction.Previous,
            Key.Right => HotkeyAction.Next,
            Key.Up => HotkeyAction.VolumeUp,
            Key.Down => HotkeyAction.VolumeDown,
            Key.D => HotkeyAction.ToggleLyrics,
            Key.L => HotkeyAction.ToggleFavorite,
            Key.M => HotkeyAction.ToggleMiniMode,
            _ => (HotkeyAction?)null
        };
        if (action is null)
            return;
        HandleHotkey(action.Value);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && e.OriginalSource is TextBox or ComboBox)
            return;
        HotkeyAction? action = null;
        if (modifiers == ModifierKeys.None)
        {
            action = e.Key switch
            {
                Key.Space => HotkeyAction.PlayPause,
                Key.MediaPlayPause => HotkeyAction.PlayPause,
                Key.MediaPreviousTrack => HotkeyAction.Previous,
                Key.MediaNextTrack => HotkeyAction.Next,
                _ => null
            };
        }

        if (action is null)
            return;
        HandleHotkey(action.Value);
        e.Handled = true;
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
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                break;
            case HotkeyAction.VolumeDown:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
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
        var track = _currentTrack ?? TrackGrid.SelectedItem as TrackModel;
        if (track is null)
            return;
        track.IsFavorite = !track.IsFavorite;
        await _store.SaveAsync(_state);
        TrackGrid.Items.Refresh();
        StatusText.Text = track.IsFavorite ? $"已收藏“{track.Title}”" : $"已取消收藏“{track.Title}”";
    }

    private void ToggleMiniMode()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            RestoreFromTray();
            return;
        }
        if (string.Equals(_state.CloseBehavior, "MinimizeToTray", StringComparison.OrdinalIgnoreCase))
            HideToTray();
        else
            WindowState = WindowState.Minimized;
    }

    private void OpenCurrentFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack is null || !File.Exists(_currentTrack.FilePath))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentTrack.FilePath}\"") { UseShellExecute = true });
    }

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private enum LibraryView
    {
        All,
        Favorites,
        Recent,
        History,
        Category,
        Albums,
        Album,
        FavoriteAlbums,
        Circles,
        Circle,
        Playlist
    }
}
