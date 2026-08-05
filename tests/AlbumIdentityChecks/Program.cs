using System.Text.Json;
using OfflineMusicLibrary;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

TrackModel reportByCircle = new TrackModel
{
	Id = "report-circle",
	FilePath = @"G:\音乐\CloudMusic\VipSongsDownload\凋叶棕\報\track-a.mp3",
	Album = "報",
	Artist = "凋叶棕",
	AlbumArtist = "凋叶棕"
};
TrackModel reportByVocalist = new TrackModel
{
	Id = "report-vocalist",
	FilePath = @"G:\音乐\CloudMusic\VipSongsDownload\nayuta\報\track-b.mp3",
	Album = "報",
	Artist = "nayuta",
	AlbumArtist = "nayuta"
};

Require(AlbumIdentity.Create(reportByCircle) == AlbumIdentity.Create(reportByVocalist),
	"单字专辑不应再按曲目歌手文件夹拆分。");
Require(AlbumIdentity.Create(reportByCircle) == "album::報", "《報》应生成稳定的专辑名称键。");

TrackModel reportWithInvisibleFormatting = new TrackModel
{
	Id = "report-invisible-formatting",
	FilePath = @"G:\音乐\CloudMusic\VipSongsDownload\めらみぽっぷ\報\track-c.mp3",
	Album = "報\u200B\uFE0F",
	Artist = "めらみぽっぷ",
	AlbumArtist = "めらみぽっぷ"
};
Require(AlbumIdentity.Create(reportWithInvisibleFormatting) == AlbumIdentity.Create(reportByCircle),
	"零宽字符和变体选择符不应制造肉眼不可见的重复专辑。");

TrackModel unknownA = new TrackModel
{
	Id = "unknown-a",
	FilePath = @"G:\Artist A\Unknown Album\track-a.mp3",
	Album = "Unknown Album"
};
TrackModel unknownB = new TrackModel
{
	Id = "unknown-b",
	FilePath = @"G:\Artist B\Unknown Album\track-b.mp3",
	Album = "Unknown Album"
};
Require(AlbumIdentity.Create(unknownA) != AlbumIdentity.Create(unknownB),
	"未知专辑仍应按目录隔离，不能全库错误合并。");

string legacyFavorite = AlbumIdentity.FolderScopedFallback(reportByVocalist);
List<string> migratedFavorites = AlbumIdentity.MigrateFavoriteKeys(
	new[] { reportByCircle, reportByVocalist },
	new[] { legacyFavorite });
Require(migratedFavorites.Contains("album::報", StringComparer.OrdinalIgnoreCase),
	"旧的文件夹专辑收藏应迁移到合并后的专辑。");
Require(!migratedFavorites.Contains(legacyFavorite, StringComparer.OrdinalIgnoreCase),
	"迁移后不应残留导致无法取消收藏的旧键。");

if (args.Length > 0 && File.Exists(args[0]))
{
	await using FileStream stream = File.OpenRead(args[0]);
	AppState state = await JsonSerializer.DeserializeAsync<AppState>(stream, new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	}) ?? throw new InvalidOperationException("无法读取实际曲库状态。");
	foreach (string albumTitle in new[] { "報", "眇", "契", "縁" })
	{
		List<TrackModel> tracks = state.Tracks.Where(track => track.Album == albumTitle).ToList();
		Require(tracks.Count > 1, $"实际曲库中缺少《{albumTitle}》回归样本。");
		Require(tracks.Select(AlbumIdentity.Create).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1,
			$"实际曲库中的《{albumTitle}》仍被拆分。");
	}
}

Console.WriteLine("Album identity checks passed.");
