using OfflineMusicLibrary;
using System.Text.Json;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

TrackModel kept = new TrackModel
{
	Id = "local-kept",
	Title = "仍在歌单",
	CloudId = "100"
};
TrackModel removedRemotely = new TrackModel
{
	Id = "local-removed-remotely",
	Title = "云端已删除",
	CloudId = "200"
};
TrackModel newlyMatched = new TrackModel
{
	Id = "local-new",
	Title = "新匹配歌曲",
	CloudId = "300"
};
List<TrackModel> library = new List<TrackModel> { kept, removedRemotely, newlyMatched };

PlaylistModel dirty = new PlaylistModel
{
	Name = "需要清理",
	TrackIds = new List<string>
	{
		"local-kept",
		"LOCAL-KEPT",
		"",
		"missing-local-id",
		"local-removed-remotely"
	}
};
PlaylistCleanupResult cleanup = PlaylistMaintenance.Clean(dirty, library);
Require(cleanup.Changed && cleanup.CurrentCount == 2, "清理后只应保留两个有效且唯一的本地曲目 ID。");
Require(cleanup.RemovedDuplicate == 1 && cleanup.RemovedBlank == 1 && cleanup.RemovedMissing == 1,
	"应分别统计重复、空白与失效记录。");
Require(dirty.TrackIds.SequenceEqual(new[] { "local-kept", "local-removed-remotely" }, StringComparer.OrdinalIgnoreCase),
	"清理必须保留有效歌曲的原始顺序。");

List<string> completeSync = PlaylistMaintenance.BuildSynchronizedTrackIds(
	dirty.TrackIds,
	new[] { newlyMatched },
	new[] { "100", "300", "999" },
	hasCompleteTrackIds: true,
	library);
Require(completeSync.SequenceEqual(new[] { "local-new", "local-kept" }, StringComparer.OrdinalIgnoreCase),
	"取得完整云端 ID 后，应移除云端已删除的旧歌曲，并保留详情暂缺但云 ID 仍存在的本地歌曲。");

List<string> incompleteSync = PlaylistMaintenance.BuildSynchronizedTrackIds(
	dirty.TrackIds,
	new[] { newlyMatched },
	new[] { "300" },
	hasCompleteTrackIds: false,
	library);
Require(incompleteSync.SequenceEqual(
	new[] { "local-new", "local-kept", "local-removed-remotely" },
	StringComparer.OrdinalIgnoreCase),
	"云端 ID 列表不完整时，应保留仍能在本地曲库中识别的旧歌曲。");

if (args.Length > 0 && File.Exists(args[0]))
{
	await using FileStream stream = File.OpenRead(args[0]);
	AppState state = await JsonSerializer.DeserializeAsync<AppState>(stream, new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	}) ?? throw new InvalidOperationException("无法读取实际曲库状态。");
	int before = state.Playlists.Sum(playlist => playlist.TrackIds.Count);
	int removed = 0;
	foreach (PlaylistModel playlist in state.Playlists)
	{
		removed += PlaylistMaintenance.Clean(playlist, state.Tracks).RemovedCount;
	}
	int after = state.Playlists.Sum(playlist => playlist.TrackIds.Count);
	Require(before - after == removed, "实际曲库清理统计应与歌单数量变化一致。");
	Require(state.Playlists.SelectMany(playlist => playlist.TrackIds).All(id =>
		state.Tracks.Any(track => string.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase))),
		"实际曲库清理后不应残留无法识别的曲目 ID。");
	Console.WriteLine($"Actual library cleanup preview: {before} -> {after}, removed {removed} stale entries.");
}

Console.WriteLine("Playlist maintenance checks passed.");
