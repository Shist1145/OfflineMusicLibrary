using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace OfflineMusicLibrary;

public partial class PreviewPlayerWindow : Window, IComponentConnector
{
	private const double CompactHeight = 88.0;
	private const double ExpandedHeight = 356.0;
	private bool _positioned;
	private bool _expanded;
	private bool _closeRestoresMain;
	private string _queueIdentity = "";

	public event EventHandler? ActivateMainRequested;

	public event EventHandler? PreviousRequested;

	public event EventHandler? PlayPauseRequested;

	public event EventHandler? NextRequested;

	public event EventHandler? FavoriteRequested;

	public event EventHandler? MuteRequested;

	public event EventHandler? Dismissed;

	public event Action<int>? VolumeDeltaRequested;

	public event Action<TrackModel>? QueueTrackRequested;

	public PreviewPlayerWindow()
	{
		InitializeComponent();
	}

	public void UpdateTrack(TrackModel? track, ImageSource? cover)
	{
		PreviewTitleText.Text = track?.Title ?? "尚未播放";
		PreviewArtistText.Text = track?.Artist ?? "本地音乐库";
		PreviewCoverImage.Source = cover;
		FallbackLogo.Visibility = cover == null ? Visibility.Visible : Visibility.Collapsed;
		Title = track == null
			? "OfflineMusicLibrary Mini"
			: track.Title + " - " + track.Artist;
		if (track != null)
		{
			MiniQueueList.SelectedItem = MiniQueueList.Items.Cast<object>()
				.OfType<TrackModel>()
				.FirstOrDefault(item => string.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase));
		}
	}

	public void UpdatePlaybackState(bool isPlaying, bool isFavorite)
	{
		PlayPauseButton.Content = isPlaying ? "Ⅱ" : "▶";
		PlayPauseButton.ToolTip = isPlaying ? "暂停" : "播放";
		FavoriteButton.Content = isFavorite ? "♥" : "♡";
		FavoriteButton.ToolTip = isFavorite ? "取消收藏当前歌曲" : "收藏当前歌曲";
	}

	public void UpdateProgress(long currentMilliseconds, long durationMilliseconds)
	{
		var duration = Math.Max(1L, durationMilliseconds);
		MiniProgress.Maximum = duration;
		MiniProgress.Value = Math.Clamp(currentMilliseconds, 0L, duration);
		MiniTimeText.Text = FormatTime(currentMilliseconds) + " / " + FormatTime(durationMilliseconds);
	}

	public void UpdateVolume(int volume)
	{
		var normalized = Math.Clamp(volume, 0, 100);
		VolumeButton.Content = normalized == 0 ? "×" : normalized < 45 ? "◖" : "◕";
		VolumeButton.ToolTip = normalized == 0
			? "已静音；单击恢复，滚轮调节音量"
			: $"音量 {normalized}%；单击静音，滚轮调节";
	}

	public void UpdateQueue(IReadOnlyList<TrackModel> queue, TrackModel? current)
	{
		var identity = string.Join('\u001f', queue.Select(track => track.Id));
		if (!string.Equals(identity, _queueIdentity, StringComparison.Ordinal))
		{
			_queueIdentity = identity;
			MiniQueueList.ItemsSource = queue.ToArray();
			QueueCountText.Text = $"{queue.Count:N0} 首";
		}
		if (current != null)
		{
			var selected = queue.FirstOrDefault(track =>
				string.Equals(track.Id, current.Id, StringComparison.OrdinalIgnoreCase));
			MiniQueueList.SelectedItem = selected;
			if (_expanded && selected != null)
				MiniQueueList.ScrollIntoView(selected);
		}
	}

	public void ShowNearDesktopEdge(bool closeRestoresMain = false, bool activate = false)
	{
		_closeRestoresMain = closeRestoresMain;
		ShowActivated = activate;
		ShowInTaskbar = closeRestoresMain;
		if (!_positioned)
		{
			var workArea = SystemParameters.WorkArea;
			Left = Math.Max(workArea.Left + 12.0, workArea.Right - Width - 18.0);
			Top = Math.Max(workArea.Top + 12.0, workArea.Bottom - Height - 18.0);
			_positioned = true;
		}
		if (!IsVisible)
			Show();
		ClampToWorkArea();
		if (activate)
			Activate();
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Left)
			return;
		if (e.ClickCount >= 2)
		{
			ActivateMainRequested?.Invoke(this, EventArgs.Empty);
			return;
		}
		try
		{
			DragMove();
			_positioned = true;
		}
		catch (InvalidOperationException)
		{
		}
	}

	private void Cover_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton == MouseButton.Left && e.ClickCount >= 2)
			ActivateMainRequested?.Invoke(this, EventArgs.Empty);
	}

	private void PreviousButton_Click(object sender, RoutedEventArgs e) =>
		PreviousRequested?.Invoke(this, EventArgs.Empty);

	private void PlayPauseButton_Click(object sender, RoutedEventArgs e) =>
		PlayPauseRequested?.Invoke(this, EventArgs.Empty);

	private void NextButton_Click(object sender, RoutedEventArgs e) =>
		NextRequested?.Invoke(this, EventArgs.Empty);

	private void FavoriteButton_Click(object sender, RoutedEventArgs e) =>
		FavoriteRequested?.Invoke(this, EventArgs.Empty);

	private void VolumeButton_Click(object sender, RoutedEventArgs e) =>
		MuteRequested?.Invoke(this, EventArgs.Empty);

	private void VolumeButton_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		VolumeDeltaRequested?.Invoke(e.Delta > 0 ? 5 : -5);
		e.Handled = true;
	}

	private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
	{
		_expanded = !_expanded;
		QueuePanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
		QueueToggleButton.Content = _expanded ? "▴" : "☷";
		QueueToggleButton.ToolTip = _expanded ? "收起播放队列" : "展开播放队列";
		Height = _expanded ? ExpandedHeight : CompactHeight;
		ClampToWorkArea();
		var selected = MiniQueueList.SelectedItem;
		if (_expanded && selected != null)
			MiniQueueList.ScrollIntoView(selected);
	}

	private void MiniQueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
	{
		if (MiniQueueList.SelectedItem is TrackModel track)
			QueueTrackRequested?.Invoke(track);
	}

	private void RestoreButton_Click(object sender, RoutedEventArgs e) =>
		ActivateMainRequested?.Invoke(this, EventArgs.Empty);

	private void CloseButton_Click(object sender, RoutedEventArgs e)
	{
		if (_closeRestoresMain)
		{
			ActivateMainRequested?.Invoke(this, EventArgs.Empty);
			return;
		}
		Hide();
		Dismissed?.Invoke(this, EventArgs.Empty);
	}

	private void ClampToWorkArea()
	{
		var workArea = SystemParameters.WorkArea;
		Left = Math.Clamp(Left, workArea.Left + 8.0, Math.Max(workArea.Left + 8.0, workArea.Right - Width - 8.0));
		Top = Math.Clamp(Top, workArea.Top + 8.0, Math.Max(workArea.Top + 8.0, workArea.Bottom - Height - 8.0));
	}

	private static string FormatTime(long milliseconds)
	{
		var time = TimeSpan.FromMilliseconds(Math.Max(0L, milliseconds));
		return time.TotalHours >= 1.0
			? time.ToString("h\\:mm\\:ss")
			: time.ToString("m\\:ss");
	}
}
