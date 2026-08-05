using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace OfflineMusicLibrary;

public sealed class TrackModel : INotifyPropertyChanged
{
	private bool _isFavorite;

	private string _recommendationReason = "";

	public string Id { get; set; } = "";

	public string FilePath { get; set; } = "";

	public string Title { get; set; } = "";

	public string Artist { get; set; } = "未知艺术家";

	public string Album { get; set; } = "未知专辑";

	public string AlbumArtist { get; set; } = "";

	public string Circle { get; set; } = "";

	public bool CircleIsManual { get; set; }

	public string Genre { get; set; } = "";

	public string Format { get; set; } = "";

	public int Year { get; set; }

	public uint TrackNumber { get; set; }

	public long DurationMs { get; set; }

	public bool HasCover { get; set; }

	public bool HasLyrics { get; set; }

	public bool IsVideo { get; set; }

	public int PlayCount { get; set; }

	public DateTime? LastPlayedAt { get; set; }

	public DateTime AddedAt { get; set; } = DateTime.Now;

	public List<string> Categories { get; set; } = new List<string>();

	public string? CloudId { get; set; }

	public List<string> CloudIds { get; set; } = new List<string>();

	[JsonIgnore]
	public bool HasCloudIds
	{
		get
		{
			if (string.IsNullOrWhiteSpace(CloudId))
			{
				List<string> cloudIds = CloudIds;
				if (cloudIds != null)
				{
					return cloudIds.Count > 0;
				}
				return false;
			}
			return true;
		}
	}

	public bool IsFavorite
	{
		get
		{
			return _isFavorite;
		}
		set
		{
			if (_isFavorite != value)
			{
				_isFavorite = value;
				OnPropertyChanged("IsFavorite");
				OnPropertyChanged("FavoriteGlyph");
				OnPropertyChanged("FavoriteToolTip");
			}
		}
	}

	[JsonIgnore]
	public BitmapSource? CoverThumbnail => CoverService.LoadThumbnail(this);

	[JsonIgnore]
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

	[JsonIgnore]
	public string FavoriteToolTip
	{
		get
		{
			if (!IsFavorite)
			{
				return "收藏这首歌";
			}
			return "取消收藏这首歌";
		}
	}

	[JsonIgnore]
	public string AlbumKey => AlbumIdentity.Create(this);

	[JsonIgnore]
	public bool IsEncryptedNcm => string.Equals(Path.GetExtension(FilePath), ".ncm", StringComparison.OrdinalIgnoreCase);

	[JsonIgnore]
	public string RecommendationReason
	{
		get => _recommendationReason;
		set
		{
			if (!string.Equals(_recommendationReason, value, StringComparison.Ordinal))
			{
				_recommendationReason = value;
				OnPropertyChanged();
			}
		}
	}

	public string DurationText => TimeSpan.FromMilliseconds(DurationMs).ToString((DurationMs >= 3600000) ? "h\\:mm\\:ss" : "m\\:ss");

	public string TrackText
	{
		get
		{
			if (TrackNumber != 0)
			{
				return TrackNumber.ToString("00");
			}
			return "";
		}
	}

	public string CategoryText => string.Join(" / ", Categories);

	public string LastPlayedText => LastPlayedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-";

	public string PlayCountText => $"{PlayCount:N0} 次";

	public string MediaTypeText
	{
		get
		{
			if (!IsEncryptedNcm)
			{
				if (!IsVideo)
				{
					return "音频";
				}
				return "视频";
			}
			return "NCM 加密文件";
		}
	}

	public string CircleText
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(Circle))
			{
				return Circle;
			}
			return "未识别";
		}
	}

	public string SearchText => $"{Title} {Artist} {Album} {AlbumArtist} {Circle} {Genre} {string.Join(' ', Categories)} {Path.GetFileNameWithoutExtension(FilePath)}".ToLowerInvariant();

	public event PropertyChangedEventHandler? PropertyChanged;

	public IEnumerable<string> GetCloudIds()
	{
		if (!string.IsNullOrWhiteSpace(CloudId))
		{
			yield return CloudId;
		}
		foreach (string id in CloudIds ?? new List<string>())
		{
			if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, CloudId, StringComparison.OrdinalIgnoreCase))
			{
				yield return id;
			}
		}
	}

	public bool HasCloudId(string id)
	{
		return GetCloudIds().Contains<string>(id, StringComparer.OrdinalIgnoreCase);
	}

	public void RememberCloudId(string id)
	{
		if (!string.IsNullOrWhiteSpace(id) && !HasCloudId(id))
		{
			if (string.IsNullOrWhiteSpace(CloudId))
			{
				CloudId = id;
			}
			else
			{
				(CloudIds ?? (CloudIds = new List<string>())).Add(id);
			}
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
