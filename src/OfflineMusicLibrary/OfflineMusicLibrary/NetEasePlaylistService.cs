using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineMusicLibrary;

public sealed partial class NetEasePlaylistService
{
	private sealed record MatchResult(IReadOnlyList<TrackModel> Tracks, HashSet<string> MatchedRemoteIds, int ExactCount, int FuzzyCount);

	private sealed class LocalMatchCandidate
	{
		public TrackModel Track { get; }

		public IReadOnlyList<string> TitleVariants { get; }

		public string Artist { get; }

		public string Album { get; }

		private LocalMatchCandidate(TrackModel track, IReadOnlyList<string> titleVariants)
		{
			Track = track;
			TitleVariants = titleVariants;
			Artist = track.Artist + " / " + track.AlbumArtist;
			Album = track.Album;
		}

		public static LocalMatchCandidate Create(TrackModel track)
		{
			string[] values = new string[2]
			{
				track.Title,
				Path.GetFileNameWithoutExtension(track.FilePath)
			};
			return new LocalMatchCandidate(track, BuildTitleVariants(values));
		}
	}

	private const int TrackDetailBatchSize = 100;

	private const int SmallRetryBatchSize = 25;

	private const int PlaylistRequestAttempts = 3;

	private readonly HttpClient _httpClient;

	private static readonly HashSet<char> MeaningfulTitleSymbols = new HashSet<char>
	{
		'△', '▽', '▲', '▼', '○', '●', '◎', '◇', '◆', '□',
		'■', '☆', '★', '∞', '∴', '∵', '※', '＊', '♪', '♫',
		'♬', '♭', '♯', '＋', '−', '×', '÷', '＝', '≠', '≈',
		'≡', 'Ⅰ', 'Ⅱ', 'Ⅲ', 'Ⅳ', 'Ⅴ', 'Ⅵ', 'Ⅶ', 'Ⅷ', 'Ⅸ',
		'Ⅹ', 'Ⅺ', 'Ⅻ'
	};

	public NetEasePlaylistService()
		: this(CreateHttpClient())
	{
	}

	public NetEasePlaylistService(HttpClient httpClient)
	{
		_httpClient = httpClient;
		if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
		{
			_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 OfflineMusicLibrary/1.2");
		}
		HttpRequestHeaders defaultRequestHeaders = _httpClient.DefaultRequestHeaders;
		if ((object)defaultRequestHeaders.Referrer == null)
		{
			Uri uri = (defaultRequestHeaders.Referrer = new Uri("https://music.163.com/"));
		}
	}

	public async Task<NetEaseImportResult> ImportAsync(string source, IReadOnlyList<TrackModel> localTracks, CancellationToken cancellationToken = default(CancellationToken))
	{
		string playlistId = ExtractPlaylistId(source) ?? throw new InvalidOperationException("无法识别网易云歌单 ID。");
		using JsonDocument document = await FetchJsonWithRetriesAsync("https://music.163.com/api/v6/playlist/detail?id=" + playlistId + "&n=10000&s=0", cancellationToken);
		if (!document.RootElement.TryGetProperty("playlist", out var playlist) || playlist.ValueKind != JsonValueKind.Object)
		{
			throw new InvalidOperationException("网易云未返回可访问的公开歌单，请确认链接及歌单权限。");
		}
		string playlistName = GetString(playlist, "name") ?? ("网易云歌单 " + playlistId);
		int declaredTrackCount = ReadInt(playlist, "trackCount");
		List<string> trackIds = ReadTrackIds(playlist);
		List<NetEaseTrack> embeddedTracks = ReadTracks(playlist);
		HashSet<string> embeddedIds = (from track in embeddedTracks
			select track.Id into id
			where !string.IsNullOrWhiteSpace(id)
			select id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<string> missingDetailIds = trackIds.Where((string id) => !embeddedIds.Contains(id)).ToList();
		List<NetEaseTrack> list = ((missingDetailIds.Count != 0) ? (await FetchTracksByIdsAsync(missingDetailIds, cancellationToken)) : new List<NetEaseTrack>());
		List<NetEaseTrack> fetchedTracks = list;
		List<NetEaseTrack> remoteTracks = MergeTracks(trackIds, embeddedTracks, fetchedTracks);
		HashSet<string> resolvedIds = (from track in embeddedTracks.Concat(fetchedTracks)
			select track.Id into id
			where !string.IsNullOrWhiteSpace(id)
			select id).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<string> unresolvedTrackIds = trackIds.Where((string id) => !resolvedIds.Contains(id)).ToList();
		MatchResult matchResult = await Task.Run(() => MatchTracks(remoteTracks, localTracks), cancellationToken);
		List<NetEaseTrack> missing = remoteTracks.Where((NetEaseTrack track) => !matchResult.MatchedRemoteIds.Contains(track.Id)).ToList();
		int declared = Math.Max(declaredTrackCount, Math.Max(trackIds.Count, remoteTracks.Count));
		DiagnosticLog.Write("NetEaseImport", $"歌单={playlistName}({playlistId})，声明={declared}，ID={trackIds.Count}，详情={resolvedIds.Count}，精确={matchResult.ExactCount}，模糊={matchResult.FuzzyCount}，未匹配={missing.Count}");
		return new NetEaseImportResult(playlistName, playlistId, declared, remoteTracks, matchResult.Tracks, missing)
		{
			TrackIdCount = trackIds.Count,
			ResolvedTrackCount = resolvedIds.Count,
			ExactMatchCount = matchResult.ExactCount,
			FuzzyMatchCount = matchResult.FuzzyCount,
			UnresolvedTrackIds = unresolvedTrackIds,
			RemoteTrackIds = trackIds
		};
	}

	public static string? ExtractPlaylistId(string source)
	{
		string trimmed = source.Trim();
		if (trimmed.All(char.IsDigit) && trimmed.Length > 0)
		{
			return trimmed;
		}
		Match match = PlaylistIdRegex().Match(trimmed);
		if (match.Success)
		{
			return match.Groups[1].Value;
		}
		match = StandaloneLongNumberRegex().Match(trimmed);
		if (!match.Success)
		{
			return null;
		}
		return match.Groups[1].Value;
	}

	private static HttpClient CreateHttpClient()
	{
		return new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(20L)
		};
	}

	private async Task<JsonDocument> FetchJsonWithRetriesAsync(string url, CancellationToken cancellationToken)
	{
		Exception lastException = null;
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			try
			{
				using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
				response.EnsureSuccessStatusCode();
				JsonDocument result;
				await using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken))
				{
					CancellationToken cancellationToken2 = cancellationToken;
					result = await JsonDocument.ParseAsync(stream, default(JsonDocumentOptions), cancellationToken2);
				}
				return result;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex2)
			{
				lastException = ex2;
				DiagnosticLog.Write("NetEaseImport", $"歌单请求失败（第 {attempt} 次）：{url}", ex2);
				if (attempt < 3)
				{
					await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
				}
			}
		}
		throw new InvalidOperationException("网易云歌单请求连续失败，请稍后重试。", lastException);
	}

	private static List<string> ReadTrackIds(JsonElement playlist)
	{
		if (!playlist.TryGetProperty("trackIds", out var trackIds) || trackIds.ValueKind != JsonValueKind.Array)
		{
			return new List<string>();
		}
		List<string> result = new List<string>();
		HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (JsonElement item in trackIds.EnumerateArray())
		{
			JsonElement idElement;
			string id = ((item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out idElement)) ? idElement.ToString() : item.ToString());
			if (!string.IsNullOrWhiteSpace(id) && known.Add(id))
			{
				result.Add(id);
			}
		}
		return result;
	}

	private async Task<List<NetEaseTrack>> FetchTracksByIdsAsync(IReadOnlyList<string> trackIds, CancellationToken cancellationToken)
	{
		Dictionary<string, NetEaseTrack> result = new Dictionary<string, NetEaseTrack>(StringComparer.OrdinalIgnoreCase);
		foreach (string[] batch in trackIds.Chunk(100))
		{
			List<NetEaseTrack> fetched = await FetchTrackGroupAsync(batch, 3, cancellationToken);
			foreach (NetEaseTrack track in fetched)
			{
				result[track.Id] = track;
			}
			string[] unresolved = batch.Where((string id) => !result.ContainsKey(id)).ToArray();
			if (fetched.Count == 0 || unresolved.Length == 0)
			{
				continue;
			}
			foreach (string[] smallBatch in unresolved.Chunk(25))
			{
				foreach (NetEaseTrack track2 in await FetchTrackGroupAsync(smallBatch, 2, cancellationToken))
				{
					result[track2.Id] = track2;
				}
			}
		}
		return (from id in trackIds.Where(result.ContainsKey)
			select result[id]).ToList();
	}

	private async Task<List<NetEaseTrack>> FetchTrackGroupAsync(IReadOnlyList<string> trackIds, int attempts, CancellationToken cancellationToken)
	{
		Dictionary<string, NetEaseTrack> result = new Dictionary<string, NetEaseTrack>(StringComparer.OrdinalIgnoreCase);
		for (int attempt = 1; attempt <= attempts; attempt++)
		{
			string[] missing = trackIds.Where((string id) => !result.ContainsKey(id)).ToArray();
			if (missing.Length == 0)
			{
				break;
			}
			string ids = Uri.EscapeDataString("[" + string.Join(",", missing) + "]");
			IDictionary<string, NetEaseTrack> destination = result;
			AddTracks(destination, await FetchTrackBatchOnceAsync("https://music.163.com/api/song/detail?ids=" + ids, cancellationToken));
			missing = trackIds.Where((string id) => !result.ContainsKey(id)).ToArray();
			if (missing.Length != 0)
			{
				string payload = Uri.EscapeDataString("[" + string.Join(",", missing.Select((string id) => "{\"id\":" + id + ",\"v\":0}")) + "]");
				destination = result;
				AddTracks(destination, await FetchTrackBatchOnceAsync("https://music.163.com/api/v3/song/detail?c=" + payload, cancellationToken));
			}
			if (result.Count < trackIds.Count && attempt < attempts)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
			}
		}
		return (from id in trackIds.Where(result.ContainsKey)
			select result[id]).ToList();
	}

	private static void AddTracks(IDictionary<string, NetEaseTrack> destination, IEnumerable<NetEaseTrack> tracks)
	{
		foreach (NetEaseTrack track in tracks)
		{
			if (!string.IsNullOrWhiteSpace(track.Id))
			{
				destination[track.Id] = track;
			}
		}
	}

	private async Task<List<NetEaseTrack>> FetchTrackBatchOnceAsync(string url, CancellationToken cancellationToken)
	{
		_ = 3;
		try
		{
			using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
			response.EnsureSuccessStatusCode();
			List<NetEaseTrack> result;
			await using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken))
			{
				CancellationToken cancellationToken2 = cancellationToken;
				using JsonDocument document = await JsonDocument.ParseAsync(stream, default(JsonDocumentOptions), cancellationToken2);
				result = ((document.RootElement.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array) ? songs.EnumerateArray().Select(ReadTrack).ToList() : new List<NetEaseTrack>());
			}
			return result;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("NetEaseImport", "歌曲详情请求失败：" + url, exception);
			return new List<NetEaseTrack>();
		}
	}

	private static List<NetEaseTrack> MergeTracks(IReadOnlyList<string> trackIds, IReadOnlyList<NetEaseTrack> embeddedTracks, IReadOnlyList<NetEaseTrack> fetchedTracks)
	{
		if (trackIds.Count == 0)
		{
			return embeddedTracks.ToList();
		}
		Dictionary<string, NetEaseTrack> byId = (from track in embeddedTracks.Concat(fetchedTracks)
			where !string.IsNullOrWhiteSpace(track.Id)
			select track).GroupBy<NetEaseTrack, string>((NetEaseTrack track) => track.Id, StringComparer.OrdinalIgnoreCase).ToDictionary<IGrouping<string, NetEaseTrack>, string, NetEaseTrack>((IGrouping<string, NetEaseTrack> group) => group.Key, (IGrouping<string, NetEaseTrack> group) => group.Last(), StringComparer.OrdinalIgnoreCase);
		return trackIds.Select((string id) => (!byId.TryGetValue(id, out var value)) ? new NetEaseTrack(id, "", "", "") : value).ToList();
	}

	private static List<NetEaseTrack> ReadTracks(JsonElement playlist)
	{
		if (!playlist.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
		{
			return new List<NetEaseTrack>();
		}
		return tracks.EnumerateArray().Select(ReadTrack).ToList();
	}

	private static NetEaseTrack ReadTrack(JsonElement track)
	{
		JsonElement idElement;
		string id = (track.TryGetProperty("id", out idElement) ? idElement.ToString() : "");
		string title = GetString(track, "name") ?? "未知歌曲";
		string artists = ReadArtistNames(track);
		string album = ReadAlbumName(track);
		return new NetEaseTrack(id, title, artists, album);
	}

	private static MatchResult MatchTracks(IReadOnlyList<NetEaseTrack> remote, IReadOnlyList<TrackModel> local)
	{
		List<LocalMatchCandidate> candidates = local.Select(LocalMatchCandidate.Create).ToList();
		Dictionary<string, List<TrackModel>> byCloudId = local.SelectMany((TrackModel track) => from id in track.GetCloudIds()
			select new
			{
				Id = id,
				Track = track
			}).GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Select(item => item.Track).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
		TrackModel[] assignments = new TrackModel[remote.Count];
		bool[] exactAssignments = new bool[remote.Count];
		HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int index = 0; index < remote.Count; index++)
		{
			NetEaseTrack remoteTrack = remote[index];
			if (byCloudId.TryGetValue(remoteTrack.Id, out var idMatches))
			{
				TrackModel exact = (from track in idMatches
					where !used.Contains(track.Id)
					select new
					{
						Track = track,
						Score = (string.IsNullOrWhiteSpace(remoteTrack.Title) ? 1 : MatchScore(LocalMatchCandidate.Create(track), remoteTrack))
					} into item
					where item.Score > 0
					orderby item.Track.IsEncryptedNcm, item.Score descending
					select item.Track).FirstOrDefault();
				if (exact != null)
				{
					assignments[index] = exact;
					exactAssignments[index] = true;
					used.Add(exact.Id);
				}
			}
		}
		int index2;
		for (index2 = 0; index2 < remote.Count; index2++)
		{
			TrackModel existing = assignments[index2];
			if (existing != null && !existing.IsEncryptedNcm)
			{
				continue;
			}
			var ranked = (from candidate in candidates
				where !used.Contains(candidate.Track.Id)
				select new
				{
					Track = candidate.Track,
					Score = MatchScore(candidate, remote[index2])
				} into item
				where item.Score > 0
				orderby item.Score descending
				select item).ToList();
			if (ranked.Count == 0)
			{
				continue;
			}
			var best = ranked[0];
			TrackModel fuzzy = ranked.FirstOrDefault(item => !item.Track.IsEncryptedNcm && item.Score >= best.Score - 140)?.Track ?? best.Track;
			if (existing == null || !fuzzy.IsEncryptedNcm)
			{
				if (existing != null)
				{
					used.Remove(existing.Id);
				}
				assignments[index2] = fuzzy;
				exactAssignments[index2] = false;
				used.Add(fuzzy.Id);
			}
		}
		List<TrackModel> tracks = new List<TrackModel>();
		HashSet<string> matchedRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int exactCount = 0;
		int fuzzyCount = 0;
		for (int index3 = 0; index3 < assignments.Length; index3++)
		{
			TrackModel match = assignments[index3];
			if (match != null)
			{
				match.RememberCloudId(remote[index3].Id);
				tracks.Add(match);
				matchedRemoteIds.Add(remote[index3].Id);
				if (exactAssignments[index3])
				{
					exactCount++;
				}
				else
				{
					fuzzyCount++;
				}
			}
		}
		return new MatchResult(tracks, matchedRemoteIds, exactCount, fuzzyCount);
	}

	private static int MatchScore(LocalMatchCandidate candidate, NetEaseTrack remoteTrack)
	{
		List<string> remoteTitles = BuildTitleVariants(new string[1] { remoteTrack.Title });
		int titleScore = candidate.TitleVariants.SelectMany((string localTitle) => remoteTitles.Select((string remoteTitle) => TitleSimilarity(localTitle, remoteTitle))).DefaultIfEmpty(0).Max();
		if (titleScore < 68)
		{
			return 0;
		}
		int artistScore = ArtistScore(candidate.Artist, remoteTrack.Artist);
		int albumScore = AlbumScore(candidate.Album, remoteTrack.Album);
		if (titleScore < 88 && artistScore == 0 && albumScore == 0)
		{
			return 0;
		}
		return titleScore * 10 + artistScore * 6 + albumScore * 3;
	}

	private static List<string> BuildTitleVariants(IEnumerable<string?> values)
	{
		HashSet<string> variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string? candidate in values)
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				continue;
			}
			string value = candidate;
			HashSet<string> forms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				value,
				LeadingTrackNumberRegex().Replace(value, ""),
				BracketTextRegex().Replace(value, ""),
				TitleNoiseWordsRegex().Replace(value, ""),
				FeaturedArtistSuffixRegex().Replace(value, "")
			};
			string[] array = forms.ToArray();
			foreach (string form in array)
			{
				forms.Add(TitleNoiseWordsRegex().Replace(BracketTextRegex().Replace(form, ""), ""));
				forms.Add(FeaturedArtistSuffixRegex().Replace(form, ""));
			}
			foreach (string form2 in forms)
			{
				AddTitleVariant(variants, form2);
				string[] separated = TitleSeparatorRegex().Split(form2);
				if (separated.Length > 1)
				{
					AddTitleVariant(variants, separated[0]);
					AddTitleVariant(variants, FeaturedArtistSuffixRegex().Replace(separated[0], ""));
				}
				separated = SlashTitleSeparatorRegex().Split(form2);
				if (separated.Length > 1)
				{
					AddTitleVariant(variants, separated[0]);
					AddTitleVariant(variants, FeaturedArtistSuffixRegex().Replace(separated[0], ""));
				}
			}
		}
		return variants.Where((string variant) => variant.Length > 0).ToList();
	}

	private static void AddTitleVariant(HashSet<string> variants, string value)
	{
		string normalized = Normalize(value);
		if (normalized.Length > 0)
		{
			variants.Add(normalized);
		}
		string loose = NormalizeLoose(value);
		if (loose.Length > 0)
		{
			variants.Add(loose);
		}
	}

	private static int TitleSimilarity(string local, string remote)
	{
		if (local.Length == 0 || remote.Length == 0)
		{
			return 0;
		}
		if (string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
		{
			return 100;
		}
		int shorter = Math.Min(local.Length, remote.Length);
		int longer = Math.Max(local.Length, remote.Length);
		bool num = local.Contains(remote, StringComparison.OrdinalIgnoreCase) || remote.Contains(local, StringComparison.OrdinalIgnoreCase);
		bool containsCjk = ContainsCjk(local) || ContainsCjk(remote);
		if (num && (shorter >= 4 || (containsCjk && shorter >= 2)))
		{
			double ratio = (double)shorter / (double)longer;
			double minimumRatio = (containsCjk ? 0.3 : 0.52);
			if (ratio >= minimumRatio)
			{
				return (int)Math.Round(74.0 + ratio * 20.0);
			}
		}
		if (longer < 5)
		{
			return 0;
		}
		int similarity = SimilarityPercent(local, remote);
		if (similarity < 74)
		{
			return 0;
		}
		return similarity;
	}

	private static int ArtistScore(string local, string remote)
	{
		string[] localNames = SplitArtistNames(local).ToArray();
		string[] remoteNames = SplitArtistNames(remote).ToArray();
		if (localNames.Length == 0 || remoteNames.Length == 0)
		{
			return 0;
		}
		string[] array = localNames;
		foreach (string left in array)
		{
			string[] array2 = remoteNames;
			foreach (string right in array2)
			{
				if (left == right)
				{
					return 18;
				}
				if (left.Length >= 2 && right.Length >= 2 && (left.Contains(right) || right.Contains(left)))
				{
					return 14;
				}
			}
		}
		return 0;
	}

	private static int AlbumScore(string local, string remote)
	{
		string left = Normalize(local);
		string right = Normalize(remote);
		if (left.Length == 0 || right.Length == 0)
		{
			return 0;
		}
		if (left == right)
		{
			return 8;
		}
		if (Math.Min(left.Length, right.Length) < 4 || (!left.Contains(right) && !right.Contains(left)))
		{
			return 0;
		}
		return 4;
	}

	private static IEnumerable<string> SplitArtistNames(string value)
	{
		return from name in ArtistSeparatorRegex().Split(value).Select(Normalize)
			where name.Length > 0 && name != Normalize("未知艺术家")
			select name;
	}

	private static int SimilarityPercent(string left, string right)
	{
		if (left.Length == 0 || right.Length == 0)
		{
			return 0;
		}
		int[] previous = new int[right.Length + 1];
		int[] current = new int[right.Length + 1];
		for (int index = 0; index <= right.Length; index++)
		{
			previous[index] = index;
		}
		for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
		{
			current[0] = leftIndex;
			for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
			{
				int cost = ((left[leftIndex - 1] != right[rightIndex - 1]) ? 1 : 0);
				current[rightIndex] = Math.Min(Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1), previous[rightIndex - 1] + cost);
			}
			int[] array = current;
			current = previous;
			previous = array;
		}
		int distance = previous[right.Length];
		int maxLength = Math.Max(left.Length, right.Length);
		return (int)Math.Round((1.0 - (double)distance / (double)maxLength) * 100.0);
	}

	private static bool ContainsCjk(string value)
	{
		return value.Any((char character) => character >= '㐀' && character <= '鿿');
	}

	private static string Normalize(string value)
	{
		return NormalizeCore(value, preserveSymbols: true);
	}

	private static string NormalizeLoose(string value)
	{
		return NormalizeCore(value, preserveSymbols: false);
	}

	private static string NormalizeCore(string value, bool preserveSymbols)
	{
		string text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
		StringBuilder builder = new StringBuilder(text.Length);
		string text2 = text;
		foreach (char character in text2)
		{
			if (char.IsLetterOrDigit(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber)
			{
				builder.Append(character);
			}
			else if (preserveSymbols && IsMeaningfulSymbol(character))
			{
				builder.Append(character);
			}
		}
		return builder.ToString();
	}

	private static bool IsMeaningfulSymbol(char character)
	{
		if (MeaningfulTitleSymbols.Contains(character))
		{
			return true;
		}
		UnicodeCategory unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(character);
		if (unicodeCategory == UnicodeCategory.MathSymbol || (uint)(unicodeCategory - 27) <= 1u)
		{
			return true;
		}
		return false;
	}

	private static string ReadArtistNames(JsonElement track)
	{
		string[] array = new string[2] { "ar", "artists" };
		foreach (string propertyName in array)
		{
			if (track.TryGetProperty(propertyName, out var artists) && artists.ValueKind == JsonValueKind.Array)
			{
				return string.Join(" / ", from artist in artists.EnumerateArray()
					select GetString(artist, "name") into name
					where !string.IsNullOrWhiteSpace(name)
					select name);
			}
		}
		return "未知艺术家";
	}

	private static string ReadAlbumName(JsonElement track)
	{
		string[] array = new string[2] { "al", "album" };
		foreach (string propertyName in array)
		{
			if (track.TryGetProperty(propertyName, out var album) && album.ValueKind == JsonValueKind.Object)
			{
				return GetString(album, "name") ?? "未知专辑";
			}
		}
		return "未知专辑";
	}

	private static int ReadInt(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return 0;
		}
		if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
		{
			return value;
		}
		if (property.ValueKind != JsonValueKind.String || !int.TryParse(property.GetString(), out value))
		{
			return 0;
		}
		return value;
	}

	private static string? GetString(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return property.GetString();
	}

	[GeneratedRegex(@"(?:playlist(?:\?id=|/)|[?&#]id=)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex PlaylistIdRegex();

	[GeneratedRegex(@"(?<!\d)(\d{5,})(?!\d)", RegexOptions.Compiled)]
	private static partial Regex StandaloneLongNumberRegex();

	[GeneratedRegex(@"[\(（\[【].*?[\)）\]】]", RegexOptions.Compiled)]
	private static partial Regex BracketTextRegex();

	[GeneratedRegex(@"^\s*\d{1,3}\s*[\.\-_、 ]+\s*", RegexOptions.Compiled)]
	private static partial Regex LeadingTrackNumberRegex();

	[GeneratedRegex(@"\s*[-–—－_·•|]\s*", RegexOptions.Compiled)]
	private static partial Regex TitleSeparatorRegex();

	[GeneratedRegex(@"\s+(?:/|／|\||｜)\s+", RegexOptions.Compiled)]
	private static partial Regex SlashTitleSeparatorRegex();

	[GeneratedRegex(@"\s+(?:feat(?:uring)?\.?|ft\.?|with|vo\.?)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex FeaturedArtistSuffixRegex();

	[GeneratedRegex(@"[,，、/＆&;；|]|\s+(?:and|x|with|feat\.?|ft\.?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex ArtistSeparatorRegex();

	[GeneratedRegex(@"(?i)\b(?:official|music|video|lyrics?|audio|remaster(?:ed)?|remix|version|live|mv|hd|hq|cover|explicit|instrumental)\b|伴奏|纯音乐|现场|高清|无损|歌词|完整版|版本|版")]
	private static partial Regex TitleNoiseWordsRegex();
}
