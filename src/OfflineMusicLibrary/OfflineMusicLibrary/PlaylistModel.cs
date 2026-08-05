using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace OfflineMusicLibrary;

public sealed class PlaylistModel : INotifyPropertyChanged
{
	private BitmapSource? _coverThumbnail;

	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name { get; set; } = "新歌单";

	public string Description { get; set; } = "";

	public string CoverPath { get; set; } = "";

	public List<string> Tags { get; set; } = new List<string>();

	public List<string> TrackIds { get; set; } = new List<string>();

	public string Source { get; set; } = "local";

	public string? CloudPlaylistId { get; set; }

	public DateTime CreatedAt { get; set; } = DateTime.Now;

	public DateTime UpdatedAt { get; set; } = DateTime.Now;

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
				OnPropertyChanged("CoverThumbnail");
			}
		}
	}

	[JsonIgnore]
	public string CountText => $"{TrackIds.Count:N0} 首";

	[JsonIgnore]
	public string DescriptionPreview
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(Description))
			{
				return Description.Trim();
			}
			return "暂无简介";
		}
	}

	[JsonIgnore]
	public string TagsText
	{
		get
		{
			if (Tags.Count != 0)
			{
				return string.Join(" · ", Tags);
			}
			return "未设置标签";
		}
	}

	[JsonIgnore]
	public string SourceText
	{
		get
		{
			if (!string.Equals(Source, "netease", StringComparison.OrdinalIgnoreCase))
			{
				return "本地歌单";
			}
			return "网易云导入";
		}
	}

	[JsonIgnore]
	public string UpdatedText => $"{SourceText} · 创建于 {CreatedAt:yyyy-MM-dd} · 更新于 {UpdatedAt:yyyy-MM-dd HH:mm}";

	public event PropertyChangedEventHandler? PropertyChanged;

	public void InvalidateCover()
	{
		CoverThumbnail = null;
	}

	public void NotifyMetadataChanged()
	{
		OnPropertyChanged("Name");
		OnPropertyChanged("CountText");
		OnPropertyChanged("DescriptionPreview");
		OnPropertyChanged("TagsText");
		OnPropertyChanged("SourceText");
		OnPropertyChanged("UpdatedText");
	}

	public override string ToString()
	{
		return Name;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
