using OfflineMusicLibrary;
using System.Text.Json;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

AppState normalState = new()
{
	PlayerPageMode = PlayerPageModes.Vinyl,
	VisualizationMode = "Spectrum",
	AudioBackend = "Wasapi",
	SpatialAudioMode = "StereoWide",
	EqualizerPreset = "Rock",
	HardwareDecoding = "D3D11VA",
	VideoOutput = "Direct3D11"
};
PlaybackEngineProfile normalProfile = PlaybackStabilityService.Resolve(normalState, isVideo: false);
Require(normalProfile.VisualizationMode == "Spectrum" &&
	normalProfile.AudioBackend == "Wasapi" &&
	normalProfile.SpatialAudioMode == "StereoWide" &&
	normalProfile.EqualizerPreset == "Rock" &&
	normalProfile.HardwareDecoding == "D3D11VA" &&
	normalProfile.CacheMilliseconds == 1500,
	"普通播放模式应保留用户选择的引擎参数。");
normalState.PlaybackWatchdogTimeoutSeconds = 12;
Require(PlaybackStabilityService.EffectiveWatchdogTimeoutSeconds(normalState, 0) == 12 &&
	PlaybackStabilityService.EffectiveWatchdogTimeoutSeconds(normalState, 1) == 17 &&
	PlaybackStabilityService.EffectiveWatchdogTimeoutSeconds(normalState, 3) == 27 &&
	PlaybackStabilityService.EffectiveWatchdogTimeoutSeconds(normalState, 99) == 27,
	"连续恢复后监控超时应逐步增加，并把额外宽限限制在十五秒以内。");
normalState.PlaybackWatchdogTimeoutSeconds = 99;
Require(PlaybackStabilityService.EffectiveWatchdogTimeoutSeconds(normalState, 0) == 30 &&
	PlaybackStabilityService.EffectiveWatchdogTimeoutSeconds(normalState, 3) == 45,
	"监控基础超时仍应限制在八到三十秒，再叠加恢复宽限。");
Require(!PlaybackStabilityService.HasStableProgressAfterRecovery(4999, 0) &&
	PlaybackStabilityService.HasStableProgressAfterRecovery(5000, 0) &&
	PlaybackStabilityService.HasStableProgressAfterRecovery(8300, 3300) &&
	!PlaybackStabilityService.HasStableProgressAfterRecovery(9000, -1),
	"恢复后只有播放位置稳定前进至少五秒，才应重置连续恢复额度。");
Require(PlayerPageModes.Normalize("Vinyl") == PlayerPageModes.Vinyl &&
	PlayerPageModes.Normalize("Lyrics") == PlayerPageModes.Lyrics &&
	PlayerPageModes.Normalize("unknown") == PlayerPageModes.Standard,
	"播放页面模式应只接受标准、黑胶和歌词三种稳定值。");

TrackModel currentTrack = new()
{
	Id = "current",
	Title = "Current",
	Artist = "Artist A",
	Album = "Album A",
	Circle = "Circle A",
	Genre = "Rock",
	Categories = ["Game"]
};
TrackModel sameAlbumTrack = new()
{
	Id = "same-album",
	Title = "Same Album",
	Artist = "Artist B",
	Album = "Album A",
	Categories = ["Other"]
};
TrackModel sameCategoryTrack = new()
{
	Id = "same-category",
	Title = "Same Category",
	Artist = "Artist C",
	Album = "Album C",
	Categories = ["Game"]
};
TrackModel unrelatedTrack = new()
{
	Id = "unrelated",
	Title = "Unrelated",
	Artist = "Artist D",
	Album = "Album D"
};
IReadOnlyList<SimilarTrackSuggestion> similar = PlayerPageService.FindSimilarTracks(
	[currentTrack, unrelatedTrack, sameCategoryTrack, sameAlbumTrack], currentTrack, 3);
Require(similar.Count == 3 && similar[0].Track.Id == "same-album" && similar[1].Track.Id == "same-category",
	"相似推荐应优先同专辑，再考虑共享分类等较弱关联。");
Require(PlayerPageService.FindSimilarTracks([currentTrack, sameAlbumTrack], currentTrack, 0).Count == 0,
	"相似推荐请求数量为零时不应意外返回歌曲。");
normalState.VisualizationMode = "Off";
Require(!PlayerPageService.RequiresVideoSurface(normalState, currentTrack),
	"普通音频且关闭可视化时应使用沉浸式页面。");
normalState.VisualizationMode = "Spectrum";
Require(PlayerPageService.RequiresVideoSurface(normalState, currentTrack),
	"音频可视化开启时应保留原视频承载页面。");
normalState.SafePlaybackMode = true;
Require(!PlayerPageService.RequiresVideoSurface(normalState, currentTrack),
	"安全播放模式应禁用音频可视化承载页面。");
Require(PlayerPageService.RequiresVideoSurface(normalState, new TrackModel { Id = "video", IsVideo = true }),
	"视频媒体无论安全模式如何都必须保留视频画面。");
Require(PlayerPageService.TonearmAngle(-1000, 10000) == -22.0 && PlayerPageService.TonearmAngle(20000, 10000) == -7.0,
	"唱臂角度必须把异常播放进度限制在唱片范围内。");

normalState.VisualizationMode = "Spectrum";
PlaybackEngineProfile safeProfile = PlaybackStabilityService.Resolve(normalState, isVideo: false);
Require(safeProfile.VisualizationMode == "Off" &&
	safeProfile.AudioBackend == "DirectSound" &&
	safeProfile.SpatialAudioMode == "Off" &&
	safeProfile.EqualizerPreset == "Off" &&
	safeProfile.HardwareDecoding == "Disabled" &&
	safeProfile.VideoOutput == "Auto" &&
	safeProfile.CacheMilliseconds == 2500,
	"安全播放模式应使用稳定优先的实际引擎参数。");

string temporaryDirectory = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary-StabilityChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
	AppStore store = new(temporaryDirectory);
	AppState legacyState = new()
	{
		Volume = 57,
		PlayerPageMode = PlayerPageModes.Vinyl,
		StateBackupEnabled = true
	};
	await File.WriteAllTextAsync(store.LegacyStatePath, JsonSerializer.Serialize(legacyState));
	AppState migrated = await store.LoadAsync();
	Require(migrated.Volume == 57 && migrated.PlayerPageMode == PlayerPageModes.Vinyl,
		"首次启动新版时应完整迁移旧 library.json。 ");
	Require(File.Exists(store.StatePath) && File.Exists(store.StateBackupPath),
		"旧状态迁移后应立即生成隔离的 v2 主文件与备份。");

	AppState savedState = new()
	{
		Volume = 64,
		PlayerPageMode = PlayerPageModes.Lyrics,
		DesktopLyricsColorScheme = "Custom",
		DesktopLyricsPrimaryColor = "#123456",
		DesktopLyricsSecondaryColor = "#ABCDEF",
		DesktopLyricsRomanizationColor = "",
		DesktopLyricsTranslationColor = "",
		PlaybackWatchdogTimeoutSeconds = 99,
		PlaybackRecoveryAttempts = 0,
		StateBackupEnabled = true
	};
	await store.SaveAsync(savedState);
	Require(File.Exists(store.StatePath), "保存后应存在主状态文件。");
	Require(File.Exists(store.StateBackupPath), "启用状态保护后应生成可恢复备份。");
	await File.WriteAllTextAsync(store.LegacyStatePath, JsonSerializer.Serialize(new AppState
	{
		Volume = 12,
		PlayerPageMode = PlayerPageModes.Standard
	}));
	AppState isolated = await store.LoadAsync();
	Require(isolated.Volume == 64 && isolated.PlayerPageMode == PlayerPageModes.Lyrics,
		"v2 状态存在后，旧版本再次写入 library.json 不得覆盖新版状态。");

	await File.WriteAllTextAsync(store.StatePath, "{ invalid json");
	AppState recovered = await store.LoadAsync();
	Require(recovered.Volume == 64, "主状态损坏时应从最近有效备份恢复，而不是返回空白默认状态。");
	Require(recovered.PlayerPageMode == PlayerPageModes.Lyrics, "沉浸式播放页面模式应随状态备份恢复。");
	Require(recovered.DesktopLyricsRomanizationColor == "#ABCDEF" && recovered.DesktopLyricsTranslationColor == "#123456",
		"旧配置应迁移为独立的音译与翻译颜色。");
	Require(recovered.PlaybackWatchdogTimeoutSeconds == 30 && recovered.PlaybackRecoveryAttempts == 1,
		"稳定性参数载入时应限制在安全范围内。");
	Require(Directory.EnumerateFiles(temporaryDirectory, "library-v2.invalid-*.json").Any(),
		"损坏的主状态文件应被保留以便诊断。");

	AppStore secondStore = new(temporaryDirectory);
	Task[] concurrentSaves = Enumerable.Range(0, 8)
		.Select(index => (index % 2 == 0 ? store : secondStore).SaveAsync(new AppState
		{
			Volume = 40 + index,
			StateBackupEnabled = true
		}))
		.ToArray();
	await Task.WhenAll(concurrentSaves);
	AppState concurrentResult = await store.LoadAsync();
	Require(concurrentResult.Volume is >= 40 and <= 47,
		"多个播放器实例接近同时保存时，最终状态文件仍应是完整 JSON。");
	Require(!Directory.EnumerateFiles(temporaryDirectory, "*.tmp").Any(),
		"并发保存不应遗留共享临时文件。");

	Console.WriteLine("Stability and state recovery checks passed.");
}
finally
{
	Directory.Delete(temporaryDirectory, recursive: true);
}
