using OfflineMusicLibrary;

DateTime today = new(2026, 7, 18, 12, 0, 0);
List<TrackModel> library = Enumerable.Range(0, 72).Select(index => new TrackModel
{
	Id = $"track-{index:00}",
	FilePath = $"track-{index:00}.mp3",
	Title = $"Track {index:00}",
	Artist = $"Artist {index % 18:00}",
	Album = $"Album {index % 24:00}",
	Circle = $"Circle {index % 5:00}",
	Genre = $"Genre {index % 6:00}",
	Categories = [$"Mood {index % 8:00}"],
	DurationMs = (150 + index % 8 * 18) * 1000L,
	PlayCount = index % 9,
	LastPlayedAt = index % 5 == 0 ? null : today.AddDays(-(index % 45)),
	AddedAt = today.AddDays(-(20 + index * 3)),
	IsFavorite = index is 2 or 7 or 18 or 33
}).ToList();
library.Add(new TrackModel
{
	Id = "encrypted",
	FilePath = "encrypted.ncm",
	Title = "Encrypted",
	Artist = "Artist 00",
	Album = "Album 00",
	IsFavorite = true
});

RecommendationResult dailyA = RecommendationService.Create(library, RecommendationPreset.DailyRecommendation, today, refreshSalt: 0);
RecommendationResult dailyB = RecommendationService.Create(library, RecommendationPreset.DailyRecommendation, today, refreshSalt: 0);
Assert(dailyA.Tracks.Select(track => track.Id).SequenceEqual(dailyB.Tracks.Select(track => track.Id)), "每日推荐在同一天必须稳定");
RecommendationResult refreshedDaily = RecommendationService.Create(library, RecommendationPreset.DailyRecommendation, today, refreshSalt: 99);
Assert(!dailyA.Tracks.Select(track => track.Id).SequenceEqual(refreshedDaily.Tracks.Select(track => track.Id)), "每日推荐手动换一批后应产生不同顺序");

foreach (RecommendationPreset preset in Enum.GetValues<RecommendationPreset>())
{
	RecommendationResult result = RecommendationService.Create(library, preset, today, refreshSalt: 3);
	Assert(result.Tracks.Count <= RecommendationService.DefaultTrackCount, $"{preset} 不应超过默认数量");
	Assert(result.Tracks.Select(track => track.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Tracks.Count, $"{preset} 不应包含重复歌曲");
	Assert(result.Tracks.All(track => !track.IsEncryptedNcm), $"{preset} 不应推荐 NCM 加密文件");
	Assert(result.Tracks.All(track => !string.IsNullOrWhiteSpace(result.ReasonFor(track))), $"{preset} 的每首歌曲都应有推荐理由");
}

foreach (RecommendationPreset preset in new[] { RecommendationPreset.PersonalRadar, RecommendationPreset.DailyRecommendation, RecommendationPreset.PersonalRoam })
{
	Assert(RecommendationService.Create(library, preset, today, refreshSalt: 3).Tracks.Count == RecommendationService.DefaultTrackCount, $"{preset} 应生成默认数量的歌曲");
}

RecommendationResult rediscover = RecommendationService.Create(library, RecommendationPreset.RediscoverFavorites, today, refreshSalt: 3);
Assert(rediscover.Tracks.Count > 0, "很久没听应在已有收藏时生成歌曲");
Assert(rediscover.Tracks.All(track => track.IsFavorite), "很久没听应优先且仅使用收藏曲目");

RecommendationResult unplayed = RecommendationService.Create(library, RecommendationPreset.UnplayedGems, today, refreshSalt: 3);
Assert(unplayed.Tracks.Count > 0, "从未播放应找到与口味相关的零播放歌曲");
Assert(unplayed.Tracks.All(track => track.PlayCount == 0 && !track.LastPlayedAt.HasValue), "从未播放不得混入有播放记录的歌曲");

RecommendationResult expansion = RecommendationService.Create(library, RecommendationPreset.FavoriteExpansion, today, refreshSalt: 3);
Assert(expansion.Tracks.Count > 0, "收藏延伸应找到与收藏相连的候选");
Assert(expansion.Tracks.All(track => !track.IsFavorite), "收藏延伸只能展示未收藏歌曲");

RecommendationResult radio = RecommendationService.Create(library, RecommendationPreset.ThirtyMinuteRadio, today, refreshSalt: 3);
double radioMinutes = TimeSpan.FromMilliseconds(radio.TotalDurationMs).TotalMinutes;
Assert(radioMinutes >= 27 && radioMinutes <= 34, $"30 分钟电台时长应接近半小时，实际为 {radioMinutes:F1} 分钟");
Assert(radio.Tracks.Where(track => track.PlayCount == 0 && !track.IsFavorite).Count() <= (radio.Tracks.Count + 4) / 5, "30 分钟电台每五首最多穿插一首未听新歌");

RecommendationResult roam = RecommendationService.Create(library, RecommendationPreset.PersonalRoam, today, refreshSalt: 4);
Assert(roam.Tracks.Take(18).Select(track => track.Artist).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() >= 12, "私人漫游前段应保持艺术家多样性");

RecommendationResult radarA = RecommendationService.Create(library, RecommendationPreset.PersonalRadar, today, refreshSalt: 1);
RecommendationResult radarB = RecommendationService.Create(library, RecommendationPreset.PersonalRadar, today, refreshSalt: 2);
Assert(!radarA.Tracks.Select(track => track.Id).SequenceEqual(radarB.Tracks.Select(track => track.Id)), "私人雷达换一批后应产生不同顺序");

List<TrackModel> safetyLibrary =
[
	Track("favorite", "Anchor Artist", "Favorite Album", "Anchor Circle", "Rock", favorite: true, plays: 12),
	Track("same-artist", "Anchor Artist", "Other Album", "Other Circle", "Jazz"),
	Track("same-album", "Other Artist", "Favorite Album", "Other Circle", "Jazz"),
	Track("same-circle", "Other Artist 2", "Other Album 2", "Anchor Circle", "Jazz"),
	Track("same-tag", "Other Artist 3", "Other Album 3", "Other Circle", "Rock"),
	Track("unrelated", "Stranger", "Unknown Album", "Unknown Circle", "Metal")
];
RecommendationResult safeExpansion = RecommendationService.Create(safetyLibrary, RecommendationPreset.FavoriteExpansion, today, refreshSalt: 0);
HashSet<string> safeIds = safeExpansion.Tracks.Select(track => track.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
Assert(!safeIds.Contains("unrelated"), "收藏延伸不得混入无法和收藏建立联系的歌曲");
Assert(safeIds.IsSupersetOf(["same-artist", "same-album", "same-circle", "same-tag"]), "收藏延伸应识别艺术家、专辑、社团和标签连接");

List<TrackModel> implicitLikeLibrary =
[
	Track("playlist-like", "Playlist Artist", "Playlist Album", "Playlist Circle", "Ambient", plays: 0),
	Track("playlist-neighbor", "Playlist Artist", "Another Album", "Another Circle", "Noise", plays: 0),
	Track("playlist-unrelated", "Unrelated", "Unrelated", "Unrelated", "Metal", plays: 0)
];
RecommendationResult implicitExpansion = RecommendationService.Create(
	implicitLikeLibrary,
	RecommendationPreset.FavoriteExpansion,
	today,
	implicitFavoriteTrackIds: ["playlist-like"]);
Assert(implicitExpansion.Tracks.Any(track => track.Id == "playlist-neighbor"), "喜欢的音乐歌单应能作为隐式收藏建立推荐连接");
Assert(implicitExpansion.Tracks.All(track => track.Id != "playlist-like" && track.Id != "playlist-unrelated"), "收藏延伸不应推荐隐式收藏本身或无关歌曲");

List<TrackModel> blankProfile =
[
	Track("blank-a", "A", "A", "A", "A"),
	Track("blank-b", "B", "B", "B", "B")
];
Assert(RecommendationService.Create(blankProfile, RecommendationPreset.UnplayedGems, today).Tracks.Count == 0, "没有任何口味信号时，从未播放宁可不推荐");
Assert(RecommendationService.Create(blankProfile, RecommendationPreset.FavoriteExpansion, today).Tracks.Count == 0, "没有任何口味信号时，收藏延伸宁可不推荐");

string historyJsonPath = Path.Combine(Path.GetTempPath(), $"offline-music-history-{Guid.NewGuid():N}.json");
string historyCsvPath = Path.Combine(Path.GetTempPath(), $"offline-music-history-{Guid.NewGuid():N}.csv");
string historyOversizedPath = Path.Combine(Path.GetTempPath(), $"offline-music-history-{Guid.NewGuid():N}.json");
try
{
	List<TrackModel> historyLibrary =
	[
		new TrackModel { Id = "cloud", CloudId = "123", FilePath = "cloud.mp3", Title = "Cloud Song", Artist = "Singer", Album = "Album", PlayCount = 2 },
		new TrackModel { Id = "fuzzy", FilePath = "fuzzy.mp3", Title = "夜曲", Artist = "周杰伦", Album = "十一月的萧邦", PlayCount = 8 },
		new TrackModel { Id = "comma", FilePath = "comma.mp3", Title = "Comma, Song", Artist = "CSV Artist", Album = "CSV Album" },
		new TrackModel { Id = "ambiguous-a", FilePath = "a.mp3", Title = "同名歌", Artist = "同一歌手", Album = "甲" },
		new TrackModel { Id = "ambiguous-b", FilePath = "b.mp3", Title = "同名歌", Artist = "同一歌手", Album = "乙" }
	];
	string historyJson = """
	{
	  "weekData": [
	    { "playCount": 999, "song": { "id": 123, "name": "Cloud Song", "ar": [{ "name": "Singer" }] } }
	  ],
	  "allData": [
	    { "playCount": 12, "lastPlayTime": 1784386800000, "song": { "id": 123, "name": "Cloud Song", "ar": [{ "name": "Singer" }], "al": { "name": "Album" } } },
	    { "playCount": 3, "song": { "id": 456, "name": "夜曲", "ar": [{ "name": "周杰伦" }], "al": { "name": "十一月的萧邦" } } },
	    { "playCount": 4, "song": { "id": 789, "name": "同名歌", "ar": [{ "name": "同一歌手" }] } },
	    { "playCount": 5, "song": { "id": 999, "name": "本地没有", "ar": [{ "name": "陌生人" }] } }
	  ]
	}
	""";
	await File.WriteAllTextAsync(historyJsonPath, historyJson);
	NetEaseHistoryImportResult firstHistoryImport = await NetEaseHistoryImportService.ImportAsync(historyJsonPath, historyLibrary);
	Assert(firstHistoryImport.SourceRecordCount == 4, "存在 allData 时应忽略 weekData，避免重复计算");
	Assert(firstHistoryImport.ExactMatchCount == 1 && firstHistoryImport.FuzzyMatchCount == 1, "历史导入应优先云 ID，并允许保守的歌名/艺人匹配");
	Assert(firstHistoryImport.AmbiguousCount == 1 && firstHistoryImport.Unmatched.Count == 2, "同名且无法区分的歌曲不得误配");
	Assert(historyLibrary[0].PlayCount == 12 && historyLibrary[1].PlayCount == 8, "导入只能提高可信播放次数，不能用较小值覆盖本地历史");
	Assert(historyLibrary[1].HasCloudId("456"), "模糊匹配成功后应记住网易云歌曲 ID");
	NetEaseHistoryImportResult repeatedHistoryImport = await NetEaseHistoryImportService.ImportAsync(historyJsonPath, historyLibrary);
	Assert(repeatedHistoryImport.PlayCountIncrease == 0, "重复导入同一份历史不得重复累加播放次数");

	await File.WriteAllTextAsync(historyCsvPath, "歌名,歌手,专辑,播放次数,最近播放\n\"Comma, Song\",CSV Artist,CSV Album,7,2026-07-18 12:00:00");
	NetEaseHistoryImportResult csvHistoryImport = await NetEaseHistoryImportService.ImportAsync(historyCsvPath, historyLibrary);
	await using (FileStream oversizedHistory = new(historyOversizedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
	{
		oversizedHistory.SetLength(64L * 1024L * 1024L + 1L);
	}
	bool oversizedHistoryRejected = false;
	try
	{
		_ = await NetEaseHistoryImportService.ImportAsync(historyOversizedPath, historyLibrary);
	}
	catch (InvalidDataException)
	{
		oversizedHistoryRejected = true;
	}
	Assert(oversizedHistoryRejected, "超大播放历史文件必须在完整读入内存前被拒绝。");
	Assert(csvHistoryImport.MatchedRecordCount == 1 && historyLibrary[2].PlayCount == 7, "CSV 导入应正确处理带逗号的引号字段");
	Assert(historyLibrary[2].LastPlayedAt.HasValue, "CSV 中的最近播放时间应写入本地历史");
}
finally
{
	if (File.Exists(historyJsonPath))
		File.Delete(historyJsonPath);
	if (File.Exists(historyCsvPath))
		File.Delete(historyCsvPath);
	if (File.Exists(historyOversizedPath))
		File.Delete(historyOversizedPath);
}

Console.WriteLine("Recommendation and NetEase history checks passed.");

static TrackModel Track(string id, string artist, string album, string circle, string genre, bool favorite = false, int plays = 0)
{
	return new TrackModel
	{
		Id = id,
		FilePath = id + ".mp3",
		Title = id,
		Artist = artist,
		Album = album,
		Circle = circle,
		Genre = genre,
		IsFavorite = favorite,
		PlayCount = plays,
		DurationMs = 3 * 60 * 1000
	};
}

static void Assert(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}
