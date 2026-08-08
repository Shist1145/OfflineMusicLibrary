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

static async Task<AppState> ReadStateAsync(string path)
{
	await using FileStream stream = File.OpenRead(path);
	return await JsonSerializer.DeserializeAsync<AppState>(stream, new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	}) ?? throw new InvalidOperationException("Could not deserialize saved state.");
}

static async Task RequireStateSaveFailureAsync(Func<Task> action, string message)
{
	try
	{
		await action();
	}
	catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
	{
		return;
	}
	throw new InvalidOperationException(message);
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

string mutexName = @"Local\OfflineMusicLibrary-StabilityChecks-" + Guid.NewGuid().ToString("N");
using (SingleInstanceService firstInstance = new SingleInstanceService(mutexName))
using (SingleInstanceService secondInstance = new SingleInstanceService(mutexName))
{
	Require(firstInstance.TryAcquire(), "The first application instance must acquire the named mutex.");
	Require(!secondInstance.TryAcquire(), "A second application instance must be rejected before it can create player resources.");
	firstInstance.Dispose();
	using SingleInstanceService replacementInstance = new SingleInstanceService(mutexName);
	Require(replacementInstance.TryAcquire(), "The mutex must become available after the owning instance exits.");
}

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
		"首次启动新版时应完整迁移旧 library.json。");
	Require(File.Exists(store.StatePath) && File.Exists(store.StateBackupPath),
		"首次创建 v2 状态时应立即生成主文件与初始恢复副本。");

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
	AppState firstBackup = await ReadStateAsync(store.StateBackupPath);
	Require(firstBackup.Volume == 57,
		"第二次保存后 backup 必须保留上一代状态，不能复制刚写入的新主文件。");
	Require(File.Exists(store.StatePreviousPath),
		"启用状态保护时应保留第二级 previous 恢复点。");

	AppState newestState = new()
	{
		Volume = 65,
		PlayerPageMode = PlayerPageModes.Standard,
		StateBackupEnabled = true
	};
	await store.SaveAsync(newestState);
	AppState mainAfterThirdSave = await ReadStateAsync(store.StatePath);
	AppState backupAfterThirdSave = await ReadStateAsync(store.StateBackupPath);
	AppState previousAfterThirdSave = await ReadStateAsync(store.StatePreviousPath);
	Require(mainAfterThirdSave.Volume == 65 && backupAfterThirdSave.Volume == 64 && previousAfterThirdSave.Volume == 57,
		"三代状态应按 main=最新、backup=上一代、previous=上上代轮换。");
	Require(!File.ReadAllBytes(store.StatePath).SequenceEqual(File.ReadAllBytes(store.StateBackupPath)),
		"主文件与 backup 不应再是同一份镜像。");

	await File.WriteAllTextAsync(store.LegacyStatePath, JsonSerializer.Serialize(new AppState
	{
		Volume = 12,
		PlayerPageMode = PlayerPageModes.Standard
	}));
	AppState isolated = await store.LoadAsync();
	Require(isolated.Volume == 65,
		"v2 状态存在后，旧版本再次写入 library.json 不得覆盖新版状态。");

	await File.WriteAllTextAsync(store.StatePath, "{ invalid json");
	AppState recovered = await store.LoadAsync();
	Require(recovered.Volume == 64, "主状态损坏时应从上一代 backup 恢复，而不是返回空白状态。");
	Require(recovered.PlayerPageMode == PlayerPageModes.Lyrics, "沉浸式播放页面模式应随上一代备份恢复。");
	Require(recovered.DesktopLyricsRomanizationColor == "#ABCDEF" && recovered.DesktopLyricsTranslationColor == "#123456",
		"旧配置应迁移为独立的音译与翻译颜色。");
	Require(recovered.PlaybackWatchdogTimeoutSeconds == 30 && recovered.PlaybackRecoveryAttempts == 1,
		"稳定性参数载入时应限制在安全范围内。");
	Require(Directory.EnumerateFiles(temporaryDirectory, "library-v2.invalid-*.json").Any(),
		"损坏的主状态文件应被保留以便诊断。");

	await File.WriteAllTextAsync(store.StateBackupPath, "{ invalid backup");
	AppState secondGenerationRecovery = await store.LoadAsync();
	Require(secondGenerationRecovery.Volume == 57,
		"主文件与 backup 同时损坏时应继续尝试 previous，而不是丢失整个曲库。");

	await store.SaveAsync(new AppState
	{
		Volume = 66,
		StateBackupEnabled = true
	});
	using (FileStream heldPrimary = new FileStream(store.StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
	{
		await RequireStateSaveFailureAsync(
			() => store.SaveAsync(new AppState { Volume = 99, StateBackupEnabled = true }),
			"A locked primary file must make SaveAsync fail instead of silently reporting success.");
	}
	Require((await store.LoadAsync()).Volume == 66,
		"A failed replacement must leave the last complete main state readable.");

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
	AppState concurrentBackup = await ReadStateAsync(store.StateBackupPath);
	_ = await ReadStateAsync(store.StatePreviousPath);
	Require(concurrentResult.Volume is >= 40 and <= 47,
		"多个保存方接近同时写入时，最终主状态仍应是完整 JSON。");
	Require(concurrentBackup.Volume is >= 40 and <= 47 && concurrentBackup.Volume != concurrentResult.Volume,
		"并发保存后 backup 仍应是一份不同于主文件的完整上一代状态。");
	Require(!Directory.EnumerateFiles(temporaryDirectory, "*.tmp").Any(),
		"正常保存、失败保存与并发保存都不应遗留本次临时文件。");

	string serializedTrack = JsonSerializer.Serialize(new TrackModel
	{
		Id = "json-shape",
		FilePath = @"C:\Music\json-shape.flac",
		Title = "JSON Shape",
		Artist = "Regression",
		Album = "Stability"
	});
	foreach (string computedProperty in new[]
	{
		"DurationText",
		"TrackText",
		"CategoryText",
		"LastPlayedText",
		"PlayCountText",
		"MediaTypeText",
		"CircleText",
		"SearchText"
	})
	{
		Require(!serializedTrack.Contains($"\"{computedProperty}\"", StringComparison.Ordinal),
			$"纯界面字段 {computedProperty} 不得写入状态文件。");
	}

	string performanceDirectory = Path.Combine(temporaryDirectory, "large-state");
	Directory.CreateDirectory(performanceDirectory);
	AppStore performanceStore = new(performanceDirectory);
	AppState performanceState = new() { StateBackupEnabled = true };
	string pathPadding = new('x', 360);
	for (int index = 0; index < 6000; index++)
	{
		performanceState.Tracks.Add(new TrackModel
		{
			Id = $"large-{index}",
			FilePath = $@"C:\Music\{pathPadding}\track-{index}.flac",
			Title = $"Large state track {index}",
			Artist = "State performance regression",
			Album = "Large library",
			AlbumArtist = "Regression suite",
			Genre = "Test",
			Format = "FLAC",
			DurationMs = 240000,
			Categories = ["Large", "Regression"]
		});
	}
	await performanceStore.SaveAsync(performanceState);
	await performanceStore.SaveAsync(performanceState);
	long largeStateBytes = new FileInfo(performanceStore.StatePath).Length;
	Require(largeStateBytes >= 4 * 1024 * 1024,
		"大状态性能回归样本必须至少为 4 MiB，避免测试失去代表性。");
	GC.Collect();
	GC.WaitForPendingFinalizers();
	GC.Collect();
	long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
	Stopwatch saveStopwatch = Stopwatch.StartNew();
	await performanceStore.SaveAsync(performanceState);
	saveStopwatch.Stop();
	long saveAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
	Require(saveAllocatedBytes < largeStateBytes * 2,
		$"大状态稳态保存分配过高：状态 {largeStateBytes:N0} 字节，分配 {saveAllocatedBytes:N0} 字节。");
	Require(saveStopwatch.Elapsed < TimeSpan.FromSeconds(5),
		$"大状态稳态保存耗时异常：{saveStopwatch.Elapsed.TotalMilliseconds:N0} ms。");

	string staleTemporary = performanceStore.StatePath + ".123.stale.tmp";
	string freshTemporary = performanceStore.StatePath + ".456.fresh.tmp";
	await File.WriteAllTextAsync(staleTemporary, "stale");
	await File.WriteAllTextAsync(freshTemporary, "fresh");
	File.SetLastWriteTimeUtc(staleTemporary, DateTime.UtcNow.AddHours(-2));
	_ = await new AppStore(performanceDirectory).LoadAsync();
	Require(!File.Exists(staleTemporary) && File.Exists(freshTemporary),
		"启动时只应清理超过一小时的遗留状态临时文件。");

	Console.WriteLine($"Single-instance, stability, rotating backup, recovery, and large-state checks passed ({largeStateBytes:N0} bytes, {saveStopwatch.Elapsed.TotalMilliseconds:N1} ms, {saveAllocatedBytes:N0} allocated bytes).");
}
finally
{
	Directory.Delete(temporaryDirectory, recursive: true);
}
