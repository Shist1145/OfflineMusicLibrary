using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagLib;

namespace OfflineMusicLibrary;

public sealed class MusicLibraryService
{
	public static readonly HashSet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".mp3", ".flac", ".m4a", ".mp4", ".ogg", ".opus", ".wav", ".wma", ".aac", ".ape",
		".ncm", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".mpeg", ".mpg"
	};

	public static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".mpeg", ".mpg" };

	private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".agents", "$RECYCLE.BIN", "System Volume Information", "_重复文件待确认" };

	private static readonly Regex CircleLabelRegex = new Regex("(?:^|[;；|\\r\\n])\\s*(?:circle|社团|社團|サークル)\\s*[:：=]\\s*(?<name>[^;；|\\r\\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex NcmFileNameRegex = new Regex("^(?:\\d{1,3}(?:\\s*[.\\-_、]\\s*|\\s+))?(?<title>.+?)\\s+-\\s+(?<artist>.+)$", RegexOptions.Compiled);

	private readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks);

	public async Task<List<TrackModel>> ScanAsync(
		IReadOnlyCollection<string> roots,
		IReadOnlyCollection<TrackModel> existing,
		IProgress<ScanProgress>? progress = null,
		CancellationToken cancellationToken = default,
		bool forceMetadataRefresh = false)
	{
		List<string> configuredRoots = NormalizeRoots(roots);
		(List<string> availableRoots, List<string> protectedLocations, List<string> files, int scanParallelism) = await Task.Run(() =>
		{
			List<string> available = new();
			List<string> protectedRoots = new();
			foreach (string root in configuredRoots)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (Directory.Exists(root))
				{
					available.Add(root);
				}
				else
				{
					protectedRoots.Add(root);
				}
			}
			List<string> discovered = available
				.SelectMany(root => EnumerateMediaFiles(root, protectedRoots, cancellationToken))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			return (available, protectedRoots, discovered, ResolveScanParallelism(available));
		}, cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		Dictionary<string, TrackModel> existingById = existing.Where((TrackModel track) => !string.IsNullOrWhiteSpace(track.Id)).GroupBy<TrackModel, string>((TrackModel track) => track.Id, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, TrackModel>, string, TrackModel>((IGrouping<string, TrackModel> group) => group.Key, (IGrouping<string, TrackModel> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		ConcurrentBag<TrackModel> tracks = new ConcurrentBag<TrackModel>();
		ConcurrentDictionary<string, string> coverSidecars = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		int scanned = 0;
		int errors = 0;
		int reused = 0;
		int refreshed = 0;
		int added = 0;
		await Parallel.ForEachAsync(files, new ParallelOptions
		{
			MaxDegreeOfParallelism = scanParallelism,
			CancellationToken = cancellationToken
		}, delegate(string path, CancellationToken token)
		{
			try
			{
				token.ThrowIfCancellationRequested();
				string id = CreateTrackId(path);
				existingById.TryGetValue(id, out TrackModel? old);
				FileStamp stamp = ReadFileStamp(path);
				if (!forceMetadataRefresh && CanReuseMetadata(old, stamp))
				{
					tracks.Add(CloneCachedTrack(old!, path, stamp));
					Interlocked.Increment(ref reused);
				}
				else
				{
					TrackModel refreshedTrack = ReadTrack(path, id, old, stamp, coverSidecars);
					token.ThrowIfCancellationRequested();
					tracks.Add(refreshedTrack);
					if (old == null)
					{
						Interlocked.Increment(ref added);
					}
					else
					{
						Interlocked.Increment(ref refreshed);
					}
				}
			}
			catch (Exception exception) when (!token.IsCancellationRequested)
			{
				Interlocked.Increment(ref errors);
				try
				{
					string id = CreateTrackId(path);
					existingById.TryGetValue(id, out TrackModel? old);
					TrackModel fallback = ReadFallbackTrack(path, id, old, ReadFileStamp(path), coverSidecars);
					token.ThrowIfCancellationRequested();
					tracks.Add(fallback);
					if (old == null)
					{
						Interlocked.Increment(ref added);
					}
					else
					{
						Interlocked.Increment(ref refreshed);
					}
					DiagnosticLog.Write("LibraryScan", "元数据读取失败，已按文件名收录：" + path, exception);
				}
				catch (Exception exception2)
				{
					DiagnosticLog.Write("LibraryScan", "文件无法收录：" + path, exception2);
				}
			}
			finally
			{
				int num = Interlocked.Increment(ref scanned);
				if (num == 1 || num % 25 == 0 || num == files.Count)
				{
					progress?.Report(new ScanProgress(num, files.Count, path, errors));
				}
			}
			return ValueTask.CompletedTask;
		});
		cancellationToken.ThrowIfCancellationRequested();
		List<TrackModel> list = tracks.ToList();
		protectedLocations = protectedLocations.Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		List<TrackModel> existingInScannableLocations = existing.Where((TrackModel track) => IsPathWithinAnyRoot(track.FilePath, availableRoots) && !IsPathWithinAnyRoot(track.FilePath, protectedLocations)).ToList();
		RestoreMovedTrackState(list, existingInScannableLocations);
		int preserved = PreserveExistingTracks(list, existing, protectedLocations);
		if (preserved > 0)
		{
			DiagnosticLog.Write("LibraryScan", $"Preserved {preserved} existing tracks because {protectedLocations.Count} configured or nested locations were unavailable.");
		}
		DiagnosticLog.Write("LibraryScan", $"Incremental scan completed: files={files.Count}, reused={reused}, refreshed={refreshed}, added={added}, fallbacks={errors}, parallelism={scanParallelism}.");
		return list.OrderBy<TrackModel, string>((TrackModel track) => track.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy<TrackModel, string>((TrackModel track) => track.Album, StringComparer.CurrentCultureIgnoreCase).ThenBy((TrackModel track) => track.TrackNumber)
			.ThenBy<TrackModel, string>((TrackModel track) => track.Title, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
	}

	private static int ResolveScanParallelism(IReadOnlyCollection<string> roots)
	{
		foreach (string root in roots)
		{
			try
			{
				string? driveRoot = Path.GetPathRoot(root);
				if (string.IsNullOrWhiteSpace(driveRoot))
				{
					continue;
				}
				DriveType driveType = new DriveInfo(driveRoot).DriveType;
				if (driveType is DriveType.Network or DriveType.Removable or DriveType.CDRom)
				{
					return 1;
				}
			}
			catch
			{
			}
		}
		return Math.Min(2, Math.Max(1, Environment.ProcessorCount));
	}

	private static FileStamp ReadFileStamp(string path)
	{
		FileInfo info = new FileInfo(path);
		return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
	}

	private static bool CanReuseMetadata(TrackModel? old, FileStamp stamp)
	{
		if (old == null)
		{
			return false;
		}
		if (old.FileSize == 0 && old.LastWriteTimeUtcTicks == 0)
		{
			return true;
		}
		return old.FileSize == stamp.Length && old.LastWriteTimeUtcTicks == stamp.LastWriteTimeUtcTicks;
	}

	private static TrackModel CloneCachedTrack(TrackModel old, string path, FileStamp stamp)
	{
		return new TrackModel
		{
			Id = old.Id,
			FilePath = path,
			FileSize = stamp.Length,
			LastWriteTimeUtcTicks = stamp.LastWriteTimeUtcTicks,
			Title = old.Title,
			Artist = old.Artist,
			Album = old.Album,
			AlbumArtist = old.AlbumArtist,
			Circle = old.Circle,
			CircleIsManual = old.CircleIsManual,
			Genre = old.Genre,
			Format = old.Format,
			Year = old.Year,
			TrackNumber = old.TrackNumber,
			DurationMs = old.DurationMs,
			HasCover = old.HasCover,
			HasLyrics = old.HasLyrics,
			IsVideo = old.IsVideo,
			PlayCount = old.PlayCount,
			LastPlayedAt = old.LastPlayedAt,
			AddedAt = old.AddedAt,
			Categories = old.Categories?.ToList() ?? new List<string>(),
			CloudId = old.CloudId,
			CloudIds = old.CloudIds?.ToList() ?? new List<string>(),
			IsFavorite = old.IsFavorite
		};
	}

	private static List<string> NormalizeRoots(IEnumerable<string> roots)
	{
		List<string> normalized = new List<string>();
		HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string root in roots)
		{
			if (string.IsNullOrWhiteSpace(root))
			{
				continue;
			}
			string value = NormalizePath(root);
			if (seen.Add(value))
			{
				normalized.Add(value);
			}
		}
		return normalized;
	}

	private static int PreserveExistingTracks(List<TrackModel> scanned, IReadOnlyCollection<TrackModel> existing, IReadOnlyCollection<string> protectedLocations)
	{
		if (protectedLocations.Count == 0)
		{
			return 0;
		}
		HashSet<string> ids = scanned.Where((TrackModel track) => !string.IsNullOrWhiteSpace(track.Id)).Select((TrackModel track) => track.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> paths = scanned.Where((TrackModel track) => !string.IsNullOrWhiteSpace(track.FilePath)).Select((TrackModel track) => NormalizePath(track.FilePath)).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		int preserved = 0;
		foreach (TrackModel track in existing)
		{
			if (string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.FilePath) || !IsPathWithinAnyRoot(track.FilePath, protectedLocations))
			{
				continue;
			}
			string path = NormalizePath(track.FilePath);
			if (ids.Contains(track.Id) || paths.Contains(path))
			{
				continue;
			}
			ids.Add(track.Id);
			paths.Add(path);
			scanned.Add(track);
			preserved++;
		}
		return preserved;
	}

	private static bool IsPathWithinAnyRoot(string? path, IReadOnlyCollection<string> roots)
	{
		return !string.IsNullOrWhiteSpace(path) && roots.Any((string root) => IsPathWithinRoot(path, root));
	}

	private static bool IsPathWithinRoot(string path, string root)
	{
		try
		{
			string normalizedPath = NormalizePath(path);
			string normalizedRoot = NormalizePath(root);
			if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (!Path.EndsInDirectorySeparator(normalizedRoot))
			{
				normalizedRoot += Path.DirectorySeparatorChar;
			}
			return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}

	private static string NormalizePath(string path)
	{
		try
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
		}
		catch
		{
			return path.Trim();
		}
	}

	private static void RestoreMovedTrackState(List<TrackModel> scanned, IReadOnlyCollection<TrackModel> existing)
	{
		HashSet<string> scannedIds = scanned.Select((TrackModel track) => track.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> existingIds = existing.Select((TrackModel track) => track.Id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<TrackModel> list = (from track in existing
			where !scannedIds.Contains(track.Id)
			orderby track.HasCloudIds descending, track.IsFavorite || track.Categories.Count > 0 descending, track.PlayCount descending
			select track).ToList();
		List<TrackModel> newCandidates = scanned.Where((TrackModel track) => !existingIds.Contains(track.Id)).ToList();
		HashSet<TrackModel> claimed = new HashSet<TrackModel>();
		foreach (TrackModel oldTrack in list)
		{
			var ranked = (from track in newCandidates
				where !claimed.Contains(track)
				select new
				{
					Track = track,
					Score = MovedTrackScore(oldTrack, track)
				} into item
				where item.Score >= 100
				orderby item.Score descending, item.Track.IsEncryptedNcm
				select item).ToList();
			if (ranked.Count != 0 && (ranked.Count <= 1 || ranked[0].Score != ranked[1].Score))
			{
				TrackModel replacement = ranked[0].Track;
				claimed.Add(replacement);
				replacement.Id = oldTrack.Id;
				replacement.IsFavorite = oldTrack.IsFavorite;
				replacement.PlayCount = oldTrack.PlayCount;
				replacement.LastPlayedAt = oldTrack.LastPlayedAt;
				replacement.AddedAt = oldTrack.AddedAt;
				replacement.Categories = oldTrack.Categories.ToList();
				replacement.CloudId = oldTrack.CloudId;
				replacement.CloudIds = oldTrack.CloudIds?.ToList() ?? new List<string>();
				if (oldTrack.CircleIsManual)
				{
					replacement.Circle = oldTrack.Circle;
					replacement.CircleIsManual = true;
				}
			}
		}
	}

	private static int MovedTrackScore(TrackModel oldTrack, TrackModel newTrack)
	{
		string oldTitle = NormalizeIdentityText(oldTrack.Title);
		string newTitle = NormalizeIdentityText(newTrack.Title);
		string oldFileName = NormalizeIdentityText(Path.GetFileNameWithoutExtension(oldTrack.FilePath));
		string newFileName = NormalizeIdentityText(Path.GetFileNameWithoutExtension(newTrack.FilePath));
		bool titleMatches = oldTitle.Length > 0 && oldTitle == newTitle;
		bool fileNameMatches = oldFileName.Length > 0 && oldFileName == newFileName;
		if (!titleMatches && !fileNameMatches)
		{
			return 0;
		}
		if (oldTrack.DurationMs > 0 && newTrack.DurationMs > 0 && Math.Abs(oldTrack.DurationMs - newTrack.DurationMs) > 3000)
		{
			return 0;
		}
		int score = (titleMatches ? 100 : 0);
		score += (fileNameMatches ? 80 : 0);
		if (oldTrack.DurationMs > 0 && newTrack.DurationMs > 0)
		{
			score += ((Math.Abs(oldTrack.DurationMs - newTrack.DurationMs) <= 1200) ? 35 : 20);
		}
		if (NormalizeIdentityText(oldTrack.Artist) == NormalizeIdentityText(newTrack.Artist))
		{
			score += 28;
		}
		if (NormalizeIdentityText(oldTrack.Album) == NormalizeIdentityText(newTrack.Album))
		{
			score += 18;
		}
		if (oldTrack.TrackNumber != 0 && oldTrack.TrackNumber == newTrack.TrackNumber)
		{
			score += 12;
		}
		return score;
	}

	private static string NormalizeIdentityText(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		string text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
		StringBuilder builder = new StringBuilder(text.Length);
		string text2 = text;
		foreach (char character in text2)
		{
			bool flag = char.IsLetterOrDigit(character);
			if (!flag)
			{
				UnicodeCategory unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(character);
				bool flag2 = ((unicodeCategory == UnicodeCategory.LetterNumber || unicodeCategory == UnicodeCategory.MathSymbol || unicodeCategory == UnicodeCategory.OtherSymbol) ? true : false);
				flag = flag2;
			}
			if (flag)
			{
				builder.Append(character);
			}
		}
		return builder.ToString();
	}

	public static TrackModel ReadTrack(string path, TrackModel? old = null)
	{
		return ReadTrack(path, CreateTrackId(path), old, ReadFileStamp(path), null);
	}

	private static TrackModel ReadTrack(
		string path,
		string id,
		TrackModel? old,
		FileStamp stamp,
		ConcurrentDictionary<string, string>? coverSidecars)
	{
		if (string.Equals(Path.GetExtension(path), ".ncm", StringComparison.OrdinalIgnoreCase))
		{
			return ReadNcmTrack(path, id, old, stamp, coverSidecars);
		}
		using TagLib.File file = TagLib.File.Create(path);
		Tag tag = file.Tag;
		string title = (string.IsNullOrWhiteSpace(tag.Title) ? Path.GetFileNameWithoutExtension(path) : tag.Title.Trim());
		string artist = FirstNonEmpty(tag.FirstPerformer, tag.FirstAlbumArtist, "未知艺术家");
		string album = FirstNonEmpty(tag.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑");
		string circle = ((old != null && old.CircleIsManual) ? old.Circle : InferCircle(tag));
		bool hasLyrics = LyricsService.FindMainLyricsPath(path) != null;
		TrackModel trackModel = new TrackModel();
		trackModel.Id = id;
		trackModel.FilePath = path;
		trackModel.FileSize = stamp.Length;
		trackModel.LastWriteTimeUtcTicks = stamp.LastWriteTimeUtcTicks;
		trackModel.Title = title;
		trackModel.Artist = artist;
		trackModel.AlbumArtist = FirstNonEmpty(tag.FirstAlbumArtist, artist);
		trackModel.Album = album;
		trackModel.Circle = circle;
		trackModel.CircleIsManual = old?.CircleIsManual ?? false;
		trackModel.Genre = FirstNonEmpty(tag.FirstGenre, "");
		trackModel.Year = (int)tag.Year;
		trackModel.TrackNumber = tag.Track;
		trackModel.DurationMs = (long)file.Properties.Duration.TotalMilliseconds;
		trackModel.Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
		trackModel.HasCover = tag.Pictures.Length != 0 || FindCoverSidecar(path, coverSidecars) != null;
		trackModel.HasLyrics = hasLyrics;
		trackModel.IsVideo = VideoExtensions.Contains(Path.GetExtension(path));
		trackModel.PlayCount = old?.PlayCount ?? 0;
		trackModel.LastPlayedAt = old?.LastPlayedAt;
		trackModel.AddedAt = old?.AddedAt ?? System.IO.File.GetCreationTime(path);
		trackModel.Categories = old?.Categories.ToList() ?? new List<string>();
		trackModel.CloudId = old?.CloudId;
		trackModel.CloudIds = old?.CloudIds?.ToList() ?? new List<string>();
		trackModel.IsFavorite = old?.IsFavorite ?? false;
		return trackModel;
	}

	private static TrackModel ReadNcmTrack(
		string path,
		string id,
		TrackModel? old,
		FileStamp stamp,
		ConcurrentDictionary<string, string>? coverSidecars)
	{
		string fileName = Path.GetFileNameWithoutExtension(path).Trim();
		Match match = NcmFileNameRegex.Match(fileName);
		string title = (match.Success ? match.Groups["title"].Value.Trim() : fileName);
		string artist = (match.Success ? match.Groups["artist"].Value.Trim() : "未知艺术家");
		string album = FirstNonEmpty(old?.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑");
		TrackModel trackModel = new TrackModel();
		trackModel.Id = id;
		trackModel.FilePath = path;
		trackModel.FileSize = stamp.Length;
		trackModel.LastWriteTimeUtcTicks = stamp.LastWriteTimeUtcTicks;
		trackModel.Title = title;
		trackModel.Artist = artist;
		trackModel.AlbumArtist = FirstNonEmpty(old?.AlbumArtist, artist);
		trackModel.Album = album;
		trackModel.Circle = old?.Circle ?? "";
		trackModel.CircleIsManual = old?.CircleIsManual ?? false;
		trackModel.Genre = old?.Genre ?? "";
		trackModel.Year = old?.Year ?? 0;
		trackModel.TrackNumber = old?.TrackNumber ?? 0;
		trackModel.DurationMs = old?.DurationMs ?? 0;
		trackModel.Format = "NCM";
		trackModel.HasCover = FindCoverSidecar(path, coverSidecars) != null;
		trackModel.HasLyrics = LyricsService.FindMainLyricsPath(path) != null;
		trackModel.IsVideo = false;
		trackModel.PlayCount = old?.PlayCount ?? 0;
		trackModel.LastPlayedAt = old?.LastPlayedAt;
		trackModel.AddedAt = old?.AddedAt ?? System.IO.File.GetCreationTime(path);
		trackModel.Categories = old?.Categories.ToList() ?? new List<string>();
		trackModel.CloudId = old?.CloudId;
		trackModel.CloudIds = old?.CloudIds?.ToList() ?? new List<string>();
		trackModel.IsFavorite = old?.IsFavorite ?? false;
		return trackModel;
	}

	private static TrackModel ReadFallbackTrack(
		string path,
		string id,
		TrackModel? old,
		FileStamp stamp,
		ConcurrentDictionary<string, string>? coverSidecars)
	{
		string fileName = Path.GetFileNameWithoutExtension(path).Trim();
		Match match = NcmFileNameRegex.Match(fileName);
		string title = (match.Success ? match.Groups["title"].Value.Trim() : fileName);
		string artist = (match.Success ? match.Groups["artist"].Value.Trim() : FirstNonEmpty(old?.Artist, "未知艺术家"));
		string extension = Path.GetExtension(path);
		TrackModel trackModel = new TrackModel();
		trackModel.Id = id;
		trackModel.FilePath = path;
		trackModel.FileSize = stamp.Length;
		trackModel.LastWriteTimeUtcTicks = stamp.LastWriteTimeUtcTicks;
		trackModel.Title = FirstNonEmpty(old?.Title, title, fileName);
		trackModel.Artist = artist;
		trackModel.AlbumArtist = FirstNonEmpty(old?.AlbumArtist, artist);
		trackModel.Album = FirstNonEmpty(old?.Album, Path.GetFileName(Path.GetDirectoryName(path)), "未知专辑");
		trackModel.Circle = old?.Circle ?? "";
		trackModel.CircleIsManual = old?.CircleIsManual ?? false;
		trackModel.Genre = old?.Genre ?? "";
		trackModel.Year = old?.Year ?? 0;
		trackModel.TrackNumber = old?.TrackNumber ?? 0;
		trackModel.DurationMs = old?.DurationMs ?? 0;
		trackModel.Format = extension.TrimStart('.').ToUpperInvariant();
		trackModel.HasCover = SafeHasCover(path, coverSidecars);
		trackModel.HasLyrics = SafeHasLyrics(path);
		trackModel.IsVideo = VideoExtensions.Contains(extension);
		trackModel.PlayCount = old?.PlayCount ?? 0;
		trackModel.LastPlayedAt = old?.LastPlayedAt;
		trackModel.AddedAt = old?.AddedAt ?? SafeGetCreationTime(path);
		trackModel.Categories = old?.Categories.ToList() ?? new List<string>();
		trackModel.CloudId = old?.CloudId;
		trackModel.CloudIds = old?.CloudIds?.ToList() ?? new List<string>();
		trackModel.IsFavorite = old?.IsFavorite ?? false;
		return trackModel;
	}

	private static string? FindCoverSidecar(string path, ConcurrentDictionary<string, string>? coverSidecars)
	{
		if (coverSidecars == null)
		{
			return CoverService.FindSidecar(path);
		}
		string? directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory))
		{
			return null;
		}
		string cached = coverSidecars.GetOrAdd(directory, _ => CoverService.FindSidecar(path) ?? string.Empty);
		return cached.Length == 0 ? null : cached;
	}

	private static bool SafeHasCover(string path, ConcurrentDictionary<string, string>? coverSidecars)
	{
		try
		{
			return FindCoverSidecar(path, coverSidecars) != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool SafeHasLyrics(string path)
	{
		try
		{
			return LyricsService.FindMainLyricsPath(path) != null;
		}
		catch
		{
			return false;
		}
	}

	private static DateTime SafeGetCreationTime(string path)
	{
		try
		{
			return System.IO.File.GetCreationTime(path);
		}
		catch
		{
			return DateTime.Now;
		}
	}

	private static string FirstNonEmpty(params string?[] values)
	{
		return values.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
	}

	private static string InferCircle(Tag tag)
	{
		string labeled = ExtractLabeledCircle(tag.Comment);
		if (labeled.Length > 0)
		{
			return labeled;
		}
		labeled = ExtractLabeledCircle(tag.Grouping);
		if (labeled.Length > 0)
		{
			return labeled;
		}
		List<string> albumArtists = (from value in tag.AlbumArtists
			where !string.IsNullOrWhiteSpace(value) && !IsGenericArtist(value)
			select value.Trim()).Distinct<string>(StringComparer.CurrentCultureIgnoreCase).ToList();
		if (albumArtists.Count > 0)
		{
			return string.Join(" / ", albumArtists);
		}
		string grouping = tag.Grouping?.Trim() ?? "";
		if (!IsGenericArtist(grouping))
		{
			return grouping;
		}
		return "";
	}

	private static string ExtractLabeledCircle(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "";
		}
		Match match = CircleLabelRegex.Match(value);
		if (!match.Success)
		{
			return "";
		}
		return match.Groups["name"].Value.Trim();
	}

	private static bool IsGenericArtist(string value)
	{
		string normalized = Regex.Replace(value.Trim().ToLowerInvariant(), "[^\\p{L}\\p{N}]", "");
		bool flag = normalized.Length == 0;
		if (!flag)
		{
			bool flag2;
			switch (normalized)
			{
			case "va":
			case "variousartists":
			case "unknownartist":
			case "未知艺术家":
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		return flag;
	}

	private static IEnumerable<string> EnumerateMediaFiles(
		string root,
		ICollection<string>? failedDirectories = null,
		CancellationToken cancellationToken = default)
	{
		Stack<string> pending = new Stack<string>();
		HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		pending.Push(root);
		while (pending.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string directory;
			try
			{
				directory = Path.GetFullPath(pending.Pop());
			}
			catch (Exception ex) when (IsExpectedEnumerationException(ex))
			{
				continue;
			}
			if (!visited.Add(directory))
			{
				continue;
			}
			string[] childDirectories;
			string[] files;
			try
			{
				childDirectories = Directory.GetDirectories(directory);
				files = Directory.GetFiles(directory);
			}
			catch (Exception ex) when (IsExpectedEnumerationException(ex))
			{
				failedDirectories?.Add(directory);
				DiagnosticLog.Write("LibraryScan", "无法枚举目录：" + directory, ex);
				continue;
			}
			string[] array = childDirectories;
			foreach (string child in array)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!SkippedDirectories.Contains(Path.GetFileName(child)))
				{
					try
					{
						if ((System.IO.File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
						{
							pending.Push(child);
						}
						else
						{
							failedDirectories?.Add(child);
						}
					}
					catch (Exception ex) when (IsExpectedEnumerationException(ex))
					{
						failedDirectories?.Add(child);
					}
				}
			}
			string[] array2 = files;
			foreach (string file in array2)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (SupportedExtensions.Contains(Path.GetExtension(file)) && !IsReparsePoint(file))
				{
					yield return file;
				}
				else if (SupportedExtensions.Contains(Path.GetExtension(file)))
				{
					failedDirectories?.Add(file);
				}
			}
		}
	}

	private static bool IsReparsePoint(string path)
	{
		try
		{
			return (System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception ex) when (IsExpectedEnumerationException(ex))
		{
			return true;
		}
	}

	private static bool IsExpectedEnumerationException(Exception exception)
	{
		return exception is IOException or UnauthorizedAccessException or NotSupportedException or
			SecurityException or ArgumentException;
	}

	public static string CreateTrackId(string path)
	{
		string normalized = Path.GetFullPath(path).ToUpperInvariant();
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).Substring(0, 24);
	}
}
