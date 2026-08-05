using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
				if (System.IO.File.Exists(path))
				{
					return path;
				}
			}
		}
		try
		{
			return Directory.EnumerateFiles(directory).FirstOrDefault((string path2) => ImageExtensions.Contains<string>(Path.GetExtension(path2), StringComparer.OrdinalIgnoreCase) && SidecarNames.Contains<string>(Path.GetFileNameWithoutExtension(path2), StringComparer.OrdinalIgnoreCase));
		}
		catch (IOException)
		{
			return null;
		}
	}

	public static BitmapSource? LoadCover(TrackModel track)
	{
		return LoadCoverCore(track, null);
	}

	public static BitmapSource? LoadImageFile(string path, int decodePixelWidth = 320)
	{
		if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
		{
			return null;
		}
		try
		{
			return CreateBitmap(System.IO.File.ReadAllBytes(path), Math.Clamp(decodePixelWidth, 32, 1600));
		}
		catch
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
		string key = $"{Math.Clamp(decodePixelWidth, 32, 800)}:{CreateCacheKey(track)}";
		lock (ThumbnailCacheLock)
		{
			if (ThumbnailCache.TryGetValue(key, out BitmapSource? cached))
			{
				return cached;
			}
		}
		BitmapSource? thumbnail = LoadCoverCore(track, Math.Clamp(decodePixelWidth, 32, 800));
		lock (ThumbnailCacheLock)
		{
			if (ThumbnailCache.Count > 1600)
			{
				ThumbnailCache.Clear();
			}
			ThumbnailCache[key] = thumbnail;
			return thumbnail;
		}
	}

	private static BitmapSource? LoadCoverCore(TrackModel track, int? decodePixelWidth)
	{
		try
		{
			using TagLib.File file = TagLib.File.Create(track.FilePath);
			byte[] bytes = file.Tag.Pictures.FirstOrDefault()?.Data?.Data;
			if (bytes != null && bytes.Length > 0)
			{
				return CreateBitmap(bytes, decodePixelWidth);
			}
		}
		catch
		{
		}
		string sidecar = FindSidecar(track.FilePath);
		if (sidecar == null)
		{
			return null;
		}
		try
		{
			return CreateBitmap(System.IO.File.ReadAllBytes(sidecar), decodePixelWidth);
		}
		catch
		{
			return null;
		}
	}

	private static string CreateCacheKey(TrackModel track)
	{
		try
		{
			FileInfo fileInfo = new FileInfo(track.FilePath);
			string sidecar = FindSidecar(track.FilePath);
			string sidecarStamp = ((sidecar == null || !System.IO.File.Exists(sidecar)) ? "" : $"{sidecar}:{System.IO.File.GetLastWriteTimeUtc(sidecar).Ticks}");
			return $"{track.FilePath}:{fileInfo.LastWriteTimeUtc.Ticks}:{fileInfo.Length}:{sidecarStamp}";
		}
		catch
		{
			return track.FilePath;
		}
	}

	private static BitmapSource CreateBitmap(byte[] bytes, int? decodePixelWidth)
	{
		using MemoryStream stream = new MemoryStream(bytes, writable: false);
		BitmapImage bitmap = new BitmapImage();
		bitmap.BeginInit();
		bitmap.CacheOption = BitmapCacheOption.OnLoad;
		bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
		if (decodePixelWidth.HasValue && decodePixelWidth.GetValueOrDefault() > 0)
		{
			bitmap.DecodePixelWidth = decodePixelWidth.Value;
		}
		bitmap.StreamSource = stream;
		bitmap.EndInit();
		bitmap.Freeze();
		return bitmap;
	}
}
