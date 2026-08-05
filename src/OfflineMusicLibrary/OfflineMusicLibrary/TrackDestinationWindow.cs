using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace OfflineMusicLibrary;

public partial class TrackDestinationWindow : Window, IComponentConnector
{
	private readonly ObservableCollection<PlaylistDestinationOption> _options;

	private readonly ICollectionView _view;

	public bool PlayNext => PlayNextCheckBox.IsChecked == true;

	public bool AppendToQueue => AppendQueueCheckBox.IsChecked == true;

	public string NewPlaylistName => NewPlaylistNameBox.Text.Trim();

	public IReadOnlyList<string> SelectedPlaylistIds => (from option in _options
		where option.IsSelected
		select option.Playlist.Id).ToList();

	public TrackDestinationWindow(IReadOnlyCollection<PlaylistModel> playlists, int selectedTrackCount)
	{
		InitializeComponent();
		SelectionSummaryText.Text = $"已选择 {selectedTrackCount:N0} 首歌曲；可以同时加入多个去向";
		_options = new ObservableCollection<PlaylistDestinationOption>(from playlist in playlists.OrderByDescending((PlaylistModel playlist) => playlist.UpdatedAt).ThenBy<PlaylistModel, string>((PlaylistModel playlist) => playlist.Name, StringComparer.CurrentCultureIgnoreCase)
			select new PlaylistDestinationOption(playlist));
		PlaylistOptionsList.ItemsSource = _options;
		_view = CollectionViewSource.GetDefaultView(_options);
		_view.Filter = FilterPlaylist;
		EmptyPlaylistText.Visibility = ((_options.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		base.Loaded += delegate
		{
			PlaylistSearchBox.Focus();
		};
	}

	private void PlaylistSearchBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		_view.Refresh();
	}

	private bool FilterPlaylist(object item)
	{
		if (!(item is PlaylistDestinationOption option))
		{
			return false;
		}
		string query = PlaylistSearchBox.Text.Trim();
		if (query.Length != 0)
		{
			return option.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
		}
		return true;
	}

	private void ConfirmButton_Click(object sender, RoutedEventArgs e)
	{
		if (!PlayNext && !AppendToQueue && SelectedPlaylistIds.Count == 0 && NewPlaylistName.Length == 0)
		{
			ValidationText.Text = "请至少选择一个歌单或播放队列操作。";
		}
		else
		{
			base.DialogResult = true;
		}
	}
}
