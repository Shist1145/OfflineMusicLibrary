using OfflineMusicLibrary;
using System.Text.Json;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

string testRoot = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary-LibraryScanChecks-" + Guid.NewGuid().ToString("N"));
string availableRoot = Path.Combine(testRoot, "available");
string unavailableRoot = Path.Combine(testRoot, "unavailable");
string newTrackPath = Path.Combine(availableRoot, "new-track.ncm");
string deletedTrackPath = Path.Combine(availableRoot, "deleted-track.ncm");
string offlineTrackPath = Path.Combine(unavailableRoot, "offline-track.ncm");
string outsideTrackPath = Path.Combine(testRoot, "outside", "outside-track.ncm");

try
{
	Directory.CreateDirectory(availableRoot);
	await File.WriteAllBytesAsync(newTrackPath, Array.Empty<byte>());

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
	List<TrackModel> existing = new List<TrackModel> { offlineTrack, deletedTrack, outsideTrack };

	MusicLibraryService service = new MusicLibraryService();
	List<TrackModel> mixedResult = await service.ScanAsync(new[] { availableRoot, unavailableRoot }, existing);

	Require(mixedResult.Count == 2, "A mixed scan should contain the newly scanned file and the preserved offline-root track.");
	TrackModel preserved = mixedResult.Single((TrackModel track) => track.Id == offlineTrack.Id);
	Require(preserved.IsFavorite && preserved.PlayCount == 17, "An offline-root track must keep its existing user state.");
	Require(mixedResult.Any((TrackModel track) => string.Equals(track.FilePath, newTrackPath, StringComparison.OrdinalIgnoreCase)),
		"Files under an available root must still be scanned.");
	Require(mixedResult.All((TrackModel track) => track.Id != deletedTrack.Id),
		"A missing file under an available root must still be removed.");
	Require(mixedResult.All((TrackModel track) => track.Id != outsideTrack.Id),
		"Tracks outside the configured roots must not be retained.");

	List<TrackModel> offlineOnlyResult = await service.ScanAsync(new[] { unavailableRoot }, new[] { offlineTrack });
	Require(offlineOnlyResult.Count == 1 && offlineOnlyResult[0].Id == offlineTrack.Id,
		"An entirely unavailable library root must preserve its previous index instead of returning an empty library.");

	if (args.Length > 0 && File.Exists(args[0]))
	{
		await using FileStream stream = File.OpenRead(args[0]);
		AppState state = await JsonSerializer.DeserializeAsync<AppState>(stream, new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		}) ?? throw new InvalidOperationException("Could not read the real library state.");
		int beforeTracks = state.Tracks.Count;
		int beforePlaylistEntries = state.Playlists.Sum((PlaylistModel playlist) => playlist.TrackIds.Count);
		List<TrackModel> realResult = await service.ScanAsync(state.LibraryFolders, state.Tracks);
		bool allRootsUnavailable = state.LibraryFolders.Count > 0 && state.LibraryFolders.All((string root) => !Directory.Exists(root));
		if (allRootsUnavailable)
		{
			Require(realResult.Count == beforeTracks,
				"An offline real library must retain every track in its previous index.");
			HashSet<string> retainedIds = realResult.Select((TrackModel track) => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
			Require(state.Playlists.SelectMany((PlaylistModel playlist) => playlist.TrackIds).All(retainedIds.Contains),
				"An offline real library must retain every locally recognized playlist entry.");
		}
		Console.WriteLine($"Real library read-only scan: {beforeTracks} -> {realResult.Count} tracks; {beforePlaylistEntries} playlist entries retained.");
	}

	Console.WriteLine("Library scan availability checks passed.");
}
finally
{
	if (Directory.Exists(testRoot))
	{
		Directory.Delete(testRoot, recursive: true);
	}
}
