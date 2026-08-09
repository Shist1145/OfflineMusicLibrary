using OfflineMusicLibrary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

await EmptyLargeBatchesRetryInSmallGroupsAsync();
await VocalAndInstrumentalVersionsStaySeparatedAsync();
await GlobalAssignmentProtectsConstrainedTracksAsync();
await OversizedResponseIsRejectedAsync();
Console.WriteLine("Cross-platform NetEase playlist checks passed.");

static async Task OversizedResponseIsRejectedAsync()
{
	using HttpClient client = new(new OversizedPlaylistHandler());
	NetEasePlaylistService service = new(client);
	bool rejected = false;
	try
	{
		_ = await service.ImportAsync("823456789", []);
	}
	catch (InvalidOperationException exception)
	{
		for (Exception? current = exception; current != null; current = current.InnerException)
		{
			if (current is InvalidDataException)
			{
				rejected = true;
				break;
			}
		}
	}
	Require(rejected, "The cross-platform importer must reject an oversized response before buffering it.");
}

static async Task EmptyLargeBatchesRetryInSmallGroupsAsync()
{
	FakeSong[] songs = Enumerable.Range(500001, 700)
		.Select(id => new FakeSong(id.ToString(), $"Track {id}", $"Artist {id}", $"Album {id}", 180000 + id % 4000))
		.ToArray();
	PlaylistApiHandler handler = new PlaylistApiHandler("cross-platform 700", songs, embedTracks: false, maximumSuccessfulDetailBatch: 25);
	using HttpClient client = new HttpClient(handler);
	NetEasePlaylistService service = new NetEasePlaylistService(client);

	NetEaseImportResult result = await service.ImportAsync("523456789", []);

	Require(result.TrackIdCount == 700 && result.ResolvedTrackCount == 700 && result.UnresolvedTrackIds.Count == 0,
		"The cross-platform importer must retry an entirely empty 100-song response in 25-song groups.");
	Require(handler.EmptyLargeDetailResponses > 0 && handler.SuccessfulSmallDetailResponses >= 28,
		"The test must exercise the small-batch retry path.");
}

static async Task VocalAndInstrumentalVersionsStaySeparatedAsync()
{
	FakeSong[] songs =
	[
		new FakeSong("601", "Starlight", "Circle A", "Album A", 201000),
		new FakeSong("602", "Starlight (Off Vocal)", "Circle A", "Album A", 201000)
	];
	PlaylistApiHandler handler = new PlaylistApiHandler("cross-platform versions", songs, embedTracks: true);
	using HttpClient client = new HttpClient(handler);
	NetEasePlaylistService service = new NetEasePlaylistService(client);
	TrackModel instrumental = new TrackModel
	{
		Id = "cross-instrumental",
		FilePath = "/music/Starlight - instrumental.flac",
		Title = "Starlight - instrumental",
		Artist = "Circle A",
		Album = "Album A",
		DurationMs = 201000,
		CloudId = "601"
	};
	TrackModel vocal = new TrackModel
	{
		Id = "cross-vocal",
		FilePath = "/music/Starlight.flac",
		Title = "Starlight",
		Artist = "Circle A",
		Album = "Album A",
		DurationMs = 201000
	};

	NetEaseImportResult result = await service.ImportAsync("623456789", [instrumental, vocal]);

	Require(result.Matched.Count == 2 && result.Matched[0].Id == vocal.Id && result.Matched[1].Id == instrumental.Id,
		"The cross-platform importer must not swap standard and Off Vocal files.");
	Require(!instrumental.HasCloudId("601") && instrumental.HasCloudId("602") && vocal.HasCloudId("601"),
		"The cross-platform importer must repair a historical vocal/instrumental cloud-ID mix-up.");
}

static async Task GlobalAssignmentProtectsConstrainedTracksAsync()
{
	FakeSong[] songs =
	[
		new FakeSong("701", "Moonlight", "未知艺术家", "未知专辑", 180000),
		new FakeSong("702", "Moonlight Sonata", "Composer", "Classical", 240000)
	];
	PlaylistApiHandler handler = new PlaylistApiHandler("cross-platform assignment", songs, embedTracks: true);
	using HttpClient client = new HttpClient(handler);
	NetEasePlaylistService service = new NetEasePlaylistService(client);
	TrackModel constrained = new TrackModel
	{
		Id = "cross-constrained",
		FilePath = "/music/Moonlight.flac",
		Title = "Moonlight",
		Artist = "Composer",
		Album = "Classical",
		DurationMs = 240000
	};
	TrackModel ambiguous = new TrackModel
	{
		Id = "cross-ambiguous",
		FilePath = "/music/Moonlight alternate.flac",
		Title = "Moonlight",
		Artist = "未知艺术家",
		Album = "",
		DurationMs = 180000
	};

	NetEaseImportResult result = await service.ImportAsync("723456789", [constrained, ambiguous]);

	Require(result.Matched.Count == 2 && result.Matched[0].Id == ambiguous.Id && result.Matched[1].Id == constrained.Id,
		"Global one-to-one assignment must keep the only valid candidate for the constrained remote track.");
}

static void Require(bool condition, string message)
{
	if (!condition)
		throw new InvalidOperationException(message);
}

internal sealed record FakeSong(string Id, string Title, string Artist, string Album, long DurationMs);

internal sealed class OversizedPlaylistHandler : HttpMessageHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
		Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new DeclaredLengthContent(32L * 1024L * 1024L + 1L)
		});
}

internal sealed class DeclaredLengthContent : HttpContent
{
	private readonly long _length;

	public DeclaredLengthContent(long length)
	{
		_length = length;
		Headers.ContentLength = length;
	}

	protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

	protected override bool TryComputeLength(out long length)
	{
		length = _length;
		return true;
	}
}

internal sealed class PlaylistApiHandler : HttpMessageHandler
{
	private static readonly Regex IdPropertyRegex = new("\\\"id\\\"\\s*:\\s*(?<id>\\d+)", RegexOptions.Compiled);
	private static readonly Regex NumberRegex = new("\\d+", RegexOptions.Compiled);
	private readonly Dictionary<string, FakeSong> _songs;
	private readonly string _playlistJson;
	private readonly int _maximumSuccessfulDetailBatch;

	public PlaylistApiHandler(string playlistName, IReadOnlyList<FakeSong> songs, bool embedTracks, int maximumSuccessfulDetailBatch = int.MaxValue)
	{
		_songs = songs.ToDictionary(song => song.Id, StringComparer.OrdinalIgnoreCase);
		_maximumSuccessfulDetailBatch = maximumSuccessfulDetailBatch;
		_playlistJson = JsonSerializer.Serialize(new
		{
			playlist = new
			{
				name = playlistName,
				trackCount = songs.Count,
				trackIds = songs.Select(song => new { id = long.Parse(song.Id) }).ToArray(),
				tracks = embedTracks ? songs.Select(ToApiSong).ToArray() : []
			}
		});
	}

	public int EmptyLargeDetailResponses { get; private set; }
	public int SuccessfulSmallDetailResponses { get; private set; }

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		Uri uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
		if (uri.AbsolutePath.Contains("playlist/detail", StringComparison.OrdinalIgnoreCase))
			return Task.FromResult(JsonResponse(_playlistJson));
		if (uri.AbsolutePath.Contains("song/detail", StringComparison.OrdinalIgnoreCase))
		{
			string[] ids = ExtractRequestedIds(uri);
			if (ids.Length > _maximumSuccessfulDetailBatch)
			{
				EmptyLargeDetailResponses++;
				return Task.FromResult(JsonResponse("{\"songs\":[]}"));
			}
			SuccessfulSmallDetailResponses++;
			object[] songs = ids.Where(_songs.ContainsKey).Select(id => ToApiSong(_songs[id])).ToArray();
			return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { songs })));
		}
		return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
	}

	private static object ToApiSong(FakeSong song) => new
	{
		id = long.Parse(song.Id),
		name = song.Title,
		ar = new[] { new { name = song.Artist } },
		al = new { name = song.Album },
		dt = song.DurationMs
	};

	private static string[] ExtractRequestedIds(Uri uri)
	{
		string query = Uri.UnescapeDataString(uri.Query.TrimStart('?'));
		MatchCollection propertyMatches = IdPropertyRegex.Matches(query);
		IEnumerable<string> ids = propertyMatches.Count > 0
			? propertyMatches.Select(match => match.Groups["id"].Value)
			: NumberRegex.Matches(query).Select(match => match.Value);
		return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, Encoding.UTF8, "application/json")
	};
}
