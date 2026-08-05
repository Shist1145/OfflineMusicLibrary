using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace OfflineMusicLibrary;

public sealed class CircleViewModel : INotifyPropertyChanged
{
	private BitmapSource? _coverThumbnail;

	public string Key { get; init; } = "";

	public string Name { get; init; } = "未识别社团";

	public int AlbumCount { get; init; }

	public int TrackCount { get; init; }

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

	public string CountText => $"{AlbumCount:N0} 张专辑 · {TrackCount:N0} 首";

	public string SearchText => Name.ToLowerInvariant();

	public event PropertyChangedEventHandler? PropertyChanged;

	public override string ToString()
	{
		return Name;
	}
}
