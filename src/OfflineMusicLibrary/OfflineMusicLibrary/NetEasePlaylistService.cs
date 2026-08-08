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
	private sealed record MatchResult(
		IReadOnlyList<TrackModel> Tracks,
		HashSet<string> MatchedRemoteIds,
		int ExactCount,
		int FuzzyCount,
		int CorrectedCloudIdCount);

	private sealed record MatchOption(int LocalIndex, int Score);

	private sealed class LocalMatchCandidate
	{
		public TrackModel Track { get; }

		public IReadOnlyList<string> TitleVariants { get; }

		public string Artist { get; }

		public string Album { get; }

		public bool IsInstrumental { get; }

		public long DurationMs => Track.DurationMs;

		private LocalMatchCandidate(TrackModel track, IReadOnlyList<string> titleVariants, bool isInstrumental)
		{
			Track = track;
			TitleVariants = titleVariants;
			Artist = track.Artist + " / " + track.AlbumArtist;
			Album = track.Album;
			IsInstrumental = isInstrumental;
		}

		public static LocalMatchCandidate Create(TrackModel track)
		{
			string[] values = new string[2]
			{
				track.Title,
				Path.GetFileNameWithoutExtension(track.FilePath)
			};
			return new LocalMatchCandidate(track, BuildTitleVariants(values), HasInstrumentalMarker(values));
		}
	}

	private const int TrackDetailBatchSize = 100;

	private const int SmallRetryBatchSize = 25;

	private const int PlayablePreferenceBonus = 140;

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
			_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 OfflineMusicLibrary/1.6.3");
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
		List<NetEaseTrack> missing = remoteTracks.Where((NetEaseTrack track) =>
			!string.IsNullOrWhiteSpace(track.Title) &&
			!matchResult.MatchedRemoteIds.Contains(track.Id)).ToList();
		int declared = Math.Max(declaredTrackCount, Math.Max(trackIds.Count, remoteTracks.Count));
		DiagnosticLog.Write("NetEaseImport", $"歌单={playlistName}({playlistId})，声明={declared}，ID={trackIds.Count}，详情={resolvedIds.Count}，详情暂缺={unresolvedTrackIds.Count}，精确={matchResult.ExactCount}，模糊={matchResult.FuzzyCount}，修正旧云ID={matchResult.CorrectedCloudIdCount}，未匹配={missing.Count}");
		return new NetEaseImportResult(playlistName, playlistId, declared, remoteTracks, matchResult.Tracks, missing)
		{
			TrackIdCount = trackIds.Count,
			ResolvedTrackCount = resolvedIds.Count,
			ExactMatchCount = matchResult.ExactCount,
			FuzzyMatchCount = matchResult.FuzzyCount,
			CorrectedCloudIdCount = matchResult.CorrectedCloudIdCount,
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
		for (int attempt = 1; attempt <= PlaylistRequestAttempts; attempt++)
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
				if (attempt < PlaylistRequestAttempts)
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
		foreach (string[] batch in trackIds.Chunk(TrackDetailBatchSize))
		{
			List<NetEaseTrack> fetched = await FetchTrackGroupAsync(batch, 3, cancellationToken);
			foreach (NetEaseTrack track in fetched)
			{
				result[track.Id] = track;
			}
			string[] unresolved = batch.Where((string id) => !result.ContainsKey(id)).ToArray();
			if (unresolved.Length == 0)
			{
				continue;
			}
			foreach (string[] smallBatch in unresolved.Chunk(SmallRetryBatchSize))
			{
				foreach (NetEaseTrack track2 in await FetchTrackGroupAsync(smallBatch, 2, cancellationToken))
				{
					result[track2.Id] = track2;
				}
			}
			unresolved = batch.Where((string id) => !result.ContainsKey(id)).ToArray();
			if (unresolved.Length > 0)
			{
				DiagnosticLog.Write("NetEaseImport", $"歌曲详情小批重试后仍缺少 {unresolved.Length}/{batch.Length} 首；保留歌曲 ID，等待下次导入继续补全。");
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
		long durationMs = ReadLong(track, "dt");
		if (durationMs <= 0)
		{
			durationMs = ReadLong(track, "duration");
		}
		return new NetEaseTrack(id, title, artists, album, durationMs);
	}

	private static MatchResult MatchTracks(IReadOnlyList<NetEaseTrack> remote, IReadOnlyList<TrackModel> local)
	{
		List<TrackModel> uniqueLocal = local
			.Where(track => track != null && !string.IsNullOrWhiteSpace(track.Id))
			.GroupBy(track => track.Id, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.OrderBy(track => track.IsEncryptedNcm).First())
			.ToList();
		List<LocalMatchCandidate> candidates = uniqueLocal.Select(LocalMatchCandidate.Create).ToList();
		Dictionary<string, List<int>> byCloudId = candidates
			.SelectMany((candidate, localIndex) => candidate.Track.GetCloudIds().Select(id => new { id, localIndex }))
			.GroupBy(item => item.id, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group.Select(item => item.localIndex).Distinct().ToList(),
				StringComparer.OrdinalIgnoreCase);

		int[] assignments = Enumerable.Repeat(-1, remote.Count).ToArray();
		int[] localOwners = Enumerable.Repeat(-1, candidates.Count).ToArray();
		bool[] exactAssignments = new bool[remote.Count];

		Dictionary<int, List<MatchOption>> exactOptions = new Dictionary<int, List<MatchOption>>();
		for (int remoteIndex = 0; remoteIndex < remote.Count; remoteIndex++)
		{
			NetEaseTrack remoteTrack = remote[remoteIndex];
			if (!string.IsNullOrWhiteSpace(remoteTrack.Id) && byCloudId.TryGetValue(remoteTrack.Id, out List<int>? knownLocalIndexes))
			{
				List<MatchOption> options = knownLocalIndexes
					.Select(localIndex => new MatchOption(localIndex, KnownIdMatchScore(candidates[localIndex], remoteTrack)))
					.Where(option => option.Score > 0)
					.OrderByDescending(option => option.Score)
					.ThenBy(option => option.LocalIndex)
					.ToList();
				if (options.Count > 0)
				{
					exactOptions[remoteIndex] = options;
				}
			}
		}
		AssignOptions(exactOptions, assignments, localOwners, exactAssignments);
		foreach (int remoteIndex in exactOptions.Keys)
		{
			exactAssignments[remoteIndex] = assignments[remoteIndex] >= 0;
		}

		Dictionary<int, List<MatchOption>> fuzzyOptions = new Dictionary<int, List<MatchOption>>();
		for (int remoteIndex = 0; remoteIndex < remote.Count; remoteIndex++)
		{
			if (assignments[remoteIndex] >= 0)
			{
				continue;
			}
			NetEaseTrack remoteTrack = remote[remoteIndex];
			List<MatchOption> options = candidates
				.Select((candidate, localIndex) => new
				{
					Candidate = candidate,
					LocalIndex = localIndex,
					RawScore = localOwners[localIndex] >= 0 ? 0 : MatchScore(candidate, remoteTrack)
				})
				.Where(item => item.RawScore > 0)
				.Select(item => new MatchOption(
					item.LocalIndex,
					item.RawScore + (item.Candidate.Track.IsEncryptedNcm ? 0 : PlayablePreferenceBonus) -
					(item.Candidate.Track.HasCloudIds && !item.Candidate.Track.HasCloudId(remoteTrack.Id) ? 30 : 0)))
				.OrderByDescending(option => option.Score)
				.ThenBy(option => option.LocalIndex)
				.ToList();
			if (options.Count > 0)
			{
				fuzzyOptions[remoteIndex] = options;
			}
		}
		AssignOptions(fuzzyOptions, assignments, localOwners, exactAssignments);

		for (int remoteIndex = 0; remoteIndex < remote.Count; remoteIndex++)
		{
			int existingLocalIndex = assignments[remoteIndex];
			if (existingLocalIndex < 0 || !candidates[existingLocalIndex].Track.IsEncryptedNcm || string.IsNullOrWhiteSpace(remote[remoteIndex].Title))
			{
				continue;
			}
			int currentScore = MatchScore(candidates[existingLocalIndex], remote[remoteIndex]);
			MatchOption? playable = candidates
				.Select((candidate, localIndex) => new MatchOption(localIndex, localOwners[localIndex] < 0 && !candidate.Track.IsEncryptedNcm ? MatchScore(candidate, remote[remoteIndex]) : 0))
				.Where(option => option.Score > 0 && option.Score >= currentScore - PlayablePreferenceBonus)
				.OrderByDescending(option => option.Score)
				.ThenBy(option => option.LocalIndex)
				.FirstOrDefault();
			if (playable == null)
			{
				continue;
			}
			localOwners[existingLocalIndex] = -1;
			assignments[remoteIndex] = playable.LocalIndex;
			localOwners[playable.LocalIndex] = remoteIndex;
			exactAssignments[remoteIndex] = false;
		}

		List<TrackModel> tracks = new List<TrackModel>();
		HashSet<string> matchedRemoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int exactCount = 0;
		int fuzzyCount = 0;
		int correctedCloudIdCount = 0;
		for (int remoteIndex = 0; remoteIndex < assignments.Length; remoteIndex++)
		{
			int localIndex = assignments[remoteIndex];
			if (localIndex >= 0)
			{
				TrackModel match = candidates[localIndex].Track;
				if (!string.IsNullOrWhiteSpace(remote[remoteIndex].Title))
				{
					foreach (TrackModel staleOwner in local.Where(track => !ReferenceEquals(track, match) && track.HasCloudId(remote[remoteIndex].Id)))
					{
						if (staleOwner.ForgetCloudId(remote[remoteIndex].Id))
						{
							correctedCloudIdCount++;
						}
					}
				}
				match.RememberCloudId(remote[remoteIndex].Id);
				tracks.Add(match);
				matchedRemoteIds.Add(remote[remoteIndex].Id);
				if (exactAssignments[remoteIndex])
				{
					exactCount++;
				}
				else
				{
					fuzzyCount++;
				}
			}
		}
		return new MatchResult(tracks, matchedRemoteIds, exactCount, fuzzyCount, correctedCloudIdCount);
	}

	private static int KnownIdMatchScore(LocalMatchCandidate candidate, NetEaseTrack remoteTrack)
	{
		if (string.IsNullOrWhiteSpace(remoteTrack.Title))
		{
			return 1000000 + (candidate.Track.IsEncryptedNcm ? 0 : PlayablePreferenceBonus);
		}
		if (candidate.IsInstrumental != HasInstrumentalMarker(new[] { remoteTrack.Title }))
		{
			return 0;
		}
		int score = Math.Max(1, MatchScore(candidate, remoteTrack));
		return 1000000 + score + (candidate.Track.IsEncryptedNcm ? 0 : PlayablePreferenceBonus);
	}

	private static void AssignOptions(
		IReadOnlyDictionary<int, List<MatchOption>> optionsByRemote,
		int[] assignments,
		int[] localOwners,
		bool[] lockedRemoteAssignments)
	{
		foreach (int remoteIndex in optionsByRemote
			.OrderBy(pair => pair.Value.Count)
			.ThenByDescending(pair => pair.Value.Count == 0 ? 0 : pair.Value[0].Score)
			.Select(pair => pair.Key))
		{
			bool[] visitedLocal = new bool[localOwners.Length];
			TryAssign(remoteIndex, optionsByRemote, assignments, localOwners, lockedRemoteAssignments, visitedLocal, new HashSet<int>());
		}
	}

	private static bool TryAssign(
		int remoteIndex,
		IReadOnlyDictionary<int, List<MatchOption>> optionsByRemote,
		int[] assignments,
		int[] localOwners,
		bool[] lockedRemoteAssignments,
		bool[] visitedLocal,
		HashSet<int> visitingRemote)
	{
		if (!visitingRemote.Add(remoteIndex) || !optionsByRemote.TryGetValue(remoteIndex, out List<MatchOption>? options))
		{
			return false;
		}
		foreach (MatchOption option in options)
		{
			if (visitedLocal[option.LocalIndex])
			{
				continue;
			}
			visitedLocal[option.LocalIndex] = true;
			int owner = localOwners[option.LocalIndex];
			if (owner >= 0 && (lockedRemoteAssignments[owner] || !TryAssign(owner, optionsByRemote, assignments, localOwners, lockedRemoteAssignments, visitedLocal, visitingRemote)))
			{
				continue;
			}

			int oldLocalIndex = assignments[remoteIndex];
			assignments[remoteIndex] = option.LocalIndex;
			localOwners[option.LocalIndex] = remoteIndex;
			if (oldLocalIndex >= 0 && oldLocalIndex != option.LocalIndex && localOwners[oldLocalIndex] == remoteIndex)
			{
				localOwners[oldLocalIndex] = -1;
			}
			visitingRemote.Remove(remoteIndex);
			return true;
		}
		visitingRemote.Remove(remoteIndex);
		return false;
	}

	private static int MatchScore(LocalMatchCandidate candidate, NetEaseTrack remoteTrack)
	{
		if (candidate.IsInstrumental != HasInstrumentalMarker(new[] { remoteTrack.Title }))
		{
			return 0;
		}
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
		int durationScore = DurationScore(candidate.DurationMs, remoteTrack.DurationMs);
		return titleScore * 10 + artistScore * 6 + albumScore * 3 + durationScore * 4;
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
				InstrumentalMarkerRegex().Replace(value, ""),
				FeaturedArtistSuffixRegex().Replace(value, "")
			};
			string[] array = forms.ToArray();
			foreach (string form in array)
			{
				forms.Add(TitleNoiseWordsRegex().Replace(BracketTextRegex().Replace(form, ""), ""));
				forms.Add(InstrumentalMarkerRegex().Replace(form, ""));
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

	private static int DurationScore(long localDurationMs, long remoteDurationMs)
	{
		if (localDurationMs <= 0 || remoteDurationMs <= 0)
		{
			return 0;
		}
		long difference = Math.Abs(localDurationMs - remoteDurationMs);
		long closeTolerance = Math.Max(2000L, Math.Min(localDurationMs, remoteDurationMs) / 100L);
		if (difference <= closeTolerance)
		{
			return 10;
		}
		if (difference <= 5000L)
		{
			return 6;
		}
		if (difference <= 10000L)
		{
			return 2;
		}
		return 0;
	}

	private static bool HasInstrumentalMarker(IEnumerable<string?> values)
	{
		return values.Any(value => !string.IsNullOrWhiteSpace(value) && InstrumentalMarkerRegex().IsMatch(value));
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

	private static long ReadLong(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out JsonElement property))
		{
			return 0L;
		}
		if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out long value))
		{
			return value;
		}
		return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value) ? value : 0L;
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

	[GeneratedRegex(@"\s+(?:feat(?:uring)?\.?|ft\.?|with|vo\.?)\s*.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex FeaturedArtistSuffixRegex();

	[GeneratedRegex(@"[,，、/＆&;；|]|\s+(?:and|x|with|feat\.?|ft\.?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
	private static partial Regex ArtistSeparatorRegex();

	[GeneratedRegex(@"(?i)\b(?:official|music|video|lyrics?|audio|remaster(?:ed)?|remix|version|live|mv|hd|hq|cover|explicit|instrumental)\b|伴奏|纯音乐|现场|高清|无损|歌词|完整版|版本|版")]
	private static partial Regex TitleNoiseWordsRegex();

	[GeneratedRegex(@"(?ix)(?:\boff[\s._-]*(?:vocals?|vo)\b|\bvocals?[\s._-]*less\b|\binstrumental(?:\s+(?:mix|version|ver\.?))?\b|\binst(?:\.|rumental)?\b|\bkaraoke\b|\baccompaniment\b|\bbacking[\s._-]*track\b|\bminus[\s._-]*(?:one|vocals?)\b|\b(?:without|no)[\s._-]*(?:lead[\s._-]*)?(?:voice|vocals?)\b|伴奏(?:版)?|纯音乐|純音樂|純音楽|无人声|無人聲|无主唱|無主唱|去人声|去人聲|オフ[\s・._-]*(?:ボ|ヴォ)ーカル|(?:ボ|ヴォ)ーカル[\s・._-]*(?:なし|無し)|歌(?:なし|無し)|インスト(?:ゥルメンタル)?|カラオケ)", RegexOptions.Compiled)]
	private static partial Regex InstrumentalMarkerRegex();
}
