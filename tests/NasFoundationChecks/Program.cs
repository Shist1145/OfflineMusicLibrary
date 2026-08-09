using System.Diagnostics;
using System.Text.Json;
using OfflineMusicLibrary;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
{
	DateTime deadline = DateTime.UtcNow + timeout;
	while (!condition())
	{
		if (DateTime.UtcNow >= deadline)
		{
			throw new TimeoutException(message);
		}
		await Task.Delay(10);
	}
}

string temporaryDirectory = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary-NasFoundationChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
	string localRoot = Path.Combine(temporaryDirectory, "library");
	Directory.CreateDirectory(localRoot);
	string normalizedLocalRoot = LibraryRootCatalog.NormalizePath(localRoot);
	LibraryRootState localState = LibraryRootCatalog.Create(localRoot);
	LibraryRootState sameLocalState = LibraryRootCatalog.Create(localRoot + Path.DirectorySeparatorChar);
	Require(localState.RootId == sameLocalState.RootId,
		"同一根目录带或不带尾部分隔符时必须得到稳定且相同的 RootId。");
	LibraryRootState uncState = LibraryRootCatalog.Create(@"\\nas-test\music");
	Require(uncState.RootKind == LibraryRootKinds.Unc && LibraryRootKinds.IsReconnectable(uncState.RootKind),
		"UNC 路径必须在不访问网络的前提下识别为可重连 NAS 根目录。");

	LibraryRootHealthService healthService = new();
	LibraryRootProbeResult localProbe = await healthService.ProbeAsync(localState, TimeSpan.FromSeconds(2));
	Require(localProbe.Reachable && localProbe.Health is LibraryRootHealthStates.Online or LibraryRootHealthStates.Slow,
		"临时本机根目录应能通过异步健康探测。");
	LibraryRootState missingState = LibraryRootCatalog.Create(Path.Combine(temporaryDirectory, "missing"));
	LibraryRootProbeResult missingProbe = await healthService.ProbeAsync(missingState, TimeSpan.FromSeconds(2));
	Require(!missingProbe.Reachable && missingProbe.Health == LibraryRootHealthStates.Offline,
		"不存在的根目录应标记为离线，而不是抛出未处理异常。");
	Directory.CreateDirectory(missingState.Path);
	LibraryRootProbeResult recoveredProbe = await healthService.ProbeAsync(missingState, TimeSpan.FromSeconds(2));
	Require(recoveredProbe.Reachable,
		"离线探测完成后不得永久缓存旧结果；根目录恢复时下一次探测必须看到在线状态。");

	TaskCompletionSource<bool> releaseProbes = new(TaskCreationOptions.RunContinuationsAsynchronously);
	int activeProbes = 0;
	int maximumActiveProbes = 0;
	int startedProbes = 0;
	LibraryRootHealthService boundedHealthService = new(
		path => Task.FromResult(new LibraryRootHealthService.ProbeOutcome(
			true,
			false,
			LibraryRootKinds.Local,
			TimeSpan.FromMilliseconds(1),
			null)),
		async path =>
		{
			Interlocked.Increment(ref startedProbes);
			int active = Interlocked.Increment(ref activeProbes);
			int observed;
			do
			{
				observed = Volatile.Read(ref maximumActiveProbes);
			}
			while (active > observed && Interlocked.CompareExchange(ref maximumActiveProbes, active, observed) != observed);
			try
			{
				await releaseProbes.Task;
				return new PathAvailabilityResult(true, false, 1, null);
			}
			finally
			{
				Interlocked.Decrement(ref activeProbes);
			}
		},
		maxConcurrentProbes: 2,
		maxOutstandingProbes: 4);
	string sharedProbePath = Path.Combine(temporaryDirectory, "probe-shared");
	Task<PathAvailabilityResult>[] boundedProbeTasks =
	[
		boundedHealthService.ProbePathAsync(sharedProbePath, TimeSpan.FromSeconds(5)),
		boundedHealthService.ProbePathAsync(sharedProbePath, TimeSpan.FromSeconds(5)),
		boundedHealthService.ProbePathAsync(Path.Combine(temporaryDirectory, "probe-1"), TimeSpan.FromSeconds(5)),
		boundedHealthService.ProbePathAsync(Path.Combine(temporaryDirectory, "probe-2"), TimeSpan.FromSeconds(5)),
		boundedHealthService.ProbePathAsync(Path.Combine(temporaryDirectory, "probe-3"), TimeSpan.FromSeconds(5))
	];
	await WaitUntilAsync(() => Volatile.Read(ref activeProbes) == 2, TimeSpan.FromSeconds(2),
		"有界探测器未按预期启动两个并发探测。");
	PathAvailabilityResult saturatedProbe = await boundedHealthService.ProbePathAsync(
		Path.Combine(temporaryDirectory, "probe-overflow"),
		TimeSpan.FromSeconds(2));
	Require(!saturatedProbe.Reachable && saturatedProbe.Error?.Contains("队列繁忙", StringComparison.Ordinal) == true,
		"超过有界探测队列时必须快速失败，不能继续堆积后台线程。");
	releaseProbes.SetResult(true);
	PathAvailabilityResult[] boundedResults = await Task.WhenAll(boundedProbeTasks);
	Require(boundedResults.All(result => result.Reachable) && maximumActiveProbes <= 2 && startedProbes == 4,
		"不同路径探测必须受并发上限约束，同一路径的在途请求必须复用同一任务。");
	Require(
		LibraryRootRetrySchedule.GetDelay(0) == TimeSpan.FromSeconds(1) &&
		LibraryRootRetrySchedule.GetDelay(1) == TimeSpan.FromSeconds(2) &&
		LibraryRootRetrySchedule.GetDelay(2) == TimeSpan.FromSeconds(5) &&
		LibraryRootRetrySchedule.GetDelay(3) == TimeSpan.FromSeconds(10) &&
		LibraryRootRetrySchedule.GetDelay(4) == TimeSpan.FromSeconds(30) &&
		LibraryRootRetrySchedule.GetDelay(99) == TimeSpan.FromSeconds(30),
		"根目录重试必须按 1、2、5、10 秒退避，之后保持 30 秒低频等待。");

	string stateDirectory = Path.Combine(temporaryDirectory, "state");
	Directory.CreateDirectory(stateDirectory);
	AppStore store = new(stateDirectory);
	AppState versionTwoState = new()
	{
		StateFormatVersion = 2,
		LibraryFolders = [localRoot, @"\\nas-test\music"],
		Tracks =
		[
			new TrackModel { Id = "track-a", FilePath = Path.Combine(localRoot, "a.flac"), Title = "A" },
			new TrackModel { Id = "track-b", FilePath = Path.Combine(localRoot, "b.flac"), Title = "B" }
		],
		LastTrackId = "track-b",
		LastPlaybackPositionMs = 45678,
		RepeatMode = "One",
		ShuffleMode = "Smart"
	};
	await File.WriteAllTextAsync(store.StatePath, JsonSerializer.Serialize(versionTwoState));
	AppState migrated = await store.LoadAsync();
	Require(migrated.StateFormatVersion == 3 && migrated.LibraryRoots.Count == 2,
		"v2 LibraryFolders 必须无损迁移为 v3 根目录状态模型。");
	Require(migrated.LibraryRoots.Select(root => root.Path).SequenceEqual(migrated.LibraryFolders, StringComparer.OrdinalIgnoreCase),
		"兼容路径列表和根目录状态列表必须保持同步。");
	Require(migrated.PlaybackSession.QueueTrackIds.SequenceEqual(new[] { "track-b" }) &&
		migrated.PlaybackSession.CurrentIndex == 0 && migrated.PlaybackSession.PositionMs == 45678,
		"旧版最后曲目和位置必须迁移为可恢复播放会话。");
	Require(migrated.RepeatMode == "One" && migrated.ShuffleMode == "Smart" && migrated.ShuffleEnabled,
		"v2 的循环和随机模式必须迁移到完整播放会话，不能被新模型默认值覆盖。");

	TrackModel trackA = migrated.Tracks.Single(track => track.Id == "track-a");
	TrackModel trackB = migrated.Tracks.Single(track => track.Id == "track-b");
	PlaybackSessionService.Capture(
		migrated,
		[trackA, trackB],
		1,
		trackB,
		98765,
		new[] { "track-a", "track-b", "track-a" },
		migrated.LibraryRoots[1].RootId);
	RestoredPlaybackSession restored = PlaybackSessionService.Restore(migrated);
	Require(restored.Queue.Select(track => track.Id).SequenceEqual(new[] { "track-a", "track-b" }) &&
		restored.CurrentIndex == 1 && restored.PositionMs == 98765,
		"完整队列、当前索引和播放位置必须按原顺序恢复。");
	Require(restored.RecentShuffleIds.SequenceEqual(new[] { "track-a", "track-b" }) &&
		restored.WaitingRootId == migrated.LibraryRoots[1].RootId,
		"随机历史应去重保序，WaitingRootId 必须随会话保留。");
	await store.SaveAsync(migrated);
	AppState reloaded = await store.LoadAsync();
	Require(reloaded.PlaybackSession.QueueTrackIds.SequenceEqual(new[] { "track-a", "track-b" }) &&
		reloaded.LibraryRoots[1].RootId == migrated.LibraryRoots[1].RootId,
		"保存和重新载入不得改变队列顺序或根目录稳定 ID。");

	string oversizedStateDirectory = Path.Combine(temporaryDirectory, "oversized-state");
	Directory.CreateDirectory(oversizedStateDirectory);
	AppStore oversizedStateStore = new(oversizedStateDirectory);
	AppState backupState = new()
	{
		Tracks = [new TrackModel { Id = "backup-track", FilePath = Path.Combine(localRoot, "backup.flac"), Title = "Backup" }]
	};
	await File.WriteAllTextAsync(oversizedStateStore.StateBackupPath, JsonSerializer.Serialize(backupState));
	await using (FileStream oversizedState = new(oversizedStateStore.StatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
	{
		oversizedState.SetLength(AppStore.MaximumStateFileBytes + 1);
	}
	AppState recoveredFromBackup = await oversizedStateStore.LoadAsync();
	Require(recoveredFromBackup.Tracks.Single().Id == "backup-track",
		"主状态文件超过安全上限时必须跳过它并从有效备份恢复。");
	string oversizedStringStateDirectory = Path.Combine(temporaryDirectory, "oversized-string-state");
	Directory.CreateDirectory(oversizedStringStateDirectory);
	AppStore oversizedStringStore = new(oversizedStringStateDirectory);
	await File.WriteAllTextAsync(oversizedStringStore.StateBackupPath, JsonSerializer.Serialize(backupState));
	string oversizedStateString = new('x', ContentReadLimits.StateStringUtf8Bytes + 1);
	await File.WriteAllTextAsync(
		oversizedStringStore.StatePath,
		$"{{\"Tracks\":[{{\"Id\":\"malformed\",\"FilePath\":\"x.flac\",\"Title\":\"{oversizedStateString}\"}}]}}");
	AppState recoveredFromOversizedString = await oversizedStringStore.LoadAsync();
	Require(recoveredFromOversizedString.Tracks.Single().Id == "backup-track",
		"状态中的单个超大字符串必须在分配到模型前被拒绝并继续从备份恢复。");
	string boundedSaveDirectory = Path.Combine(temporaryDirectory, "bounded-string-save");
	AppStore boundedSaveStore = new(boundedSaveDirectory);
	await boundedSaveStore.SaveAsync(new AppState { AppTitleText = "valid-title" });
	bool oversizedStringSaveRejected = false;
	try
	{
		await boundedSaveStore.SaveAsync(new AppState
		{
			AppTitleText = new string('y', ContentReadLimits.StateStringUtf8Bytes + 1)
		});
	}
	catch (JsonException)
	{
		oversizedStringSaveRejected = true;
	}
	Require(oversizedStringSaveRejected && (await boundedSaveStore.LoadAsync()).AppTitleText == "valid-title",
		"超大状态字符串写入必须失败，并保留上一份完整主状态。");

	string cacheDirectory = Path.Combine(temporaryDirectory, "asset-cache");
	PersistentAssetCache.Configure(cacheDirectory, enabled: true, maximumMegabytes: 128);
	AssetSourceStamp firstStamp = new(1234, 5678, "v1");
	byte[] payload = [1, 3, 3, 7];
	Require(PersistentAssetCache.Write("artwork", "track-a", firstStamp, payload),
		"资源缓存应能原子写入临时测试目录。");
	Require(PersistentAssetCache.TryRead("artwork", "track-a", firstStamp, allowStale: false, out byte[] exact) && exact.SequenceEqual(payload),
		"来源指纹相同时必须命中缓存。");
	Require(!PersistentAssetCache.TryRead("artwork", "track-a", new AssetSourceStamp(9999, 5678, "v1"), allowStale: false, out _),
		"来源长度变化时不得把旧缓存冒充为最新资源。");
	Require(PersistentAssetCache.TryRead("artwork", "track-a", null, allowStale: true, out byte[] stale) && stale.SequenceEqual(payload),
		"NAS 离线、无法取得来源指纹时必须允许读取最后一份本机缓存。");
	AssetCacheStatistics cacheStatistics = PersistentAssetCache.GetStatistics();
	Require(cacheStatistics.Files == 1 && cacheStatistics.Bytes == payload.Length,
		"缓存统计必须只计算本机 payload 文件。");
	Require(!PersistentAssetCache.IsSafeCachePathForTesting(Path.Combine(cacheDirectory, "..", "outside.payload")),
		"缓存边界检查必须拒绝任何规范化后逃离缓存根目录的路径。");
	string cachedPayloadPath = Directory.GetFiles(cacheDirectory, "*.payload", SearchOption.AllDirectories).Single();
	string cachedMetadataPath = Directory.GetFiles(cacheDirectory, "*.meta.json", SearchOption.AllDirectories).Single();
	await using (FileStream oversizedPayload = new(cachedPayloadPath, FileMode.Open, FileAccess.Write, FileShare.None))
	{
		oversizedPayload.SetLength(ContentReadLimits.ArtworkBytes + 1L);
	}
	Require(!PersistentAssetCache.TryRead("artwork", "track-a", firstStamp, allowStale: false, out _),
		"超大封面缓存必须在分配内存前被拒绝。");
	Require(PersistentAssetCache.Write("artwork", "track-a", firstStamp, payload),
		"拒绝受损缓存后必须仍能用有效数据原子重建该条目。");
	await using (FileStream oversizedMetadata = new(cachedMetadataPath, FileMode.Open, FileAccess.Write, FileShare.None))
	{
		oversizedMetadata.SetLength(ContentReadLimits.CacheMetadataBytes + 1L);
	}
	Require(!PersistentAssetCache.TryRead("artwork", "track-a", firstStamp, allowStale: false, out _),
		"超大缓存元数据必须在 JSON 反序列化前被拒绝。");
	Require(PersistentAssetCache.Write("artwork", "track-a", firstStamp, payload),
		"元数据受损后必须仍能安全重建缓存条目。");

	string outsideCacheDirectory = Path.Combine(temporaryDirectory, "outside-cache");
	Directory.CreateDirectory(outsideCacheDirectory);
	string outsideSentinel = Path.Combine(outsideCacheDirectory, "do-not-delete.payload");
	await File.WriteAllBytesAsync(outsideSentinel, [9, 9, 9]);
	string cacheLink = Path.Combine(cacheDirectory, "linked-outside");
	bool reparsePointCreated = false;
	try
	{
		Directory.CreateSymbolicLink(cacheLink, outsideCacheDirectory);
		reparsePointCreated = (File.GetAttributes(cacheLink) & FileAttributes.ReparsePoint) != 0;
	}
	catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
	{
		if (OperatingSystem.IsWindows())
		{
			ProcessStartInfo junctionInfo = new("cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			junctionInfo.ArgumentList.Add("/d");
			junctionInfo.ArgumentList.Add("/c");
			junctionInfo.ArgumentList.Add("mklink");
			junctionInfo.ArgumentList.Add("/J");
			junctionInfo.ArgumentList.Add(cacheLink);
			junctionInfo.ArgumentList.Add(outsideCacheDirectory);
			using Process? junctionProcess = Process.Start(junctionInfo);
			if (junctionProcess != null)
			{
				await junctionProcess.WaitForExitAsync();
				reparsePointCreated = junctionProcess.ExitCode == 0 &&
					Directory.Exists(cacheLink) &&
					(File.GetAttributes(cacheLink) & FileAttributes.ReparsePoint) != 0;
			}
		}
		if (!reparsePointCreated)
		{
			Console.WriteLine($"Reparse-point cache test skipped on this host: {exception.GetType().Name}");
		}
	}
	if (reparsePointCreated)
	{
		PersistentAssetCache.Clear();
		Require(File.Exists(outsideSentinel),
			"缓存清理不得穿过目录联接或符号链接删除根目录之外的文件。");
		Directory.Delete(cacheLink);
	}

	string lyricAudioPath = Path.Combine(localRoot, "lyrics.flac");
	string lyricPath = Path.Combine(localRoot, "lyrics.lrc");
	await File.WriteAllBytesAsync(lyricAudioPath, [0]);
	await File.WriteAllTextAsync(lyricPath, "[00:01.00]Original\n[00:01.00]翻译");
	FileInfo lyricAudioInfo = new(lyricAudioPath);
	TrackModel lyricTrack = new()
	{
		Id = "lyrics-track",
		FilePath = lyricAudioPath,
		FileSize = lyricAudioInfo.Length,
		LastWriteTimeUtcTicks = lyricAudioInfo.LastWriteTimeUtc.Ticks
	};
	List<LyricLine> onlineLyrics = LyricsService.LoadForTrack(lyricTrack);
	Require(onlineLyrics.Count == 1 && onlineLyrics[0].Translation == "翻译",
		"在线读取后的歌词必须写入持久缓存且保持语义分类。");
	File.Delete(lyricPath);
	File.Delete(lyricAudioPath);
	List<LyricLine> offlineLyrics = LyricsService.LoadForTrack(lyricTrack);
	Require(offlineLyrics.Count == 1 && offlineLyrics[0].Original == "Original",
		"媒体根目录离线后仍应从本机缓存恢复歌词。");

	string oversizedAudioPath = Path.Combine(localRoot, "oversized-lyrics.flac");
	string oversizedLyricPath = Path.Combine(localRoot, "oversized-lyrics.lrc");
	await File.WriteAllBytesAsync(oversizedAudioPath, [0]);
	await using (FileStream oversizedLyrics = new(oversizedLyricPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
	{
		oversizedLyrics.SetLength(ContentReadLimits.LyricsFileBytes + 1L);
	}
	Require(LyricsService.LoadForTrack(oversizedAudioPath).Count == 0,
		"超过安全上限的歌词文件必须被忽略，不能一次性读入内存。");
	string oversizedImagePath = Path.Combine(localRoot, "oversized-cover.jpg");
	await using (FileStream oversizedImage = new(oversizedImagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
	{
		oversizedImage.SetLength(ContentReadLimits.ArtworkBytes + 1L);
	}
	Require(CoverService.LoadImageFile(oversizedImagePath) == null,
		"超过安全上限的图片必须在解码前被拒绝。");

	AppState networkPlaybackState = new()
	{
		NasBufferSeconds = 18,
		LibraryFolders = [@"\\nas-test\music"],
		LibraryRoots = [uncState]
	};
	PlaybackEngineProfile networkProfile = PlaybackStabilityService.Resolve(networkPlaybackState, isVideo: false, @"\\nas-test\music\album\song.flac");
	Require(networkProfile.IsNetworkSource && networkProfile.CacheMilliseconds == 18000,
		"NAS 媒体必须使用设置的 5–30 秒网络缓存，而不是本机默认 1500 ms。");
	networkPlaybackState.SafePlaybackMode = true;
	networkPlaybackState.NasBufferSeconds = 5;
	PlaybackEngineProfile safeNetworkProfile = PlaybackStabilityService.Resolve(networkPlaybackState, isVideo: false, @"\\nas-test\music\song.flac");
	Require(safeNetworkProfile.IsNetworkSource && safeNetworkProfile.CacheMilliseconds == 10000,
		"稳定优先模式下 NAS 缓冲不得低于 10 秒。");

	PersistentAssetCache.Clear();
	Require(PersistentAssetCache.GetStatistics().Files == 0,
		"清理操作只能移除测试目录中的本机缓存并留下可继续使用的缓存根目录。");

	Console.WriteLine("NAS root health, migration, session restore, local asset cache, and network buffering checks passed.");
}
finally
{
	PersistentAssetCache.Configure("", enabled: false, maximumMegabytes: 128);
	Directory.Delete(temporaryDirectory, recursive: true);
}
