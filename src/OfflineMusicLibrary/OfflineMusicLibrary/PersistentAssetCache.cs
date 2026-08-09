using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OfflineMusicLibrary;

public readonly record struct AssetSourceStamp(
	long Length,
	long LastWriteUtcTicks,
	string Signature = "");

public sealed class CachedAssetRef
{
	public string TrackId { get; set; } = "";

	public string AssetKind { get; set; } = "";

	public string CachePath { get; set; } = "";

	public long SourceLength { get; set; }

	public long SourceLastWriteUtcTicks { get; set; }

	public string SourceSignature { get; set; } = "";

	public DateTime CacheCreatedUtc { get; set; }
}

public readonly record struct AssetCacheStatistics(long Bytes, int Files, long MaximumBytes, bool Enabled);

public static class PersistentAssetCache
{
	private static readonly object SettingsGate = new();
	private static readonly object[] EntryLocks = Enumerable.Range(0, 128).Select(_ => new object()).ToArray();
	private static readonly EnumerationOptions CacheEnumerationOptions = new()
	{
		RecurseSubdirectories = true,
		IgnoreInaccessible = true,
		ReturnSpecialDirectories = false,
		AttributesToSkip = FileAttributes.ReparsePoint
	};
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
	private static string _baseDirectory = "";
	private static long _maximumBytes = 1024L * 1024L * 1024L;
	private static bool _enabled;
	private static DateTime _nextTrimUtc;

	public static void Configure(string baseDirectory, bool enabled, int maximumMegabytes)
	{
		lock (SettingsGate)
		{
			try
			{
				_baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
					? ""
					: Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
			}
			catch (Exception ex) when (IsExpectedFileSystemException(ex))
			{
				DiagnosticLog.Write("ASSET_CACHE", "Invalid local asset cache directory", ex);
				_baseDirectory = "";
			}
			_enabled = enabled && _baseDirectory.Length > 0;
			_maximumBytes = (long)Math.Clamp(maximumMegabytes, 128, 8192) * 1024L * 1024L;
			_nextTrimUtc = DateTime.MinValue;
		}
	}

	public static bool TryRead(
		string assetKind,
		string trackId,
		AssetSourceStamp? currentSource,
		bool allowStale,
		out byte[] bytes)
	{
		bytes = Array.Empty<byte>();
		if (!TryResolveEntry(assetKind, trackId, out CacheEntryPaths paths))
		{
			return false;
		}
		lock (GetEntryLock(paths.LockKey))
		{
			try
			{
				if (!IsSafeCachePath(paths.BaseDirectory, paths.MetadataPath) ||
					!IsSafeCachePath(paths.BaseDirectory, paths.PayloadPath))
				{
					return false;
				}
				if (!File.Exists(paths.MetadataPath) || !File.Exists(paths.PayloadPath))
				{
					return false;
				}
				byte[] metadata = BoundedFileReader.ReadAllBytes(paths.MetadataPath, ContentReadLimits.CacheMetadataBytes);
				CachedAssetRef? reference = JsonSerializer.Deserialize<CachedAssetRef>(metadata, JsonOptions);
				if (reference == null ||
					!string.Equals(reference.TrackId, trackId, StringComparison.Ordinal) ||
					!string.Equals(reference.AssetKind, assetKind, StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(reference.CachePath, Path.GetFileName(paths.PayloadPath), StringComparison.Ordinal))
				{
					return false;
				}
				if (currentSource.HasValue && !Matches(reference, currentSource.Value) && !allowStale)
				{
					return false;
				}
				bytes = BoundedFileReader.ReadAllBytes(paths.PayloadPath, GetPayloadLimit(assetKind));
				return bytes.Length > 0;
			}
			catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException or SecurityException or ArgumentException)
			{
				DiagnosticLog.Write("ASSET_CACHE", $"Could not read {assetKind} cache for '{trackId}'", ex);
				bytes = Array.Empty<byte>();
				return false;
			}
		}
	}

	public static bool Write(
		string assetKind,
		string trackId,
		AssetSourceStamp source,
		ReadOnlySpan<byte> bytes)
	{
		if (bytes.Length == 0 || !TryResolveEntry(assetKind, trackId, out CacheEntryPaths paths) ||
			bytes.Length > GetPayloadLimit(assetKind))
		{
			return false;
		}
		lock (GetEntryLock(paths.LockKey))
		{
			string payloadTemporary = paths.PayloadPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
			string metadataTemporary = paths.MetadataPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
			try
			{
				if (!IsSafeCachePath(paths.BaseDirectory, paths.PayloadPath) ||
					!IsSafeCachePath(paths.BaseDirectory, paths.MetadataPath))
				{
					return false;
				}
				Directory.CreateDirectory(paths.Directory);
				if (!IsSafeCachePath(paths.BaseDirectory, paths.PayloadPath) ||
					!IsSafeCachePath(paths.BaseDirectory, paths.MetadataPath))
				{
					return false;
				}
				File.WriteAllBytes(payloadTemporary, bytes.ToArray());
				CachedAssetRef reference = new()
				{
					TrackId = trackId,
					AssetKind = assetKind,
					CachePath = Path.GetFileName(paths.PayloadPath),
					SourceLength = Math.Max(0, source.Length),
					SourceLastWriteUtcTicks = Math.Max(0, source.LastWriteUtcTicks),
					SourceSignature = source.Signature ?? "",
					CacheCreatedUtc = DateTime.UtcNow
				};
				File.WriteAllText(metadataTemporary, JsonSerializer.Serialize(reference, JsonOptions));
				File.Move(payloadTemporary, paths.PayloadPath, overwrite: true);
				File.Move(metadataTemporary, paths.MetadataPath, overwrite: true);
				TrimIfNeeded();
				return true;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException or ArgumentException)
			{
				DiagnosticLog.Write("ASSET_CACHE", $"Could not write {assetKind} cache for '{trackId}'", ex);
				return false;
			}
			finally
			{
				TryDeleteWithinBase(paths.BaseDirectory, payloadTemporary);
				TryDeleteWithinBase(paths.BaseDirectory, metadataTemporary);
			}
		}
	}

	public static AssetCacheStatistics GetStatistics()
	{
		(string baseDirectory, bool enabled, long maximumBytes) = SnapshotSettings();
		if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
		{
			return new AssetCacheStatistics(0, 0, maximumBytes, enabled);
		}
		try
		{
			if (!IsSafeCacheRoot(baseDirectory))
			{
				return new AssetCacheStatistics(0, 0, maximumBytes, enabled);
			}
			long bytes = 0;
			int files = 0;
			foreach (string path in EnumerateSafeFiles(baseDirectory, "*.payload"))
			{
				long length = new FileInfo(path).Length;
				bytes = length > long.MaxValue - bytes ? long.MaxValue : bytes + length;
				files = files == int.MaxValue ? int.MaxValue : files + 1;
			}
			return new AssetCacheStatistics(bytes, files, maximumBytes, enabled);
		}
		catch (Exception ex) when (IsExpectedFileSystemException(ex))
		{
			DiagnosticLog.Write("ASSET_CACHE", "Could not calculate cache size", ex);
			return new AssetCacheStatistics(0, 0, maximumBytes, enabled);
		}
	}

	public static void Clear()
	{
		(string baseDirectory, _, _) = SnapshotSettings();
		if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
		{
			return;
		}
		if (!IsSafeCacheRoot(baseDirectory))
		{
			DiagnosticLog.Write("ASSET_CACHE", "Refused to clear an unsafe or redirected cache root");
			return;
		}
		foreach (string pattern in new[] { "*.payload", "*.meta.json", "*.tmp" })
		{
			try
			{
				foreach (string path in EnumerateSafeFiles(baseDirectory, pattern))
				{
					TryDeleteWithinBase(baseDirectory, path);
				}
			}
			catch (Exception ex) when (IsExpectedFileSystemException(ex))
			{
				DiagnosticLog.Write("ASSET_CACHE", "Could not completely clear the local asset cache", ex);
			}
		}
	}

	private static bool TryResolveEntry(string assetKind, string trackId, out CacheEntryPaths paths)
	{
		paths = default;
		(string baseDirectory, bool enabled, _) = SnapshotSettings();
		if (!enabled || string.IsNullOrWhiteSpace(assetKind) || string.IsNullOrWhiteSpace(trackId) ||
			assetKind.Length > 64 || trackId.Length > 1024)
		{
			return false;
		}
		string safeKind = new(assetKind.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
		if (safeKind.Length == 0)
		{
			return false;
		}
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assetKind + "\n" + trackId))).ToLowerInvariant();
		string directory = Path.Combine(baseDirectory, safeKind, hash[..2]);
		string prefix = Path.Combine(directory, hash);
		paths = new CacheEntryPaths(baseDirectory, directory, prefix + ".payload", prefix + ".meta.json", prefix);
		return IsPathWithinBase(baseDirectory, paths.PayloadPath) && IsPathWithinBase(baseDirectory, paths.MetadataPath);
	}

	private static bool Matches(CachedAssetRef reference, AssetSourceStamp source)
	{
		if (reference.SourceLength != Math.Max(0, source.Length) ||
			reference.SourceLastWriteUtcTicks != Math.Max(0, source.LastWriteUtcTicks))
		{
			return false;
		}
		return string.IsNullOrWhiteSpace(source.Signature) ||
			string.Equals(reference.SourceSignature, source.Signature, StringComparison.Ordinal);
	}

	private static void TrimIfNeeded()
	{
		(string baseDirectory, bool enabled, long maximumBytes) = SnapshotSettings();
		DateTime now = DateTime.UtcNow;
		lock (SettingsGate)
		{
			if (!enabled || now < _nextTrimUtc)
			{
				return;
			}
			_nextTrimUtc = now.AddSeconds(30);
		}
		try
		{
			if (!IsSafeCacheRoot(baseDirectory))
			{
				return;
			}
			FileInfo[] files = EnumerateSafeFiles(baseDirectory, "*.payload")
				.Select(path => new FileInfo(path))
				.OrderBy(file => file.LastWriteTimeUtc)
				.ToArray();
			long total = 0;
			foreach (FileInfo file in files)
			{
				total = file.Length > long.MaxValue - total ? long.MaxValue : total + file.Length;
			}
			if (total <= maximumBytes)
			{
				return;
			}
			long target = maximumBytes * 9 / 10;
			foreach (FileInfo file in files)
			{
				if (total <= target)
				{
					break;
				}
				long length = file.Length;
				if (TryDeleteWithinBase(baseDirectory, file.FullName))
				{
					TryDeleteWithinBase(baseDirectory, Path.ChangeExtension(file.FullName, ".meta.json"));
					total -= length;
				}
			}
		}
		catch (Exception ex) when (IsExpectedFileSystemException(ex))
		{
			DiagnosticLog.Write("ASSET_CACHE", "Could not trim the local asset cache", ex);
		}
	}

	private static (string BaseDirectory, bool Enabled, long MaximumBytes) SnapshotSettings()
	{
		lock (SettingsGate)
		{
			return (_baseDirectory, _enabled, _maximumBytes);
		}
	}

	internal static bool IsSafeCachePathForTesting(string path)
	{
		(string baseDirectory, _, _) = SnapshotSettings();
		return !string.IsNullOrWhiteSpace(baseDirectory) && IsSafeCachePath(baseDirectory, path);
	}

	private static object GetEntryLock(string key)
	{
		int index = StringComparer.OrdinalIgnoreCase.GetHashCode(key) & (EntryLocks.Length - 1);
		return EntryLocks[index];
	}

	private static int GetPayloadLimit(string assetKind)
	{
		return assetKind.Equals("artwork", StringComparison.OrdinalIgnoreCase)
			? ContentReadLimits.ArtworkBytes
			: assetKind.Equals("lyrics", StringComparison.OrdinalIgnoreCase)
				? ContentReadLimits.CachedLyricsBytes
				: ContentReadLimits.GenericCachePayloadBytes;
	}

	private static IEnumerable<string> EnumerateSafeFiles(string baseDirectory, string pattern)
	{
		foreach (string path in Directory.EnumerateFiles(baseDirectory, pattern, CacheEnumerationOptions))
		{
			if (IsSafeCachePath(baseDirectory, path))
			{
				yield return path;
			}
		}
	}

	private static bool IsSafeCacheRoot(string baseDirectory)
	{
		try
		{
			return Directory.Exists(baseDirectory) &&
				(File.GetAttributes(baseDirectory) & FileAttributes.ReparsePoint) == 0;
		}
		catch (Exception ex) when (IsExpectedFileSystemException(ex))
		{
			return false;
		}
	}

	private static bool IsSafeCachePath(string baseDirectory, string path)
	{
		try
		{
			string normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
			string normalizedPath = Path.GetFullPath(path);
			if (!IsPathWithinBase(normalizedBase, normalizedPath))
			{
				return false;
			}
			if (Directory.Exists(normalizedBase) &&
				(File.GetAttributes(normalizedBase) & FileAttributes.ReparsePoint) != 0)
			{
				return false;
			}

			string? directory = Path.GetDirectoryName(normalizedPath);
			if (string.IsNullOrWhiteSpace(directory))
			{
				return false;
			}
			string relativeDirectory = Path.GetRelativePath(normalizedBase, directory);
			string current = normalizedBase;
			foreach (string component in relativeDirectory.Split(
				[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
				StringSplitOptions.RemoveEmptyEntries))
			{
				if (component == ".")
				{
					continue;
				}
				current = Path.Combine(current, component);
				if (Directory.Exists(current) &&
					(File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
				{
					return false;
				}
			}
			return !File.Exists(normalizedPath) ||
				(File.GetAttributes(normalizedPath) & FileAttributes.ReparsePoint) == 0;
		}
		catch (Exception ex) when (IsExpectedFileSystemException(ex))
		{
			return false;
		}
	}

	private static bool IsPathWithinBase(string baseDirectory, string path)
	{
		string normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
		string normalizedPath = Path.GetFullPath(path);
		string prefix = normalizedBase.EndsWith(Path.DirectorySeparatorChar) ||
			normalizedBase.EndsWith(Path.AltDirectorySeparatorChar)
			? normalizedBase
			: normalizedBase + Path.DirectorySeparatorChar;
		return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryDeleteWithinBase(string baseDirectory, string path)
	{
		try
		{
			if (!IsSafeCachePath(baseDirectory, path))
			{
				return false;
			}
			if (!File.Exists(path))
			{
				return true;
			}
			File.Delete(path);
			return true;
		}
		catch (Exception ex) when (IsExpectedFileSystemException(ex))
		{
			return false;
		}
	}

	private static bool IsExpectedFileSystemException(Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException or ArgumentException;
	}

	private readonly record struct CacheEntryPaths(
		string BaseDirectory,
		string Directory,
		string PayloadPath,
		string MetadataPath,
		string LockKey);
}
