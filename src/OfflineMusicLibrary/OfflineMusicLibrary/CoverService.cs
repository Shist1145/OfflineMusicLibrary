using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Media.Imaging;
using TagLib;

namespace OfflineMusicLibrary;

public static class CoverService
{
	private static readonly string[] SidecarNames = new string[5] { "cover", "folder", "front", "album", "albumart" };

	private static readonly string[] ImageExtensions = new string[4] { ".jpg", ".jpeg", ".png", ".webp" };

	private static readonly object ThumbnailCacheLock = new object();

	private static readonly Dictionary<string, BitmapSource?> ThumbnailCache = new Dictionary<string, BitmapSource?>(StringComparer.OrdinalIgnoreCase);

	public static string? FindSidecar(string audioPath)
	{
		string directory = Path.GetDirectoryName(audioPath);
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return null;
		}
		string[] sidecarNames = SidecarNames;
		foreach (string name in sidecarNames)
		{
			string[] imageExtensions = ImageExtensions;
			foreach (string extension in imageExtensions)
			{
				string path = Path.Combine(directory, name + extension);
				if (IsSafeSidecar(path))
				{
					return path;
				}
			}
		}
		try
		{
			return Directory.EnumerateFiles(directory).FirstOrDefault((string path2) =>
				ImageExtensions.Contains<string>(Path.GetExtension(path2), StringComparer.OrdinalIgnoreCase) &&
				SidecarNames.Contains<string>(Path.GetFileNameWithoutExtension(path2), StringComparer.OrdinalIgnoreCase) &&
				IsSafeSidecar(path2));
		}
		catch (Exception ex) when (IsExpectedMediaException(ex))
		{
			return null;
		}
	}

	public static BitmapSource? LoadCover(TrackModel track)
	{
		return LoadCoverCore(track, 1600, allowSourceRead: true);
	}

	public static BitmapSource? LoadImageFile(string path, int decodePixelWidth = 320)
	{
		if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
		{
			return null;
		}
		try
		{
			return CreateBitmap(BoundedFileReader.ReadAllBytes(path, ContentReadLimits.ArtworkBytes), Math.Clamp(decodePixelWidth, 32, 1600));
		}
		catch (Exception ex) when (IsExpectedMediaException(ex))
		{
			return null;
		}
	}

	public static BitmapSource? LoadThumbnail(TrackModel track)
	{
		return LoadThumbnail(track, 72);
	}

	public static BitmapSource? LoadThumbnail(TrackModel track, int decodePixelWidth)
	{
		return LoadThumbnailCore(track, decodePixelWidth, allowSourceRead: true);
	}

	public static BitmapSource? LoadCachedThumbnail(TrackModel track, int decodePixelWidth = 72)
	{
		return LoadThumbnailCore(track, decodePixelWidth, allowSourceRead: false);
	}

	private static BitmapSource? LoadThumbnailCore(TrackModel track, int decodePixelWidth, bool allowSourceRead)
	{
		string key = $"{Math.Clamp(decodePixelWidth, 32, 800)}:{CreateCacheKey(track)}";
		lock (ThumbnailCacheLock)
		{
			if (ThumbnailCache.TryGetValue(key, out BitmapSource? cached))
			{
				return cached;
			}
		}
		BitmapSource? thumbnail = LoadCoverCore(track, Math.Clamp(decodePixelWidth, 32, 800), allowSourceRead);
		lock (ThumbnailCacheLock)
		{
			if (ThumbnailCache.Count > 1600)
			{
				ThumbnailCache.Clear();
			}
			if (thumbnail != null)
			{
				ThumbnailCache[key] = thumbnail;
			}
			return thumbnail;
		}
	}

	private static BitmapSource? LoadCoverCore(TrackModel track, int? decodePixelWidth, bool allowSourceRead)
	{
		AssetSourceStamp indexedStamp = new(Math.Max(0, track.FileSize), Math.Max(0, track.LastWriteTimeUtcTicks));
		if (PersistentAssetCache.TryRead("artwork", track.Id, indexedStamp, allowStale: false, out byte[] cachedBytes))
		{
			try
			{
				return CreateBitmap(cachedBytes, decodePixelWidth);
			}
			catch (Exception ex) when (IsExpectedMediaException(ex))
			{
			}
		}
		if (!allowSourceRead)
		{
			return PersistentAssetCache.TryRead("artwork", track.Id, null, allowStale: true, out cachedBytes)
				? TryCreateBitmap(cachedBytes, decodePixelWidth)
				: null;
		}

		byte[]? sourceBytes = null;
		AssetSourceStamp sourceStamp = indexedStamp;
		try
		{
			using TagLib.File file = TagLib.File.Create(track.FilePath);
			sourceBytes = file.Tag.Pictures.FirstOrDefault()?.Data?.Data;
			if (sourceBytes != null && sourceBytes.Length > 0)
			{
				sourceStamp = ReadSourceStamp(track.FilePath, "embedded");
			}
		}
		catch (Exception ex) when (IsExpectedMediaException(ex))
		{
		}
		if (sourceBytes is { Length: > ContentReadLimits.ArtworkBytes })
		{
			sourceBytes = null;
		}
		if (sourceBytes == null || sourceBytes.Length == 0)
		{
			string? sidecar = FindSidecar(track.FilePath);
			if (sidecar != null)
			{
				try
				{
					sourceBytes = BoundedFileReader.ReadAllBytes(sidecar, ContentReadLimits.ArtworkBytes);
					AssetSourceStamp sidecarStamp = ReadSourceStamp(sidecar, Path.GetFileName(sidecar));
					sourceStamp = new AssetSourceStamp(
						indexedStamp.Length,
						indexedStamp.LastWriteUtcTicks,
						$"{sidecarStamp.Signature}:{sidecarStamp.Length}:{sidecarStamp.LastWriteUtcTicks}");
				}
				catch (Exception ex) when (IsExpectedMediaException(ex))
				{
					sourceBytes = null;
				}
			}
		}
		if (sourceBytes != null && sourceBytes.Length > 0)
		{
			PersistentAssetCache.Write("artwork", track.Id, sourceStamp, sourceBytes);
			return TryCreateBitmap(sourceBytes, decodePixelWidth);
		}
		return PersistentAssetCache.TryRead("artwork", track.Id, null, allowStale: true, out cachedBytes)
			? TryCreateBitmap(cachedBytes, decodePixelWidth)
			: null;
	}

	private static string CreateCacheKey(TrackModel track)
	{
		return $"{track.Id}:{track.FileSize}:{track.LastWriteTimeUtcTicks}";
	}

	private static AssetSourceStamp ReadSourceStamp(string path, string signature)
	{
		try
		{
			FileInfo info = new(path);
			return new AssetSourceStamp(info.Length, info.LastWriteTimeUtc.Ticks, signature);
		}
		catch (Exception ex) when (IsExpectedMediaException(ex))
		{
			return new AssetSourceStamp(0, 0, signature);
		}
	}

	private static BitmapSource? TryCreateBitmap(byte[] bytes, int? decodePixelWidth)
	{
		try
		{
			return CreateBitmap(bytes, decodePixelWidth);
		}
		catch (Exception ex) when (IsExpectedMediaException(ex))
		{
			return null;
		}
	}

	private static BitmapSource CreateBitmap(byte[] bytes, int? decodePixelWidth)
	{
		using MemoryStream stream = new MemoryStream(bytes, writable: false);
		BitmapImage bitmap = new BitmapImage();
		bitmap.BeginInit();
		bitmap.CacheOption = BitmapCacheOption.OnLoad;
		bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
		int decodeLimit = Math.Clamp(decodePixelWidth ?? 1600, 32, 1600);
		bitmap.DecodePixelWidth = decodeLimit;
		bitmap.DecodePixelHeight = decodeLimit;
		bitmap.StreamSource = stream;
		bitmap.EndInit();
		bitmap.Freeze();
		return bitmap;
	}

	private static bool IsSafeSidecar(string path)
	{
		try
		{
			if (!System.IO.File.Exists(path))
			{
				return false;
			}
			FileInfo info = new(path);
			return (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
				info.Length is > 0 and <= ContentReadLimits.ArtworkBytes;
		}
		catch (Exception ex) when (IsExpectedMediaException(ex))
		{
			return false;
		}
	}

	private static bool IsExpectedMediaException(Exception exception)
	{
		return exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or
			ArgumentException or InvalidOperationException or SecurityException or COMException or
			CorruptFileException or UnsupportedFormatException;
	}
}
