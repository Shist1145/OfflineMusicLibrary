using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OfflineMusicLibrary;

if (args.Length > 0 && string.Equals(args[0], "--live", StringComparison.OrdinalIgnoreCase))
{
    await RunLiveAuditAsync(args);
    return;
}

await AdaptiveDetailRetryRecoversSevenHundredTracksAsync();
await IncompleteDetailsPreserveExistingPlaylistEntriesAsync();
await InstrumentalVersionsStayWithTheirOwnFilesAsync();
await InstrumentalMarkerVariantsStaySeparatedAsync();
await CompactFeaturedArtistSuffixMatchesAsync();
await ConstrainedTracksAreAssignedBeforeAmbiguousTracksAsync();
await OversizedPlaylistResponseIsRejectedAsync();
Console.WriteLine("NetEase playlist import checks passed.");

static async Task OversizedPlaylistResponseIsRejectedAsync()
{
    using HttpClient client = new(new OversizedPlaylistHandler());
    NetEasePlaylistService service = new(client);
    bool rejected = false;
    try
    {
        _ = await service.ImportAsync("123456789", Array.Empty<TrackModel>());
    }
    catch (InvalidOperationException exception) when (ContainsInvalidDataException(exception))
    {
        rejected = true;
    }
    Require(rejected, "An oversized playlist response must be rejected before its body is buffered or parsed.");
    Require(NetEasePlaylistService.ExtractPlaylistId(new string('9', 21)) == null,
        "Unreasonably long numeric playlist identifiers must be rejected.");
}

static bool ContainsInvalidDataException(Exception exception)
{
    for (Exception? current = exception; current != null; current = current.InnerException)
    {
        if (current is InvalidDataException)
            return true;
    }
    return false;
}

static async Task AdaptiveDetailRetryRecoversSevenHundredTracksAsync()
{
    FakeSong[] songs = Enumerable.Range(100001, 700)
        .Select(id => new FakeSong(id.ToString(), $"Unique Track {id}", $"Artist {id}", $"Album {id}", 180000 + id % 5000))
        .ToArray();
    PlaylistApiHandler handler = new PlaylistApiHandler("700-track retry", songs, embedTracks: false, maximumSuccessfulDetailBatch: 25);
    using HttpClient client = new HttpClient(handler);
    NetEasePlaylistService service = new NetEasePlaylistService(client);

    NetEaseImportResult result = await service.ImportAsync("123456789", Array.Empty<TrackModel>());

    Require(result.DeclaredTrackCount == 700 && result.TrackIdCount == 700,
        "A 700-track playlist must retain every declared remote ID.");
    Require(result.ResolvedTrackCount == 700 && result.UnresolvedTrackIds.Count == 0,
        "When 100-track detail batches return empty, all IDs must be retried in 25-track groups.");
    Require(handler.EmptyLargeDetailResponses > 0 && handler.SuccessfulSmallDetailResponses >= 28,
        "The test must exercise the adaptive small-batch recovery path.");
}

static async Task IncompleteDetailsPreserveExistingPlaylistEntriesAsync()
{
    FakeSong[] songs =
    [
        new FakeSong("191", "Temporarily unavailable detail", "Artist", "Album", 180000)
    ];
    PlaylistApiHandler handler = new PlaylistApiHandler(
        "partial detail safety",
        songs,
        embedTracks: false,
        maximumSuccessfulDetailBatch: 0);
    using HttpClient client = new HttpClient(handler);
    NetEasePlaylistService service = new NetEasePlaylistService(client);
    TrackModel existing = new TrackModel
    {
        Id = "existing-local-track",
        FilePath = @"C:\Music\Temporarily unavailable detail.flac",
        Title = "Temporarily unavailable detail",
        Artist = "Artist",
        Album = "Album",
        DurationMs = 180000
    };

    NetEaseImportResult result = await service.ImportAsync("193456789", new[] { existing });
    List<string> synchronized = PlaylistMaintenance.BuildSynchronizedTrackIds(
        new[] { existing.Id },
        result.Matched,
        result.RemoteTrackIds,
        result.HasCompleteRemoteDetails,
        new[] { existing });

    Require(result.HasCompleteTrackIds && !result.HasCompleteRemoteDetails && result.UnresolvedTrackIds.Count == 1,
        "The importer must distinguish complete IDs from temporarily incomplete song details.");
    Require(synchronized.SequenceEqual(new[] { existing.Id }, StringComparer.OrdinalIgnoreCase),
        "Incomplete remote details must not authorize removal of existing playlist entries.");
}

static async Task InstrumentalVersionsStayWithTheirOwnFilesAsync()
{
    FakeSong[] songs =
    [
        new FakeSong("201", "Starlight", "Circle A", "Starlight Album", 201000),
        new FakeSong("202", "Starlight (Off Vocal)", "Circle A", "Starlight Album", 201000)
    ];
    PlaylistApiHandler handler = new PlaylistApiHandler("version-aware", songs, embedTracks: true);
    using HttpClient client = new HttpClient(handler);
    NetEasePlaylistService service = new NetEasePlaylistService(client);
    TrackModel instrumental = new TrackModel
    {
        Id = "local-off-vocal",
        FilePath = @"C:\Music\Starlight - instrumental.flac",
        Title = "Starlight - instrumental",
        Artist = "Circle A",
        Album = "Starlight Album",
        DurationMs = 201000,
        CloudId = "201"
    };
    TrackModel vocal = new TrackModel
    {
        Id = "local-vocal",
        FilePath = @"C:\Music\Starlight.flac",
        Title = "Starlight",
        Artist = "Circle A",
        Album = "Starlight Album",
        DurationMs = 201000
    };

    NetEaseImportResult result = await service.ImportAsync("223456789", new[] { instrumental, vocal });

    Require(result.Matched.Count == 2, "Both vocal and instrumental editions should match.");
    Require(result.Matched[0].Id == vocal.Id && result.Matched[1].Id == instrumental.Id,
        "The standard song and Off Vocal song must not be swapped.");
    Require(!instrumental.HasCloudId("201") && instrumental.HasCloudId("202") && vocal.HasCloudId("201"),
        "A previously learned wrong Off Vocal cloud ID must be repaired.");
    Require(result.CorrectedCloudIdCount == 1,
        "The import report should expose the corrected historical cloud-ID assignment.");
}

static async Task InstrumentalMarkerVariantsStaySeparatedAsync()
{
    (string BaseTitle, string InstrumentalTitle)[] versions =
    [
        ("Moon Bloom", "Moon Bloom（伴奏版）"),
        ("Night Sky", "Night Sky オフ・ヴォーカル"),
        ("Sea Glass", "Sea Glass (Vocal-less)"),
        ("Dawn Chorus", "Dawn Chorus (Backing Track)"),
        ("Rain Trace", "Rain Trace（純音樂）"),
        ("Echo Line", "Echo Line（無主唱）")
    ];
    List<FakeSong> songs = new List<FakeSong>();
    List<TrackModel> localTracks = new List<TrackModel>();
    for (int index = 0; index < versions.Length; index++)
    {
        string vocalId = (211 + index * 2).ToString();
        string instrumentalId = (212 + index * 2).ToString();
        (string baseTitle, string instrumentalTitle) = versions[index];
        songs.Add(new FakeSong(vocalId, baseTitle, $"Artist {index}", $"Album {index}", 210000 + index));
        songs.Add(new FakeSong(instrumentalId, instrumentalTitle, $"Artist {index}", $"Album {index}", 210000 + index));
        localTracks.Add(new TrackModel
        {
            Id = $"vocal-{index}",
            FilePath = $@"C:\Music\{baseTitle}.flac",
            Title = baseTitle,
            Artist = $"Artist {index}",
            Album = $"Album {index}",
            DurationMs = 210000 + index
        });
        localTracks.Add(new TrackModel
        {
            Id = $"instrumental-{index}",
            FilePath = $@"C:\Music\{instrumentalTitle}.flac",
            Title = instrumentalTitle,
            Artist = $"Artist {index}",
            Album = $"Album {index}",
            DurationMs = 210000 + index,
            CloudId = vocalId
        });
    }

    PlaylistApiHandler handler = new PlaylistApiHandler("instrumental marker variants", songs, embedTracks: true);
    using HttpClient client = new HttpClient(handler);
    NetEasePlaylistService service = new NetEasePlaylistService(client);
    NetEaseImportResult result = await service.ImportAsync("233456789", localTracks);

    Require(result.Matched.Count == songs.Count, "Every vocal and instrumental marker variant should match.");
    for (int index = 0; index < versions.Length; index++)
    {
        TrackModel vocal = localTracks[index * 2];
        TrackModel instrumental = localTracks[index * 2 + 1];
        string vocalId = songs[index * 2].Id;
        string instrumentalId = songs[index * 2 + 1].Id;
        Require(result.Matched[index * 2].Id == vocal.Id && result.Matched[index * 2 + 1].Id == instrumental.Id,
            $"Instrumental marker variant '{versions[index].InstrumentalTitle}' must not be swapped with its vocal edition.");
        Require(vocal.HasCloudId(vocalId) && !instrumental.HasCloudId(vocalId) && instrumental.HasCloudId(instrumentalId),
            $"Wrong historical cloud IDs must be repaired for '{versions[index].InstrumentalTitle}'.");
    }
    Require(result.CorrectedCloudIdCount == versions.Length,
        "The report should count every corrected vocal/instrumental cloud-ID mix-up.");
}

static async Task CompactFeaturedArtistSuffixMatchesAsync()
{
    FakeSong[] songs =
    [
        new FakeSong("251", "ever after", "millie", "Everflowering", 400480)
    ];
    PlaylistApiHandler handler = new PlaylistApiHandler("compact featured artist", songs, embedTracks: true);
    using HttpClient client = new HttpClient(handler);
    NetEasePlaylistService service = new NetEasePlaylistService(client);
    TrackModel local = new TrackModel
    {
        Id = "local-compact-featuring",
        FilePath = @"C:\Music\ever after feat.millie - millie.flac",
        Title = "ever after feat.millie",
        Artist = "millie",
        Album = "Everflowering",
        DurationMs = 400480
    };

    NetEaseImportResult result = await service.ImportAsync("253456789", new[] { local });

    Require(result.Matched.Count == 1 && result.Matched[0].Id == local.Id,
        "A compact 'feat.artist' suffix must not hide an otherwise exact local title match.");
}

static async Task ConstrainedTracksAreAssignedBeforeAmbiguousTracksAsync()
{
    FakeSong[] songs =
    [
        new FakeSong("301", "Moonlight", "未知艺术家", "未知专辑", 180000),
        new FakeSong("302", "Moonlight Sonata", "Composer", "Classical", 240000)
    ];
    PlaylistApiHandler handler = new PlaylistApiHandler("global assignment", songs, embedTracks: true);
    using HttpClient client = new HttpClient(handler);
    NetEasePlaylistService service = new NetEasePlaylistService(client);
    TrackModel constrained = new TrackModel
    {
        Id = "local-constrained",
        FilePath = @"C:\Music\Moonlight.flac",
        Title = "Moonlight",
        Artist = "Composer",
        Album = "Classical",
        DurationMs = 240000
    };
    TrackModel ambiguous = new TrackModel
    {
        Id = "local-ambiguous",
        FilePath = @"C:\Music\Moonlight alternate.flac",
        Title = "Moonlight",
        Artist = "未知艺术家",
        Album = "",
        DurationMs = 180000
    };

    NetEaseImportResult result = await service.ImportAsync("323456789", new[] { constrained, ambiguous });

    Require(result.Matched.Count == 2, "Global one-to-one assignment should recover both plausible tracks.");
    Require(result.Matched[0].Id == ambiguous.Id && result.Matched[1].Id == constrained.Id,
        "An ambiguous early song must not steal the only candidate of a later constrained song.");
}

static async Task RunLiveAuditAsync(string[] arguments)
{
    if (arguments.Length < 3)
    {
        throw new ArgumentException("Usage: --live <state-path> <playlist-id>");
    }
    string statePath = Path.GetFullPath(arguments[1]);
    string playlistId = arguments[2];
    await using FileStream stream = File.OpenRead(statePath);
    if (stream.Length > 256L * 1024L * 1024L)
    {
        throw new InvalidDataException("State file exceeds the 256 MiB live-audit safety limit.");
    }
    AppState state = await JsonSerializer.DeserializeAsync<AppState>(stream, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? throw new InvalidDataException("State file is empty.");
    PlaylistModel? existing = state.Playlists.FirstOrDefault(playlist =>
        string.Equals(playlist.CloudPlaylistId, playlistId, StringComparison.OrdinalIgnoreCase));
    NetEasePlaylistService service = new NetEasePlaylistService();
    NetEaseImportResult result = await service.ImportAsync(playlistId, state.Tracks);
    Console.WriteLine(
        $"Read-only live audit: existing={existing?.TrackIds.Count ?? 0}, declared={result.DeclaredTrackCount}, " +
        $"ids={result.TrackIdCount}, details={result.ResolvedTrackCount}, exact={result.ExactMatchCount}, " +
        $"fuzzy={result.FuzzyMatchCount}, matched={result.Matched.Count}, unresolved={result.UnresolvedTrackIds.Count}, " +
        $"unmatched={result.Missing.Count}, correctedIds={result.CorrectedCloudIdCount}");
    foreach (NetEaseTrack track in result.Missing)
    {
        Console.WriteLine($"UNMATCHED\t{track.Id}\t{track.Title}\t{track.Artist}\t{track.Album}\t{track.DurationMs}");
        string remoteTitle = AuditKey(track.Title);
        string remoteArtist = AuditKey(track.Artist);
        foreach (TrackModel candidate in state.Tracks
            .Where(candidate => IsPlausibleAuditCandidate(candidate, track, remoteTitle, remoteArtist))
            .OrderBy(candidate => Math.Abs(candidate.DurationMs - track.DurationMs))
            .Take(5))
        {
            string assignedRemoteIds = string.Join(",", candidate.GetCloudIds().Where(result.RemoteTrackIds.Contains));
            Console.WriteLine(
                $"  LOCAL-CANDIDATE\t{candidate.Title}\t{candidate.Artist}\t{candidate.Album}\t" +
                $"{candidate.DurationMs}\tassigned={assignedRemoteIds}\t{candidate.FilePath}");
        }
    }
}

static bool IsPlausibleAuditCandidate(
    TrackModel candidate,
    NetEaseTrack remote,
    string remoteTitle,
    string remoteArtist)
{
    string localTitle = AuditKey(candidate.Title);
    string fileTitle = AuditKey(Path.GetFileNameWithoutExtension(candidate.FilePath));
    bool titleRelated = localTitle == remoteTitle || fileTitle == remoteTitle ||
        (Math.Min(localTitle.Length, remoteTitle.Length) >= 5 &&
            (localTitle.Contains(remoteTitle) || remoteTitle.Contains(localTitle))) ||
        (Math.Min(fileTitle.Length, remoteTitle.Length) >= 5 &&
            (fileTitle.Contains(remoteTitle) || remoteTitle.Contains(fileTitle)));
    if (!titleRelated)
    {
        return false;
    }
    bool artistRelated = remoteArtist.Length == 0 ||
        AuditKey(candidate.Artist + candidate.AlbumArtist).Contains(remoteArtist) ||
        remoteArtist.Contains(AuditKey(candidate.Artist));
    long durationDifference = candidate.DurationMs > 0 && remote.DurationMs > 0
        ? Math.Abs(candidate.DurationMs - remote.DurationMs)
        : long.MaxValue;
    return artistRelated || durationDifference <= 15000;
}

static string AuditKey(string value)
{
    return new string(value.Normalize(NormalizationForm.FormKC)
        .ToLowerInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record FakeSong(string Id, string Title, string Artist, string Album, long DurationMs);

internal sealed class OversizedPlaylistHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeclaredLengthContent(32L * 1024L * 1024L + 1L)
        });
    }
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

    protected override bool TryComputeLength(out long computedLength)
    {
        computedLength = _length;
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

    public PlaylistApiHandler(
        string playlistName,
        IReadOnlyList<FakeSong> songs,
        bool embedTracks,
        int maximumSuccessfulDetailBatch = int.MaxValue)
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
                tracks = embedTracks ? songs.Select(ToApiSong).ToArray() : Array.Empty<object>()
            }
        });
    }

    public int EmptyLargeDetailResponses { get; private set; }
    public int SuccessfulSmallDetailResponses { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
        if (uri.AbsolutePath.Contains("playlist/detail", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(JsonResponse(_playlistJson));
        }
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

    private static object ToApiSong(FakeSong song)
    {
        return new
        {
            id = long.Parse(song.Id),
            name = song.Title,
            ar = new[] { new { name = song.Artist } },
            al = new { name = song.Album },
            dt = song.DurationMs
        };
    }

    private static string[] ExtractRequestedIds(Uri uri)
    {
        string query = Uri.UnescapeDataString(uri.Query.TrimStart('?'));
        MatchCollection propertyMatches = IdPropertyRegex.Matches(query);
        IEnumerable<string> ids = propertyMatches.Count > 0
            ? propertyMatches.Select(match => match.Groups["id"].Value)
            : NumberRegex.Matches(query).Select(match => match.Value);
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}
