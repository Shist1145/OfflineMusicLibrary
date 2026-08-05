using System.Windows;
using System.Windows.Controls;
using OfflineMusicLibrary;

internal static class Program
{
	[STAThread]
	private static void Main()
	{
		using var scope = new WindowScope(new PreviewPlayerWindow());
		var window = scope.Window;
		Assert(window.Width == 420 && window.Height == 88, "Mini 默认尺寸应为 420×88");
		Assert(window.Topmost && window.WindowStyle == WindowStyle.None, "Mini 应无系统标题栏且始终置顶");

		var tracks = new[]
		{
			new TrackModel { Id = "one", Title = "First", Artist = "Artist A", FilePath = "one.mp3" },
			new TrackModel { Id = "two", Title = "Second", Artist = "Artist B", FilePath = "two.mp3" }
		};
		window.UpdateQueue(tracks, tracks[1]);
		window.UpdateTrack(tracks[1], null);
		window.UpdatePlaybackState(isPlaying: true, isFavorite: true);
		window.UpdateProgress(30_000, 180_000);
		window.UpdateVolume(68);

		var queue = Require<ListBox>(window, "MiniQueueList");
		Assert(queue.Items.Count == 2 && ReferenceEquals(queue.SelectedItem, tracks[1]), "Mini 队列与当前歌曲应同步");
		Assert(Require<TextBlock>(window, "PreviewTitleText").Text == "Second", "Mini 应显示当前歌名");
		Assert(Require<Button>(window, "PlayPauseButton").Content?.ToString() == "Ⅱ", "播放状态应同步");
		Assert(Require<Button>(window, "FavoriteButton").Content?.ToString() == "♥", "收藏状态应同步");
		Assert(Require<ProgressBar>(window, "MiniProgress").Value == 30_000, "播放进度应同步");

		Require<Button>(window, "QueueToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert(window.Height == 356, "展开队列后高度应为 356");
		Assert(Require<Border>(window, "QueuePanel").Visibility == Visibility.Visible, "展开后应显示播放队列");
		Require<Button>(window, "QueueToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert(window.Height == 88, "再次点击应回到紧凑高度");

		Console.WriteLine("Mini player checks passed.");
	}

	private static T Require<T>(FrameworkElement owner, string name) where T : class
	{
		if (owner.FindName(name) is T value)
			return value;
		throw new InvalidOperationException($"找不到控件 {name}");
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException(message);
	}

	private sealed class WindowScope : IDisposable
	{
		public PreviewPlayerWindow Window { get; }

		public WindowScope(PreviewPlayerWindow window)
		{
			Window = window;
		}

		public void Dispose()
		{
			Window.Close();
		}
	}
}
