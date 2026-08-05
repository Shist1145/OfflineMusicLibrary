using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace OfflineMusicLibrary;

public sealed class AlbumViewModel : INotifyPropertyChanged
{
	private BitmapSource? _coverThumbnail;

	public string Key { get; init; } = "";

	public string Title { get; init; } = "未知专辑";

	public string Artist { get; init; } = "未知艺术家";

	public string CircleNames { get; init; } = "";

	public int TrackCount { get; init; }

	public bool IsFavorite { get; init; }

	public TrackModel? RepresentativeTrack { get; init; }

	[JsonIgnore]
	public BitmapSource? CoverThumbnail
	{
		get
		{
			return _coverThumbnail;
		}
		set
		{
			if (_coverThumbnail != value)
			{
				_coverThumbnail = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CoverThumbnail"));
			}
		}
	}

	public string DisplayName
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(Artist) && !(Artist == "未知艺术家"))
			{
				return Title + " — " + Artist;
			}
			return Title;
		}
	}

	public string CountText
	{
		get
		{
			if (IsFavorite)
			{
				return $"♥ {TrackCount:N0} 首";
			}
			return $"{TrackCount:N0} 首";
		}
	}

	public string FavoriteGlyph
	{
		get
		{
			if (!IsFavorite)
			{
				return "♡";
			}
			return "♥";
		}
	}

	public string FavoriteToolTip
	{
		get
		{
			if (!IsFavorite)
			{
				return "收藏这张专辑";
			}
			return "取消收藏这张专辑";
		}
	}

	public string SearchText => $"{Title} {Artist} {CircleNames}".ToLowerInvariant();

	public event PropertyChangedEventHandler? PropertyChanged;

	public override string ToString()
	{
		return DisplayName;
	}
}
