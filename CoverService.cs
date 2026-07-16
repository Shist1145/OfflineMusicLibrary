using System.IO;
using System.Windows.Media.Imaging;
using TagFile = TagLib.File;

namespace OfflineMusicLibrary;

public static class CoverService
{
    private static readonly string[] SidecarNames = ["cover", "folder", "front", "album", "albumart"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly object ThumbnailCacheLock = new();
    private static readonly Dictionary<string, BitmapSource?> ThumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindSidecar(string audioPath)
    {
        var directory = Path.GetDirectoryName(audioPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        foreach (var name in SidecarNames)
        foreach (var extension in ImageExtensions)
        {
            var path = Path.Combine(directory, name + extension);
            if (File.Exists(path))
                return path;
        }

        try
        {
            return Directory.EnumerateFiles(directory)
                .FirstOrDefault(path =>
                    ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
                    SidecarNames.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static BitmapSource? LoadCover(TrackModel track)
    {
        return LoadCoverCore(track, decodePixelWidth: null);
    }

    public static BitmapSource? LoadImageFile(string path, int decodePixelWidth = 320)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return CreateBitmap(File.ReadAllBytes(path), Math.Clamp(decodePixelWidth, 32, 1600));
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? LoadThumbnail(TrackModel track) => LoadThumbnail(track, decodePixelWidth: 72);

    public static BitmapSource? LoadThumbnail(TrackModel track, int decodePixelWidth)
    {
        var key = $"{Math.Clamp(decodePixelWidth, 32, 800)}:{CreateCacheKey(track)}";
        lock (ThumbnailCacheLock)
            if (ThumbnailCache.TryGetValue(key, out var cached))
                return cached;

        var thumbnail = LoadCoverCore(track, decodePixelWidth: Math.Clamp(decodePixelWidth, 32, 800));
        lock (ThumbnailCacheLock)
        {
            if (ThumbnailCache.Count > 1600)
                ThumbnailCache.Clear();
            ThumbnailCache[key] = thumbnail;
        }
        return thumbnail;
    }

    private static BitmapSource? LoadCoverCore(TrackModel track, int? decodePixelWidth)
    {
        try
        {
            using var file = TagFile.Create(track.FilePath);
            var picture = file.Tag.Pictures.FirstOrDefault();
            if (picture?.Data?.Data is { Length: > 0 } bytes)
                return CreateBitmap(bytes, decodePixelWidth);
        }
        catch
        {
            // Sidecar artwork remains a useful fallback when a tag cannot be read.
        }

        var sidecar = FindSidecar(track.FilePath);
        if (sidecar is null)
            return null;

        try
        {
            return CreateBitmap(File.ReadAllBytes(sidecar), decodePixelWidth);
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
            var fileInfo = new FileInfo(track.FilePath);
            var sidecar = FindSidecar(track.FilePath);
            var sidecarStamp = sidecar is null || !File.Exists(sidecar)
                ? ""
                : $"{sidecar}:{File.GetLastWriteTimeUtc(sidecar).Ticks}";
            return $"{track.FilePath}:{fileInfo.LastWriteTimeUtc.Ticks}:{fileInfo.Length}:{sidecarStamp}";
        }
        catch
        {
            return track.FilePath;
        }
    }

    private static BitmapSource CreateBitmap(byte[] bytes, int? decodePixelWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (decodePixelWidth is > 0)
            bitmap.DecodePixelWidth = decodePixelWidth.Value;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
