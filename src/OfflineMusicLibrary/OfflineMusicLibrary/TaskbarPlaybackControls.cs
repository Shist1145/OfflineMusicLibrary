using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace OfflineMusicLibrary;

internal sealed class TaskbarPlaybackControls
{
	private enum Glyph
	{
		Previous,
		Play,
		Pause,
		Next,
		Favorite,
		FavoriteActive
	}

	private readonly ThumbButtonInfo _previous;

	private readonly ThumbButtonInfo _playPause;

	private readonly ThumbButtonInfo _next;

	private readonly ThumbButtonInfo _favorite;

	private readonly ImageSource _playIcon = CreateGlyph(Glyph.Play);

	private readonly ImageSource _pauseIcon = CreateGlyph(Glyph.Pause);

	private readonly ImageSource _favoriteIcon = CreateGlyph(Glyph.Favorite);

	private readonly ImageSource _favoriteActiveIcon = CreateGlyph(Glyph.FavoriteActive);

	private bool? _lastPlaying;

	private bool? _lastFavorite;

	public TaskbarItemInfo ItemInfo { get; }

	public event EventHandler? PreviousRequested;

	public event EventHandler? PlayPauseRequested;

	public event EventHandler? NextRequested;

	public event EventHandler? FavoriteRequested;

	public TaskbarPlaybackControls()
	{
		_previous = CreateButton("上一首", CreateGlyph(Glyph.Previous));
		_playPause = CreateButton("播放", _playIcon);
		_next = CreateButton("下一首", CreateGlyph(Glyph.Next));
		_favorite = CreateButton("收藏当前歌曲", _favoriteIcon);
		_previous.Click += delegate
		{
			this.PreviousRequested?.Invoke(this, EventArgs.Empty);
		};
		_playPause.Click += delegate
		{
			this.PlayPauseRequested?.Invoke(this, EventArgs.Empty);
		};
		_next.Click += delegate
		{
			this.NextRequested?.Invoke(this, EventArgs.Empty);
		};
		_favorite.Click += delegate
		{
			this.FavoriteRequested?.Invoke(this, EventArgs.Empty);
		};
		ItemInfo = new TaskbarItemInfo();
		ItemInfo.ThumbButtonInfos.Add(_previous);
		ItemInfo.ThumbButtonInfos.Add(_playPause);
		ItemInfo.ThumbButtonInfos.Add(_next);
		ItemInfo.ThumbButtonInfos.Add(_favorite);
	}

	public void Update(bool hasTrack, bool hasQueue, bool isPlaying, bool isFavorite)
	{
		_previous.IsEnabled = true;
		_next.IsEnabled = true;
		_playPause.IsEnabled = true;
		_favorite.IsEnabled = true;
		if (_lastPlaying != isPlaying)
		{
			_lastPlaying = isPlaying;
			_playPause.ImageSource = (isPlaying ? _pauseIcon : _playIcon);
			_playPause.Description = (isPlaying ? "暂停" : "播放");
		}
		if (_lastFavorite != isFavorite)
		{
			_lastFavorite = isFavorite;
			_favorite.ImageSource = (isFavorite ? _favoriteActiveIcon : _favoriteIcon);
			_favorite.Description = (isFavorite ? "取消收藏当前歌曲" : "收藏当前歌曲");
		}
	}

	private static ThumbButtonInfo CreateButton(string description, ImageSource image)
	{
		return new ThumbButtonInfo
		{
			Description = description,
			ImageSource = image,
			DismissWhenClicked = false,
			IsInteractive = true,
			Visibility = Visibility.Visible
		};
	}

	private static BitmapSource CreateGlyph(Glyph glyph)
	{
		DrawingVisual visual = new DrawingVisual();
		using (DrawingContext drawing = visual.RenderOpen())
		{
			SolidColorBrush white = new SolidColorBrush(Color.FromRgb(245, 247, 250));
			SolidColorBrush accent = new SolidColorBrush(Color.FromRgb(byte.MaxValue, 75, 107));
			Pen pen = new Pen(white, 2.2)
			{
				LineJoin = PenLineJoin.Round
			};
			switch (glyph)
			{
			case Glyph.Previous:
				drawing.DrawRoundedRectangle(white, null, new Rect(5.0, 6.0, 2.5, 20.0), 1.0, 1.0);
				drawing.DrawGeometry(white, null, Geometry.Parse("M 24,6 L 9,16 24,26 Z"));
				break;
			case Glyph.Play:
				drawing.DrawGeometry(white, null, Geometry.Parse("M 8,5 L 26,16 8,27 Z"));
				break;
			case Glyph.Pause:
				drawing.DrawRoundedRectangle(white, null, new Rect(8.0, 6.0, 6.0, 20.0), 1.5, 1.5);
				drawing.DrawRoundedRectangle(white, null, new Rect(19.0, 6.0, 6.0, 20.0), 1.5, 1.5);
				break;
			case Glyph.Next:
				drawing.DrawRoundedRectangle(white, null, new Rect(24.5, 6.0, 2.5, 20.0), 1.0, 1.0);
				drawing.DrawGeometry(white, null, Geometry.Parse("M 8,6 L 23,16 8,26 Z"));
				break;
			case Glyph.Favorite:
				drawing.DrawGeometry(Brushes.Transparent, pen, HeartGeometry());
				break;
			case Glyph.FavoriteActive:
				drawing.DrawGeometry(accent, new Pen(accent, 1.4), HeartGeometry());
				break;
			}
		}
		RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(32, 32, 96.0, 96.0, PixelFormats.Pbgra32);
		renderTargetBitmap.Render(visual);
		renderTargetBitmap.Freeze();
		return renderTargetBitmap;
	}

	private static Geometry HeartGeometry()
	{
		return Geometry.Parse("M 16,27 C 13,24 5,19 5,11 C 5,7 8,5 11,5 C 13.5,5 15,6.5 16,8.5 C 17,6.5 18.5,5 21,5 C 25,5 28,8 27,12 C 26,19 19,24 16,27 Z");
	}
}
