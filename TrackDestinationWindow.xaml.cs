using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace OfflineMusicLibrary;

public partial class TrackDestinationWindow : Window
{
    private readonly ObservableCollection<PlaylistDestinationOption> _options;
    private readonly ICollectionView _view;

    public TrackDestinationWindow(IReadOnlyCollection<PlaylistModel> playlists, int selectedTrackCount)
    {
        InitializeComponent();
        SelectionSummaryText.Text = $"已选择 {selectedTrackCount:N0} 首歌曲；可以同时加入多个去向";
        _options = new ObservableCollection<PlaylistDestinationOption>(
            playlists.OrderByDescending(playlist => playlist.UpdatedAt)
                .ThenBy(playlist => playlist.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(playlist => new PlaylistDestinationOption(playlist)));
        PlaylistOptionsList.ItemsSource = _options;
        _view = CollectionViewSource.GetDefaultView(_options);
        _view.Filter = FilterPlaylist;
        EmptyPlaylistText.Visibility = _options.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => PlaylistSearchBox.Focus();
    }

    public bool PlayNext => PlayNextCheckBox.IsChecked == true;
    public bool AppendToQueue => AppendQueueCheckBox.IsChecked == true;
    public string NewPlaylistName => NewPlaylistNameBox.Text.Trim();
    public IReadOnlyList<string> SelectedPlaylistIds => _options
        .Where(option => option.IsSelected)
        .Select(option => option.Playlist.Id)
        .ToList();

    private void PlaylistSearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private bool FilterPlaylist(object item)
    {
        if (item is not PlaylistDestinationOption option)
            return false;
        var query = PlaylistSearchBox.Text.Trim();
        return query.Length == 0 || option.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PlayNext && !AppendToQueue && SelectedPlaylistIds.Count == 0 && NewPlaylistName.Length == 0)
        {
            ValidationText.Text = "请至少选择一个歌单或播放队列操作。";
            return;
        }
        DialogResult = true;
    }
}

public sealed class PlaylistDestinationOption(PlaylistModel playlist)
{
    public PlaylistModel Playlist { get; } = playlist;
    public bool IsSelected { get; set; }
    public string SearchText => $"{Playlist.Name} {Playlist.Description} {string.Join(' ', Playlist.Tags)}";
}
