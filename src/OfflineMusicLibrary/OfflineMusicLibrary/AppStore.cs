using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineMusicLibrary;

public sealed class AppStore
{
	private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

	private readonly JsonSerializerOptions _options = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public string DataDirectory { get; }

	public string StatePath => Path.Combine(DataDirectory, "library-v2.json");

	public string StateBackupPath => Path.Combine(DataDirectory, "library-v2.backup.json");

	public string StateWriteLockPath => Path.Combine(DataDirectory, "library-v2.write.lock");

	public string LegacyStatePath => Path.Combine(DataDirectory, "library.json");

	public string LegacyStateBackupPath => Path.Combine(DataDirectory, "library.backup.json");

	public string PlaylistArtworkDirectory => Path.Combine(DataDirectory, "playlist-artwork");

	public AppStore(string? dataDirectory = null)
	{
		DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfflineMusicLibrary");
	}

	public async Task<AppState> LoadAsync()
	{
		Directory.CreateDirectory(DataDirectory);
		AppState? current = await TryLoadStatePairAsync(StatePath, StateBackupPath, "新版");
		if (current != null)
		{
			return current;
		}

		AppState? legacy = await TryLoadStatePairAsync(LegacyStatePath, LegacyStateBackupPath, "旧版");
		if (legacy != null)
		{
			legacy.StateFormatVersion = 2;
			await SaveAsync(legacy);
			DiagnosticLog.Write("STATE", "Migrated legacy library.json into isolated v2 state files");
			return legacy;
		}
		return CreateDefaultState();
	}

	private async Task<AppState?> TryLoadStatePairAsync(string primaryPath, string backupPath, string label)
	{
		if (File.Exists(primaryPath))
		{
			try
			{
				return await LoadStateFileAsync(primaryPath);
			}
			catch (Exception ex) when (IsRecoverableStateException(ex))
			{
				DiagnosticLog.Write("STATE", $"{label}主状态文件无效，正在尝试备份", ex);
				PreserveInvalidStateFile(primaryPath, label);
			}
		}
		if (File.Exists(backupPath))
		{
			try
			{
				AppState recovered = await LoadStateFileAsync(backupPath);
				DiagnosticLog.Write("STATE", $"已从{label}状态备份恢复曲库与设置");
				return recovered;
			}
			catch (Exception ex) when (IsRecoverableStateException(ex))
			{
				DiagnosticLog.Write("STATE", $"{label}状态备份也无效", ex);
			}
		}
		return null;
	}

	public async Task SaveAsync(AppState state)
	{
		await _writeLock.WaitAsync();
		string temporary = StatePath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
		try
		{
			Directory.CreateDirectory(DataDirectory);
			await using FileStream stateWriteLock = await AcquireStateWriteLockAsync();
			await using (FileStream stream = File.Create(temporary))
			{
				await JsonSerializer.SerializeAsync((Stream)stream, state, _options, default(CancellationToken));
			}
			await MoveWithRetryAsync(temporary, StatePath);
			if (state.StateBackupEnabled)
			{
				await TryRefreshBackupAsync();
			}
		}
		catch (Exception ex) when (IsRecoverableStateException(ex))
		{
			DiagnosticLog.Write("STATE", "状态保存失败；已保留上一次完整文件", ex);
		}
		finally
		{
			try
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			catch
			{
			}
			_writeLock.Release();
		}
	}

	private async Task<AppState> LoadStateFileAsync(string path)
	{
		await using FileStream stream = File.OpenRead(path);
		AppState state = (await JsonSerializer.DeserializeAsync<AppState>((Stream)stream, _options, default(CancellationToken))) ?? throw new JsonException("状态文件内容为空。");
		Normalize(state);
		return state;
	}

	private async Task TryRefreshBackupAsync()
	{
		string temporaryBackup = StateBackupPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.Copy(StatePath, temporaryBackup, overwrite: true);
			await MoveWithRetryAsync(temporaryBackup, StateBackupPath);
		}
		catch (Exception ex) when (IsRecoverableStateException(ex))
		{
			DiagnosticLog.Write("STATE", "Could not refresh state backup", ex);
			try
			{
				if (File.Exists(temporaryBackup))
				{
					File.Delete(temporaryBackup);
				}
			}
			catch
			{
			}
		}
	}

	private static async Task MoveWithRetryAsync(string source, string destination)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				File.Move(source, destination, overwrite: true);
				return;
			}
			catch (Exception ex) when (attempt < 2 && (ex is IOException || ex is UnauthorizedAccessException))
			{
				await Task.Delay(120);
			}
		}
	}

	private async Task<FileStream> AcquireStateWriteLockAsync()
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				return new FileStream(StateWriteLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
			}
			catch (IOException) when (attempt < 200)
			{
				await Task.Delay(50);
			}
		}
	}

	private void PreserveInvalidStateFile(string sourcePath, string label)
	{
		try
		{
			string safeLabel = label == "新版" ? "v2" : "legacy";
			string invalidCopy = Path.Combine(DataDirectory, $"library-{safeLabel}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
			File.Copy(sourcePath, invalidCopy, overwrite: true);
		}
		catch (Exception ex) when (IsRecoverableStateException(ex))
		{
			DiagnosticLog.Write("STATE", "Could not preserve invalid state file", ex);
		}
	}

	private static bool IsRecoverableStateException(Exception exception)
	{
		return exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException;
	}

	private static AppState CreateDefaultState()
	{
		string preferred = "G:\\音乐\\CloudMusic\\_按专辑分类";
		string fallback = "E:\\CloudMusic";
		AppState state = new AppState();
		if (Directory.Exists(preferred))
		{
			state.LibraryFolders.Add(preferred);
		}
		else if (Directory.Exists(fallback))
		{
			state.LibraryFolders.Add(fallback);
		}
		return state;
	}

	private static void Normalize(AppState state)
	{
		state.StateFormatVersion = 2;
		AppState appState = state;
		if (appState.LibraryFolders == null)
		{
			List<string> list = (appState.LibraryFolders = new List<string>());
		}
		appState = state;
		if (appState.Tracks == null)
		{
			List<TrackModel> list3 = (appState.Tracks = new List<TrackModel>());
		}
		appState = state;
		if (appState.Playlists == null)
		{
			List<PlaylistModel> list5 = (appState.Playlists = new List<PlaylistModel>());
		}
		appState = state;
		if (appState.FavoriteAlbumKeys == null)
		{
			List<string> list = (appState.FavoriteAlbumKeys = new List<string>());
		}
		state.EqualizerPreset = AudioEffectPresets.NormalizeEqualizer(state.EqualizerPreset);
		state.SpatialAudioMode = AudioEffectPresets.NormalizeSpatialAudio(state.SpatialAudioMode);
		state.LyricsDisplayMode = LyricsDisplayModes.Normalize(state.LyricsDisplayMode);
		state.PlayerPageMode = PlayerPageModes.Normalize(state.PlayerPageMode);
		state.DesktopLyricsPrimaryColor = LyricsStyleService.NormalizeColor(state.DesktopLyricsPrimaryColor, "#C9B7FF");
		state.DesktopLyricsSecondaryColor = LyricsStyleService.NormalizeColor(state.DesktopLyricsSecondaryColor, "#79D9A9");
		state.DesktopLyricsRomanizationColor = LyricsStyleService.NormalizeColor(state.DesktopLyricsRomanizationColor, state.DesktopLyricsSecondaryColor);
		state.DesktopLyricsTranslationColor = LyricsStyleService.NormalizeColor(state.DesktopLyricsTranslationColor, state.DesktopLyricsPrimaryColor);
		state.DesktopLyricsStrokeColor = LyricsStyleService.NormalizeColor(state.DesktopLyricsStrokeColor, "#000000");
		state.DesktopLyricsOriginalOpacity = ClampFinite(state.DesktopLyricsOriginalOpacity, 0.35, 1.0, 1.0);
		state.DesktopLyricsRomanizationOpacity = ClampFinite(state.DesktopLyricsRomanizationOpacity, 0.35, 1.0, 0.92);
		state.DesktopLyricsTranslationOpacity = ClampFinite(state.DesktopLyricsTranslationOpacity, 0.35, 1.0, 0.9);
		state.DesktopLyricsStrokeScale = ClampFinite(state.DesktopLyricsStrokeScale, 0.5, 2.0, 1.0);
		state.PlaybackWatchdogTimeoutSeconds = Math.Clamp(state.PlaybackWatchdogTimeoutSeconds, 8, 30);
		state.PlaybackRecoveryAttempts = Math.Clamp(state.PlaybackRecoveryAttempts, 1, 5);
		foreach (TrackModel track in state.Tracks)
		{
			TrackModel current;
			TrackModel trackModel = (current = track);
			if (current.Categories == null)
			{
				List<string> list = (current.Categories = new List<string>());
			}
			current = trackModel;
			if (current.CloudIds == null)
			{
				List<string> list = (current.CloudIds = new List<string>());
			}
		}
		foreach (PlaylistModel playlist in state.Playlists)
		{
			PlaylistModel playlistModel = playlist;
			if (playlistModel.Name == null)
			{
				string text = (playlistModel.Name = "新歌单");
			}
			playlistModel = playlist;
			if (playlistModel.Description == null)
			{
				string text = (playlistModel.Description = "");
			}
			playlistModel = playlist;
			if (playlistModel.CoverPath == null)
			{
				string text = (playlistModel.CoverPath = "");
			}
			playlistModel = playlist;
			if (playlistModel.Source == null)
			{
				string text = (playlistModel.Source = "local");
			}
			playlistModel = playlist;
			if (playlistModel.TrackIds == null)
			{
				List<string> list = (playlistModel.TrackIds = new List<string>());
			}
			playlistModel = playlist;
			if (playlistModel.Tags == null)
			{
				List<string> list = (playlistModel.Tags = new List<string>());
			}
			if (playlist.CreatedAt == default(DateTime))
			{
				playlist.CreatedAt = ((playlist.UpdatedAt == default(DateTime)) ? DateTime.Now : playlist.UpdatedAt);
			}
			if (playlist.UpdatedAt == default(DateTime))
			{
				playlist.UpdatedAt = playlist.CreatedAt;
			}
		}
	}

	private static double ClampFinite(double value, double minimum, double maximum, double fallback)
	{
		return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
	}
}
