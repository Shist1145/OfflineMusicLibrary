using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace OfflineMusicLibrary;

public sealed partial class MainWindow : Window
{
    private readonly AppStore _store = new();
    private readonly MusicLibraryService _libraryService = new();
    private readonly NetEasePlaylistService _netEaseService = new();
    private readonly PlaybackService _playback = new();
    private readonly ObservableCollection<TrackModel> _libraryItems = [];
    private readonly ObservableCollection<TrackModel> _playlistItems = [];
    private readonly ObservableCollection<TrackModel> _albumTrackItems = [];
    private readonly ObservableCollection<TrackModel> _circleTrackItems = [];
    private readonly ObservableCollection<TrackModel> _recommendationItems = [];
    private readonly ObservableCollection<AlbumCard> _albumItems = [];
    private readonly ObservableCollection<CircleCard> _circleItems = [];
    private readonly Dictionary<string, Bitmap> _coverCache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private DataGrid _libraryGrid = null!;
    private DataGrid _playlistGrid = null!;
    private DataGrid _albumTracksGrid = null!;
    private DataGrid _circleTracksGrid = null!;
    private DataGrid _recommendationGrid = null!;
    private ListBox _playlistList = null!;
    private ListBox _albumList = null!;
    private ListBox _circleList = null!;
    private TabControl _mainTabs = null!;
    private TextBox _searchBox = null!;
    private TextBlock _trackCountText = null!;
    private TextBlock _nowPlayingText = null!;
    private TextBlock _positionText = null!;
    private TextBlock _statusText = null!;
    private TextBlock _recommendationTasteText = null!;
    private TextBlock _recommendationInsightText = null!;
    private Slider _positionSlider = null!;
    private Slider _volumeSlider = null!;
    private AppState _state = new();
    private TrackModel? _currentTrack;
    private List<TrackModel> _currentQueue = [];
    private bool _isBusy;
    private bool _isLoaded;
    private bool _updatingPosition;
    private int _recommendationRefreshSalt;
    private RecommendationPreset _activeRecommendationPreset = RecommendationPreset.RediscoverFavorites;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _libraryGrid = RequireControl<DataGrid>("LibraryGrid");
        _playlistGrid = RequireControl<DataGrid>("PlaylistGrid");
        _albumTracksGrid = RequireControl<DataGrid>("AlbumTracksGrid");
        _circleTracksGrid = RequireControl<DataGrid>("CircleTracksGrid");
        _recommendationGrid = RequireControl<DataGrid>("RecommendationGrid");
        _playlistList = RequireControl<ListBox>("PlaylistList");
        _albumList = RequireControl<ListBox>("AlbumList");
        _circleList = RequireControl<ListBox>("CircleList");
        _mainTabs = RequireControl<TabControl>("MainTabs");
        _searchBox = RequireControl<TextBox>("SearchBox");
        _trackCountText = RequireControl<TextBlock>("TrackCountText");
        _nowPlayingText = RequireControl<TextBlock>("NowPlayingText");
        _positionText = RequireControl<TextBlock>("PositionText");
        _statusText = RequireControl<TextBlock>("StatusText");
        _recommendationTasteText = RequireControl<TextBlock>("RecommendationTasteText");
        _recommendationInsightText = RequireControl<TextBlock>("RecommendationInsightText");
        _positionSlider = RequireControl<Slider>("PositionSlider");
        _volumeSlider = RequireControl<Slider>("VolumeSlider");

        _libraryGrid.ItemsSource = _libraryItems;
        _playlistGrid.ItemsSource = _playlistItems;
        _albumTracksGrid.ItemsSource = _albumTrackItems;
        _circleTracksGrid.ItemsSource = _circleTrackItems;
        _recommendationGrid.ItemsSource = _recommendationItems;
        _albumList.ItemsSource = _albumItems;
        _circleList.ItemsSource = _circleItems;

        _playback.PositionChanged += (time, length) =>
            Dispatcher.UIThread.Post(() => UpdatePlaybackPosition(time, length));
        _playback.Ended += () => Dispatcher.UIThread.Post(PlayNext);
        _playback.Failed += message => Dispatcher.UIThread.Post(() => _statusText.Text = message);
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _playback.Dispose();
            foreach (var bitmap in _coverCache.Values.Distinct())
                bitmap.Dispose();
        };
    }

    private T RequireControl<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException($"界面控件未加载：{name}");

    private async Task LoadAsync()
    {
        _state = await _store.LoadAsync();
        _isLoaded = true;
        _volumeSlider.Value = _state.Volume;
        RefreshAllViews();
        _statusText.Text = _state.LibraryFolders.Count == 0
            ? "先添加音乐文件夹；FLAC、MP3、NCM 等文件都会递归收录。"
            : $"已加载 {_state.Tracks.Count:N0} 首本地歌曲";
        _ = LoadCoversAsync(_state.Tracks);
    }

    private async void AddFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择音乐文件夹",
            AllowMultiple = true
        });
        var changed = false;
        foreach (var folder in folders)
        {
            var path = folder.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path) ||
                _state.LibraryFolders.Contains(path, PathComparer))
                continue;
            _state.LibraryFolders.Add(path);
            changed = true;
        }
        if (!changed)
            return;
        await _store.SaveAsync(_state);
        await ScanLibraryAsync();
    }

    private async void RescanButton_Click(object? sender, RoutedEventArgs e) => await ScanLibraryAsync();

    private async Task ScanLibraryAsync()
    {
        if (_isBusy)
            return;
        if (_state.LibraryFolders.Count == 0)
        {
            _statusText.Text = "尚未添加音乐文件夹。";
            return;
        }

        _isBusy = true;
        var progress = new Progress<ScanProgress>(value =>
        {
            _statusText.Text = $"正在扫描 {value.Scanned:N0} / {value.Total:N0}，元数据回退 {value.MetadataFallbacks:N0}";
            _trackCountText.Text = Path.GetFileName(value.CurrentFile);
        });
        try
        {
            _state.Tracks = await _libraryService.ScanAsync(_state.LibraryFolders, _state.Tracks, progress);
            await _store.SaveAsync(_state);
            RefreshAllViews();
            _statusText.Text = $"扫描完成：{_state.Tracks.Count:N0} 个媒体文件均已收录";
            _ = LoadCoversAsync(_state.Tracks);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("LibraryScan", "曲库扫描失败。", exception);
            await ShowMessageAsync("扫描失败", exception.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void ImportNetEaseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        var prompt = new TextPromptWindow(
            "导入网易云歌单",
            "粘贴公开歌单链接或歌单 ID",
            "程序会先重新扫描所有音乐文件，再按完整歌曲 ID、歌名、艺术家和专辑匹配本地文件。");
        var source = await prompt.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(source))
            return;

        _isBusy = true;
        try
        {
            if (_state.LibraryFolders.Count > 0)
            {
                var progress = new Progress<ScanProgress>(value =>
                    _statusText.Text = $"导入前同步本地文件 {value.Scanned:N0} / {value.Total:N0}");
                _state.Tracks = await _libraryService.ScanAsync(_state.LibraryFolders, _state.Tracks, progress);
            }

            _statusText.Text = "正在读取完整网易云歌单并匹配本地文件…";
            var result = await _netEaseService.ImportAsync(source, _state.Tracks);
            var playlist = _state.Playlists.FirstOrDefault(item => item.CloudPlaylistId == result.PlaylistId);
            if (playlist is null)
            {
                playlist = new PlaylistModel
                {
                    Name = MakeUniquePlaylistName(result.PlaylistName),
                    Source = "netease",
                    CloudPlaylistId = result.PlaylistId
                };
                _state.Playlists.Add(playlist);
            }
            else
            {
                playlist.Name = result.PlaylistName;
            }

            var imported = result.Matched.Select(track => track.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!result.HasCompleteRemoteDetails && playlist.TrackIds.Count > 0)
                imported = imported.Concat(playlist.TrackIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            playlist.TrackIds = imported;
            playlist.UpdatedAt = DateTime.Now;
            await _store.SaveAsync(_state);
            RefreshAllViews();
            _playlistList.SelectedItem = playlist;

            var missing = string.Join(Environment.NewLine, result.Missing.Take(12).Select(track =>
                string.IsNullOrWhiteSpace(track.Title)
                    ? $"• 网易云歌曲 ID {track.Id}（详情暂未返回）"
                    : $"• {track.Title} - {track.Artist}"));
            var message = $"歌单：{result.PlaylistName}{Environment.NewLine}" +
                          $"网易云歌曲：{result.DeclaredTrackCount}{Environment.NewLine}" +
                          $"完整歌曲 ID：{result.TrackIdCount}{Environment.NewLine}" +
                          $"已读取详情：{result.ResolvedTrackCount}{Environment.NewLine}{Environment.NewLine}" +
                          $"云 ID 精确匹配：{result.ExactMatchCount}{Environment.NewLine}" +
                          $"名称/艺术家/专辑匹配：{result.FuzzyMatchCount}{Environment.NewLine}" +
                          $"导入本地文件：{result.Matched.Count}{Environment.NewLine}" +
                          $"确实缺少或未匹配：{result.Missing.Count}";
            if (missing.Length > 0)
                message += $"{Environment.NewLine}{Environment.NewLine}未匹配：{Environment.NewLine}{missing}";
            await ShowMessageAsync("导入完成", message);
            _statusText.Text = $"已导入“{result.PlaylistName}”：{result.Matched.Count} / {result.DeclaredTrackCount} 首";
            _ = LoadCoversAsync(_state.Tracks);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("NetEaseImport", "歌单导入失败。", exception);
            await ShowMessageAsync("导入失败", exception.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void ImportNetEaseHistoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入网易云播放历史",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("网易云播放历史") { Patterns = ["*.json", "*.csv", "*.tsv", "*.txt"] },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count == 0)
            return;
        var filePath = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            await ShowMessageAsync("无法读取文件", "请选择保存在本机磁盘上的 JSON、CSV、TSV 或 TXT 文件。");
            return;
        }

        _isBusy = true;
        try
        {
            _statusText.Text = "正在匹配网易云播放历史…";
            var result = await NetEaseHistoryImportService.ImportAsync(filePath, _state.Tracks);
            await _store.SaveAsync(_state);
            RefreshAllViews();

            var unmatched = string.Join(Environment.NewLine, result.Unmatched.Take(10).Select(item =>
                $"• {item.Title} - {(string.IsNullOrWhiteSpace(item.Artist) ? "未知歌手" : item.Artist)}"));
            var message = $"读取历史记录：{result.SourceRecordCount:N0}{Environment.NewLine}" +
                          $"匹配记录：{result.MatchedRecordCount:N0}{Environment.NewLine}" +
                          $"更新本地歌曲：{result.MatchedTrackCount:N0}{Environment.NewLine}" +
                          $"网易云 ID 精确匹配：{result.ExactMatchCount:N0}{Environment.NewLine}" +
                          $"歌名 / 艺人保守匹配：{result.FuzzyMatchCount:N0}{Environment.NewLine}" +
                          $"新增可信播放次数：{result.PlayCountIncrease:N0}{Environment.NewLine}" +
                          $"更新最近播放时间：{result.LastPlayedUpdateCount:N0}{Environment.NewLine}" +
                          $"未匹配：{result.Unmatched.Count:N0}（含歧义 {result.AmbiguousCount:N0}）";
            if (unmatched.Length > 0)
                message += $"{Environment.NewLine}{Environment.NewLine}部分未匹配记录：{Environment.NewLine}{unmatched}";
            message += $"{Environment.NewLine}{Environment.NewLine}播放历史已立即用于安心发现；重复导入不会重复累加。";
            await ShowMessageAsync("播放历史导入完成", message);
            _statusText.Text = $"已导入网易云历史：匹配 {result.MatchedRecordCount:N0} / {result.SourceRecordCount:N0} 条";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("NetEaseHistoryImport", "播放历史导入失败。", exception);
            await ShowMessageAsync("播放历史导入失败", exception.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e) => RefreshAllViews();

    private void MainTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
            return;
        if (_mainTabs.SelectedIndex == 4)
            OpenRecommendation(_activeRecommendationPreset);
        UpdateCurrentQueue();
    }

    private void PlaylistList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
            return;
        RefreshPlaylistTracks();
        UpdateCurrentQueue();
    }

    private void AlbumList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
            return;
        var selected = _albumList.SelectedItem as AlbumCard;
        Replace(_albumTrackItems, selected is null
            ? []
            : FilterTracks(_state.Tracks.Where(track => track.AlbumKey == selected.Key)));
        UpdateCurrentQueue();
    }

    private void CircleList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded)
            return;
        var selected = _circleList.SelectedItem as CircleCard;
        Replace(_circleTrackItems, selected is null
            ? []
            : FilterTracks(_state.Tracks.Where(track =>
                string.Equals(track.CircleText, selected.Name, StringComparison.CurrentCultureIgnoreCase))));
        UpdateCurrentQueue();
    }

    private async void FavoriteTrackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TrackModel track })
            return;
        track.IsFavorite = !track.IsFavorite;
        await _store.SaveAsync(_state);
        OpenRecommendation(_activeRecommendationPreset);
        e.Handled = true;
    }

    private void PlayTrackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TrackModel track })
            PlayTrack(track);
        e.Handled = true;
    }

    private void TrackGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: TrackModel track })
            PlayTrack(track);
    }

    private void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentTrack is null)
        {
            var first = _currentQueue.FirstOrDefault() ?? _state.Tracks.FirstOrDefault();
            if (first is not null)
                PlayTrack(first);
            return;
        }
        try
        {
            _playback.TogglePause();
        }
        catch (Exception exception)
        {
            _ = ShowMessageAsync("无法播放", exception.Message);
        }
    }

    private void PreviousButton_Click(object? sender, RoutedEventArgs e) => PlayAdjacent(-1);
    private void NextButton_Click(object? sender, RoutedEventArgs e) => PlayNext();
    private void PlayNext() => PlayAdjacent(1);

    private void PlayAdjacent(int offset)
    {
        if (_currentQueue.Count == 0)
            UpdateCurrentQueue();
        if (_currentQueue.Count == 0)
            return;
        var index = _currentTrack is null ? -1 : _currentQueue.FindIndex(track => track.Id == _currentTrack.Id);
        var next = (index + offset + _currentQueue.Count) % _currentQueue.Count;
        PlayTrack(_currentQueue[next]);
    }

    private void PlayTrack(TrackModel track)
    {
        try
        {
            UpdateCurrentQueue();
            if (!_currentQueue.Any(item => item.Id == track.Id))
                _currentQueue = _state.Tracks.ToList();
            _playback.Volume = _state.Volume;
            _playback.Play(track.FilePath);
            _currentTrack = track;
            track.PlayCount++;
            track.LastPlayedAt = DateTime.Now;
            _nowPlayingText.Text = $"{track.Title}  —  {track.Artist}";
            _statusText.Text = $"正在播放：{track.Title}";
            _ = _store.SaveAsync(_state);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("Playback", $"无法播放：{track.FilePath}", exception);
            _ = ShowMessageAsync("无法播放", exception.Message);
        }
    }

    private void PositionSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_updatingPosition)
            _playback.Seek((long)_positionSlider.Value);
    }

    private void VolumeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_isLoaded)
            return;
        _state.Volume = (int)Math.Round(e.NewValue);
        _playback.Volume = _state.Volume;
        _ = _store.SaveAsync(_state);
    }

    private void UpdatePlaybackPosition(long time, long length)
    {
        _updatingPosition = true;
        _positionSlider.Maximum = Math.Max(1, length);
        _positionSlider.Value = Math.Clamp(time, 0, Math.Max(1, length));
        _positionText.Text = $"{FormatTime(time)} / {FormatTime(length)}";
        _updatingPosition = false;
    }

    private static string FormatTime(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(milliseconds >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");

    private void RefreshAllViews()
    {
        var selectedPlaylistId = (_playlistList.SelectedItem as PlaylistModel)?.Id;
        var selectedAlbumKey = (_albumList.SelectedItem as AlbumCard)?.Key;
        var selectedCircle = (_circleList.SelectedItem as CircleCard)?.Name;
        var filtered = FilterTracks(_state.Tracks).ToList();
        Replace(_libraryItems, filtered);

        _playlistList.ItemsSource = _state.Playlists;
        if (selectedPlaylistId is not null)
            _playlistList.SelectedItem = _state.Playlists.FirstOrDefault(item => item.Id == selectedPlaylistId);
        RefreshPlaylistTracks();

        var albums = _state.Tracks
            .GroupBy(track => track.AlbumKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var representative = group.OrderBy(track => track.TrackNumber == 0 ? uint.MaxValue : track.TrackNumber).First();
                var artists = group.Select(track => track.AlbumArtist)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .Take(3)
                    .ToList();
                var circles = group.Select(track => track.Circle)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase);
                return new AlbumCard(group.Key, representative.Album,
                    artists.Count > 0 ? string.Join(" / ", artists) : representative.Artist,
                    string.Join(" / ", circles), group.Count(), representative);
            })
            .Where(album => MatchesSearch($"{album.Title} {album.Artist} {album.Circles}"))
            .OrderBy(album => album.Title, StringComparer.CurrentCultureIgnoreCase);
        Replace(_albumItems, albums);
        if (selectedAlbumKey is not null)
            _albumList.SelectedItem = _albumItems.FirstOrDefault(item => item.Key == selectedAlbumKey);
        if (_albumList.SelectedItem is null && _albumItems.Count > 0)
            _albumList.SelectedIndex = 0;

        var circlesList = _state.Tracks
            .GroupBy(track => track.CircleText, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new CircleCard(group.Key, group.Count()))
            .Where(circle => MatchesSearch(circle.Name))
            .OrderByDescending(circle => circle.TrackCount)
            .ThenBy(circle => circle.Name, StringComparer.CurrentCultureIgnoreCase);
        Replace(_circleItems, circlesList);
        if (selectedCircle is not null)
            _circleList.SelectedItem = _circleItems.FirstOrDefault(item => item.Name == selectedCircle);
        if (_circleList.SelectedItem is null && _circleItems.Count > 0)
            _circleList.SelectedIndex = 0;

        _trackCountText.Text = $"{filtered.Count:N0} / {_state.Tracks.Count:N0} 首";
        RefreshRecommendationOverview();
        UpdateCurrentQueue();
    }

    private void RefreshPlaylistTracks()
    {
        if (_playlistList.SelectedItem is not PlaylistModel playlist)
        {
            Replace(_playlistItems, []);
            return;
        }
        var ids = playlist.TrackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Replace(_playlistItems, FilterTracks(_state.Tracks.Where(track => ids.Contains(track.Id))));
    }

    private IEnumerable<TrackModel> FilterTracks(IEnumerable<TrackModel> tracks) =>
        tracks.Where(track => MatchesSearch(track.SearchText));

    private bool MatchesSearch(string value)
    {
        var query = _searchBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(query) || value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateCurrentQueue()
    {
        _currentQueue = _mainTabs.SelectedIndex switch
        {
            1 => _playlistItems.ToList(),
            2 => _albumTrackItems.ToList(),
            3 => _circleTrackItems.ToList(),
            4 => _recommendationItems.ToList(),
            _ => _libraryItems.ToList()
        };
    }

    private void RefreshRecommendationOverview()
    {
        var implicitLikes = GetImplicitFavoriteTrackIds();
        _recommendationTasteText.Text = RecommendationService.DescribeTaste(_state.Tracks, implicitLikes);
        OpenRecommendation(_activeRecommendationPreset);
    }

    private void OpenRecommendation(RecommendationPreset preset)
    {
        _activeRecommendationPreset = preset;
        var result = RecommendationService.Create(
            _state.Tracks,
            preset,
            DateTime.Now,
            _recommendationRefreshSalt,
            implicitFavoriteTrackIds: GetImplicitFavoriteTrackIds());
        foreach (var track in result.Tracks)
            track.RecommendationReason = result.ReasonFor(track);
        Replace(_recommendationItems, FilterTracks(result.Tracks));
        _recommendationInsightText.Text = $"{result.Title} · {result.Insight}";
        if (_mainTabs.SelectedIndex == 4)
            _trackCountText.Text = $"{_recommendationItems.Count:N0} 首";
    }

    private void RecommendationPresetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: not null } button &&
            Enum.TryParse<RecommendationPreset>(button.Tag.ToString(), true, out var preset))
        {
            OpenRecommendation(preset);
            UpdateCurrentQueue();
        }
    }

    private void RefreshRecommendationButton_Click(object? sender, RoutedEventArgs e)
    {
        _recommendationRefreshSalt++;
        OpenRecommendation(_activeRecommendationPreset);
        UpdateCurrentQueue();
    }

    private void PlayRecommendationButton_Click(object? sender, RoutedEventArgs e)
    {
        UpdateCurrentQueue();
        var first = _recommendationItems.FirstOrDefault();
        if (first is null)
        {
            _statusText.Text = "当前预设还没有足够可靠的候选。";
            return;
        }
        PlayTrack(first);
    }

    private HashSet<string> GetImplicitFavoriteTrackIds() =>
        _state.Playlists
            .Where(playlist => IsFavoritePlaylistName(playlist.Name))
            .SelectMany(playlist => playlist.TrackIds ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsFavoritePlaylistName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var value = name.Trim();
        return value.Contains("喜欢的音乐", StringComparison.CurrentCultureIgnoreCase) ||
               value.Equals("我喜欢", StringComparison.CurrentCultureIgnoreCase) ||
               value.Contains("liked songs", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("favorites", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("favourites", StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoadCoversAsync(IEnumerable<TrackModel> tracks)
    {
        var groups = tracks
            .Where(track => !string.IsNullOrWhiteSpace(track.ArtworkPath) && File.Exists(track.ArtworkPath))
            .GroupBy(track => track.ArtworkPath, PathComparer)
            .ToList();
        using var gate = new SemaphoreSlim(4, 4);
        await Task.WhenAll(groups.Select(async group =>
        {
            await gate.WaitAsync();
            try
            {
                Bitmap? bitmap;
                lock (_coverCache)
                    _coverCache.TryGetValue(group.Key, out bitmap);
                if (bitmap is null)
                {
                    bitmap = await Task.Run(() =>
                    {
                        using var stream = File.OpenRead(group.Key);
                        return Bitmap.DecodeToWidth(stream, 180);
                    });
                    lock (_coverCache)
                        _coverCache[group.Key] = bitmap;
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var track in group)
                        track.Cover = bitmap;
                });
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("Artwork", $"封面读取失败：{group.Key}", exception);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private async Task ShowMessageAsync(string title, string message) =>
        await new MessageWindow(title, message).ShowDialog(this);

    private string MakeUniquePlaylistName(string name)
    {
        var candidate = name;
        var suffix = 2;
        while (_state.Playlists.Any(item => string.Equals(item.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
            candidate = $"{name} ({suffix++})";
        return candidate;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
