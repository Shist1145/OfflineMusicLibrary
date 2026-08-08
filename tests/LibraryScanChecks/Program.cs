using OfflineMusicLibrary;
using System.Diagnostics;
using System.Text.Json;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

static TrackModel CachedTrack(string path, string title, bool includeStamp = true)
{
	FileInfo info = new FileInfo(path);
	return new TrackModel
	{
		Id = MusicLibraryService.CreateTrackId(path),
		FilePath = path,
		FileSize = includeStamp ? info.Length : 0,
		LastWriteTimeUtcTicks = includeStamp ? info.LastWriteTimeUtc.Ticks : 0,
		Title = title,
		Artist = "Cached artist",
		Album = "Cached album"
	};
}

string testRoot = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary-LibraryScanChecks-" + Guid.NewGuid().ToString("N"));
string availableRoot = Path.Combine(testRoot, "available");
string unavailableRoot = Path.Combine(testRoot, "unavailable");
string newTrackPath = Path.Combine(availableRoot, "new-track.ncm");
string unchangedTrackPath = Path.Combine(availableRoot, "unchanged - artist.ncm");
string legacyTrackPath = Path.Combine(availableRoot, "legacy - artist.ncm");
string modifiedTrackPath = Path.Combine(availableRoot, "modified - artist.ncm");
string deletedTrackPath = Path.Combine(availableRoot, "deleted-track.ncm");
string offlineTrackPath = Path.Combine(unavailableRoot, "offline-track.ncm");
string outsideTrackPath = Path.Combine(testRoot, "outside", "outside-track.ncm");

try
{
	Directory.CreateDirectory(availableRoot);
	await File.WriteAllBytesAsync(newTrackPath, Array.Empty<byte>());
	await File.WriteAllBytesAsync(unchangedTrackPath, [1, 2, 3]);
	await File.WriteAllBytesAsync(legacyTrackPath, [4, 5, 6]);
	await File.WriteAllBytesAsync(modifiedTrackPath, [7, 8, 9]);

	TrackModel unchangedTrack = CachedTrack(unchangedTrackPath, "Cached unchanged title");
	unchangedTrack.PlayCount = 23;
	TrackModel legacyTrack = CachedTrack(legacyTrackPath, "Legacy metadata is reused once", includeStamp: false);
	TrackModel modifiedTrack = CachedTrack(modifiedTrackPath, "Stale title before modification");
	await File.AppendAllBytesAsync(modifiedTrackPath, [10]);

	TrackModel offlineTrack = new TrackModel
	{
		Id = MusicLibraryService.CreateTrackId(offlineTrackPath),
		FilePath = offlineTrackPath,
		Title = "Offline track",
		Artist = "Preserved artist",
		IsFavorite = true,
		PlayCount = 17
	};
	TrackModel deletedTrack = new TrackModel
	{
		Id = MusicLibraryService.CreateTrackId(deletedTrackPath),
		FilePath = deletedTrackPath,
		Title = "Actually deleted track"
	};
	TrackModel outsideTrack = new TrackModel
	{
		Id = MusicLibraryService.CreateTrackId(outsideTrackPath),
		FilePath = outsideTrackPath,
		Title = "Outside configured roots"
	};
	List<TrackModel> existing =
	[
		offlineTrack,
		deletedTrack,
		outsideTrack,
		unchangedTrack,
		legacyTrack,
		modifiedTrack
	];

	MusicLibraryService service = new MusicLibraryService();
	List<TrackModel> mixedResult = await service.ScanAsync([availableRoot, unavailableRoot], existing);

	Require(mixedResult.Count == 5, "A mixed scan should contain four available files and the preserved offline-root track.");
	TrackModel preserved = mixedResult.Single(track => track.Id == offlineTrack.Id);
	Require(preserved.IsFavorite && preserved.PlayCount == 17, "An offline-root track must keep its existing user state.");
	Require(mixedResult.Any(track => string.Equals(track.FilePath, newTrackPath, StringComparison.OrdinalIgnoreCase)),
		"A newly added file under an available root must be indexed.");
	Require(mixedResult.All(track => track.Id != deletedTrack.Id),
		"A missing file under an available root must still be removed.");
	Require(mixedResult.All(track => track.Id != outsideTrack.Id),
		"Tracks outside the configured roots must not be retained.");

	TrackModel reused = mixedResult.Single(track => track.Id == unchangedTrack.Id);
	Require(reused.Title == "Cached unchanged title" && reused.PlayCount == 23,
		"An unchanged stamped file must reuse cached metadata and user state.");
	Require(!ReferenceEquals(reused, unchangedTrack),
		"Incremental scanning must return a clone so cancellation cannot mutate the active state in place.");
	TrackModel seededLegacy = mixedResult.Single(track => track.Id == legacyTrack.Id);
	Require(seededLegacy.Title == "Legacy metadata is reused once" && seededLegacy.FileSize > 0 && seededLegacy.LastWriteTimeUtcTicks > 0,
		"A pre-1.6.2 cached track should seed its file stamp without forcing a full metadata reread.");
	TrackModel refreshed = mixedResult.Single(track => track.Id == modifiedTrack.Id);
	Require(refreshed.Title == "modified",
		"A changed file stamp must trigger metadata rereading instead of retaining stale cached fields.");

	List<TrackModel> forced = await service.ScanAsync(
		[availableRoot],
		[seededLegacy],
		forceMetadataRefresh: true);
	Require(forced.Single(track => track.Id == legacyTrack.Id).Title == "legacy",
		"A forced metadata refresh must reread even an unchanged stamped file.");

	List<TrackModel> offlineOnlyResult = await service.ScanAsync([unavailableRoot], [offlineTrack]);
	Require(offlineOnlyResult.Count == 1 && offlineOnlyResult[0].Id == offlineTrack.Id,
		"An entirely unavailable library root must preserve its previous index instead of returning an empty library.");

	TrackModel cancellationSentinel = CachedTrack(unchangedTrackPath, "Cancellation sentinel", includeStamp: false);
	using (CancellationTokenSource cancelled = new CancellationTokenSource())
	{
		cancelled.Cancel();
		try
		{
			_ = await service.ScanAsync([availableRoot], [cancellationSentinel], cancellationToken: cancelled.Token);
			throw new InvalidOperationException("A pre-cancelled scan must throw OperationCanceledException.");
		}
		catch (OperationCanceledException)
		{
		}
	}
	Require(cancellationSentinel.FileSize == 0 && cancellationSentinel.LastWriteTimeUtcTicks == 0,
		"A cancelled scan must not mutate the caller's active track objects.");

	if (args.Length > 0 && File.Exists(args[0]))
	{
		await using FileStream stream = File.OpenRead(args[0]);
		AppState state = await JsonSerializer.DeserializeAsync<AppState>(stream, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		}) ?? throw new InvalidOperationException("Could not read the real library state.");
		int beforeTracks = state.Tracks.Count;
		int beforePlaylistEntries = state.Playlists.Sum(playlist => playlist.TrackIds.Count);
		Stopwatch timer = Stopwatch.StartNew();
		List<TrackModel> realResult = await service.ScanAsync(state.LibraryFolders, state.Tracks);
		timer.Stop();
		bool allRootsUnavailable = state.LibraryFolders.Count > 0 && state.LibraryFolders.All(root => !Directory.Exists(root));
		if (allRootsUnavailable)
		{
			Require(realResult.Count == beforeTracks,
				"An offline real library must retain every track in its previous index.");
			HashSet<string> retainedIds = realResult.Select(track => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
			Require(state.Playlists.SelectMany(playlist => playlist.TrackIds).All(retainedIds.Contains),
				"An offline real library must retain every locally recognized playlist entry.");
		}
		Console.WriteLine($"Real library read-only incremental scan: {beforeTracks} -> {realResult.Count} tracks in {timer.Elapsed.TotalSeconds:N1}s; {beforePlaylistEntries} playlist entries untouched.");
	}

	Console.WriteLine("Library scan availability, incrementality, refresh, and cancellation checks passed.");
}
finally
{
	if (Directory.Exists(testRoot))
	{
		Directory.Delete(testRoot, recursive: true);
	}
}
