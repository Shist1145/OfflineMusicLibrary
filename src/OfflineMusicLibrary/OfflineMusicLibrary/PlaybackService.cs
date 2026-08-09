using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

namespace OfflineMusicLibrary;

public sealed class PlaybackService : IDisposable
{
	private sealed record EngineResources(LibVLC LibVlc, MediaPlayer Player);

	private readonly object _engineSync = new object();

	private readonly ConditionalWeakTable<MediaPlayer, SemaphoreSlim> _commandGates = new ConditionalWeakTable<MediaPlayer, SemaphoreSlim>();

	private LibVLC _libVlc = null!;

	private Media? _currentMedia;

	private CancellationTokenSource? _parseCancellation;

	private double _requestedRate = 1.0;

	private string _preferredAudioDeviceId = "";

	private string _engineVisualizationMode = "Off";

	private string _engineAudioBackend = "DirectSound";

	private string _engineSpatialAudioMode = "Off";

	private string _equalizerPreset = "Off";

	private long _cachedTime;

	private long _cachedLength;

	private long _lastProgressUtcTicks = DateTime.UtcNow.Ticks;

	private int _cachedIsPlaying;

	private int _desiredIsPlaying;

	private int _cachedVolume = 76;

	private int _playRequestVersion;

	private int _pauseIntentVersion;

	private int _equalizerRequestVersion;

	private bool _disposed;

	public MediaPlayer Player { get; private set; } = null!;

	public bool IsPlaying => Volatile.Read(in _cachedIsPlaying) != 0;

	public long Time => Math.Max(0L, Interlocked.Read(in _cachedTime));

	public long Length => Math.Max(0L, Interlocked.Read(in _cachedLength));

	public float Fps => 0f;

	public DateTime LastProgressUtc => new DateTime(Math.Clamp(Interlocked.Read(in _lastProgressUtcTicks), DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);

	public int Volume
	{
		get
		{
			return Volatile.Read(in _cachedVolume);
		}
		set
		{
			int volume = Math.Clamp(value, 0, 100);
			Volatile.Write(ref _cachedVolume, volume);
			RunNativeCommand(Player, delegate(MediaPlayer player)
			{
				player.Volume = volume;
			}, "set volume", trackBound: false);
		}
	}

	public event EventHandler? Ended;

	public event EventHandler? PlaybackError;

	public event EventHandler? PlaybackReady;

	public event EventHandler? PlayerChanged;

	public event Action<MediaDetails>? MediaDetailsChanged;

	public PlaybackService()
	{
		Core.Initialize();
		CreateInitialEngine("Off", "DirectSound", "Off", "Off", 76);
	}

	public void Play(string path, AppState state, bool isVideo, string? sidecarSubtitlePath = null)
	{
		_requestedRate = Math.Clamp(state.PlaybackRate, 0.25, 4.0);
		_preferredAudioDeviceId = state.PreferredAudioDeviceId;
		PlaybackEngineProfile profile = PlaybackStabilityService.Resolve(state, isVideo, path);
		EnsureEngine(profile.VisualizationMode, profile.AudioBackend, profile.SpatialAudioMode, profile.EqualizerPreset);
		MediaPlayer player = Player;
		Media next = CreateMedia(_libVlc, path, profile, sidecarSubtitlePath);
		Media old;
		CancellationTokenSource parseCancellation;
		lock (_engineSync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			old = _currentMedia;
			_currentMedia = next;
			_parseCancellation?.Cancel();
			_parseCancellation?.Dispose();
			parseCancellation = (_parseCancellation = new CancellationTokenSource());
			Interlocked.Increment(ref _playRequestVersion);
			ResetCachedPlaybackState();
		}
		DiagnosticLog.Write("PLAY", $"Opening '{path}' via {_engineAudioBackend}, cache {profile.CacheMilliseconds} ms, network={profile.IsNetworkSource}, safe={state.SafePlaybackMode}");
		Volatile.Write(ref _desiredIsPlaying, 1);
		bool started;
		try
		{
			started = player.Play(next);
		}
		catch
		{
			RollbackFailedPlay(next, old, parseCancellation);
			throw;
		}
		if (!started)
		{
			RollbackFailedPlay(next, old, parseCancellation);
			throw new InvalidOperationException("底层播放引擎拒绝了该媒体文件。请检查文件是否损坏或仍可访问。");
		}
		RunNativeCommand(player, delegate(MediaPlayer activePlayer)
		{
			activePlayer.SetRate((float)_requestedRate);
			if (!string.IsNullOrWhiteSpace(_preferredAudioDeviceId))
			{
				activePlayer.SetOutputDevice(_preferredAudioDeviceId);
			}
		}, "apply playback options", trackBound: false);
		old?.Dispose();
		DiagnosticLog.Observe(ParseMediaAsync(next, parseCancellation.Token), "MEDIA", "Could not parse newly opened media");
	}

	private void RollbackFailedPlay(Media attemptedMedia, Media? previousMedia, CancellationTokenSource parseCancellation)
	{
		lock (_engineSync)
		{
			if (ReferenceEquals(_currentMedia, attemptedMedia))
			{
				_currentMedia = previousMedia;
				if (ReferenceEquals(_parseCancellation, parseCancellation))
				{
					_parseCancellation.Cancel();
					_parseCancellation.Dispose();
					_parseCancellation = null;
				}
				Interlocked.Increment(ref _playRequestVersion);
				ResetCachedPlaybackState();
			}
		}
		attemptedMedia.Dispose();
	}

	public void TogglePause()
	{
		int desired;
		int next;
		do
		{
			desired = Volatile.Read(in _desiredIsPlaying);
			next = ((desired == 0) ? 1 : 0);
		}
		while (Interlocked.CompareExchange(ref _desiredIsPlaying, next, desired) != desired);
		bool shouldPause = next == 0;
		Volatile.Write(ref _cachedIsPlaying, next);
		TouchProgressClock();
		MediaPlayer player = Player;
		int intentVersion = Interlocked.Increment(ref _pauseIntentVersion);
		Task.Run(async delegate
		{
			await Task.Delay(70);
			if (intentVersion == Volatile.Read(in _pauseIntentVersion))
			{
				RunNativeCommand(player, delegate(MediaPlayer activePlayer)
				{
					activePlayer.SetPause(shouldPause);
				}, shouldPause ? "pause" : "resume");
			}
		});
	}

	public void Seek(long milliseconds)
	{
		long length = Length;
		if (length > 0)
		{
			long target = Math.Clamp(milliseconds, 0L, length);
			Interlocked.Exchange(ref _cachedTime, target);
			TouchProgressClock();
			RunNativeCommand(Player, delegate(MediaPlayer player)
			{
				player.Time = target;
			}, "seek");
		}
	}

	public bool SetRate(double rate)
	{
		_requestedRate = Math.Clamp(rate, 0.25, 4.0);
		RunNativeCommand(Player, delegate(MediaPlayer player)
		{
			player.SetRate((float)_requestedRate);
		}, "set rate", trackBound: false);
		return true;
	}

	public void SetEqualizerPreset(string? preset)
	{
		string normalized = AudioEffectPresets.NormalizeEqualizer(preset);
		_equalizerPreset = normalized;
		MediaPlayer player = Player;
		int requestVersion = Interlocked.Increment(ref _equalizerRequestVersion);
		RunNativeCommand(player, delegate(MediaPlayer activePlayer)
		{
			if (requestVersion == Volatile.Read(in _equalizerRequestVersion))
			{
				ApplyEqualizer(activePlayer, normalized);
			}
		}, "apply equalizer", trackBound: false);
	}

	public bool TakeSnapshot(string path)
	{
		return Player.TakeSnapshot(0u, path, 0u, 0u);
	}

	public IReadOnlyList<MediaTrackOption> GetAudioTracks()
	{
		return MapTracks(Player.AudioTrackDescription, "音轨");
	}

	public IReadOnlyList<MediaTrackOption> GetVideoTracks()
	{
		return MapTracks(Player.VideoTrackDescription, "视频轨");
	}

	public IReadOnlyList<MediaTrackOption> GetSubtitleTracks()
	{
		return MapTracks(Player.SpuDescription, "字幕");
	}

	public IReadOnlyList<AudioDeviceOption> GetAudioDevices()
	{
		List<AudioDeviceOption> devices = new List<AudioDeviceOption>
		{
			new AudioDeviceOption("", "系统默认设备")
		};
		AudioOutputDevice[] available = Player.AudioOutputDeviceEnum;
		if (available == null)
		{
			return devices;
		}
		devices.AddRange(from device in available
			where !string.IsNullOrWhiteSpace(device.DeviceIdentifier)
			select new AudioDeviceOption(device.DeviceIdentifier, device.Description));
		return devices;
	}

	public MediaControlSnapshot CaptureMediaControls()
	{
		MediaPlayer player = Player;
		return new MediaControlSnapshot(MapTracks(player.AudioTrackDescription, "音轨"), player.AudioTrack, MapTracks(player.VideoTrackDescription, "视频轨"), player.VideoTrack, MapTracks(player.SpuDescription, "字幕"), player.Spu, GetAudioDevices());
	}

	public bool SetAudioTrack(int id)
	{
		RunNativeCommand(Player, delegate(MediaPlayer player)
		{
			player.SetAudioTrack(id);
		}, "set audio track");
		return true;
	}

	public bool SetVideoTrack(int id)
	{
		RunNativeCommand(Player, delegate(MediaPlayer player)
		{
			player.SetVideoTrack(id);
		}, "set video track");
		return true;
	}

	public bool SetSubtitleTrack(int id)
	{
		RunNativeCommand(Player, delegate(MediaPlayer player)
		{
			player.SetSpu(id);
		}, "set subtitle track");
		return true;
	}

	public void SetSubtitleDelay(long milliseconds)
	{
		RunNativeCommand(Player, delegate(MediaPlayer player)
		{
			player.SetSpuDelay(milliseconds * 1000);
		}, "set subtitle delay");
	}

	public void SetAudioDevice(string deviceId)
	{
		_preferredAudioDeviceId = deviceId;
		if (!string.IsNullOrWhiteSpace(deviceId))
		{
			RunNativeCommand(Player, delegate(MediaPlayer player)
			{
				player.SetOutputDevice(deviceId);
			}, "set audio device", trackBound: false);
		}
	}

	public void Stop()
	{
		Volatile.Write(ref _desiredIsPlaying, 0);
		Volatile.Write(ref _cachedIsPlaying, 0);
		Interlocked.Increment(ref _pauseIntentVersion);
		RunNativeCommand(Player, delegate(MediaPlayer player)
		{
			player.Stop();
		}, "stop");
	}

	public async Task<bool> RecoverAsync(string path, AppState state, bool isVideo, long resumeAtMilliseconds, string? sidecarSubtitlePath = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		int expectedRequest = Volatile.Read(in _playRequestVersion);
		PlaybackEngineProfile profile = PlaybackStabilityService.Resolve(state, isVideo, path);
		string visualization = profile.VisualizationMode;
		string backend = NormalizeAudioBackend(profile.AudioBackend);
		string spatialAudio = AudioEffectPresets.NormalizeSpatialAudio(profile.SpatialAudioMode);
		string equalizer = AudioEffectPresets.NormalizeEqualizer(profile.EqualizerPreset);
		int volume = Volume;
		DiagnosticLog.Write("WATCHDOG", $"Rebuilding engine for '{path}' at {resumeAtMilliseconds} ms");
		EngineResources fresh = await Task.Run(() => CreateEngineResources(visualization, backend, spatialAudio, equalizer, volume), cancellationToken);
		Media media;
		try
		{
			media = CreateMedia(fresh.LibVlc, path, profile, sidecarSubtitlePath);
		}
		catch
		{
			QueueEngineCleanup(fresh.Player, fresh.LibVlc, null);
			throw;
		}
		Task<(bool Started, long Length)> startTask = Task.Run(delegate
		{
			if (!string.IsNullOrWhiteSpace(_preferredAudioDeviceId))
			{
				fresh.Player.SetOutputDevice(_preferredAudioDeviceId);
			}
			bool started = fresh.Player.Play(media);
			fresh.Player.SetRate((float)_requestedRate);
			return (Started: started, Length: started ? Math.Max(0L, fresh.Player.Length) : 0L);
		}, cancellationToken);
		Task completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(8L), cancellationToken));
		if (completed != startTask)
		{
			QueueReplacementCleanupAfterStart(startTask, fresh.Player, fresh.LibVlc, media);
			if (cancellationToken.IsCancellationRequested)
			{
				throw new OperationCanceledException(cancellationToken);
			}
			DiagnosticLog.Write("WATCHDOG", "Replacement engine did not start within eight seconds");
			return false;
		}
		(bool Started, long Length) startResult;
		try
		{
			startResult = await startTask;
		}
		catch
		{
			QueueEngineCleanup(fresh.Player, fresh.LibVlc, media);
			throw;
		}
		if (!startResult.Started)
		{
			QueueEngineCleanup(fresh.Player, fresh.LibVlc, media);
			return false;
		}
		if (cancellationToken.IsCancellationRequested)
		{
			QueueEngineCleanup(fresh.Player, fresh.LibVlc, media);
			throw new OperationCanceledException(cancellationToken);
		}
		MediaPlayer oldPlayer;
		LibVLC oldLibVlc;
		Media oldMedia;
		CancellationTokenSource parseCancellation;
		lock (_engineSync)
		{
			if (_disposed || expectedRequest != Volatile.Read(in _playRequestVersion))
			{
				QueueEngineCleanup(fresh.Player, fresh.LibVlc, media);
				return false;
			}
			oldPlayer = Player;
			oldLibVlc = _libVlc;
			oldMedia = _currentMedia;
			Player = fresh.Player;
			_libVlc = fresh.LibVlc;
			_currentMedia = media;
			_engineVisualizationMode = visualization;
			_engineAudioBackend = backend;
			_engineSpatialAudioMode = spatialAudio;
			_equalizerPreset = equalizer;
			_parseCancellation?.Cancel();
			_parseCancellation?.Dispose();
			parseCancellation = (_parseCancellation = new CancellationTokenSource());
			Interlocked.Increment(ref _playRequestVersion);
			ResetCachedPlaybackState();
			Interlocked.Exchange(ref _cachedLength, startResult.Length);
			Volatile.Write(ref _desiredIsPlaying, 1);
			Volatile.Write(ref _cachedIsPlaying, 1);
			TouchProgressClock();
		}
		this.PlayerChanged?.Invoke(this, EventArgs.Empty);
		this.PlaybackReady?.Invoke(this, EventArgs.Empty);
		QueueEngineCleanup(oldPlayer, oldLibVlc, oldMedia);
		if (resumeAtMilliseconds > 0)
		{
			await Task.Delay(600, cancellationToken);
			if (Player == fresh.Player)
			{
				long target = Math.Max(0L, resumeAtMilliseconds);
				Interlocked.Exchange(ref _cachedTime, target);
				await Task.Run(() => fresh.Player.Time = target, cancellationToken);
			}
		}
		DiagnosticLog.Observe(ParseMediaAsync(media, parseCancellation.Token), "MEDIA", "Could not parse media after playback recovery");
		DiagnosticLog.Write("WATCHDOG", "Replacement engine started successfully");
		return true;
	}

	private void QueueReplacementCleanupAfterStart(Task startTask, MediaPlayer player, LibVLC libVlc, Media media)
	{
		_ = startTask.ContinueWith(delegate(Task completed)
		{
			if (completed.IsFaulted)
			{
				_ = completed.Exception;
			}
			QueueEngineCleanup(player, libVlc, media);
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
	}

	public void Dispose()
	{
		MediaPlayer player;
		LibVLC libVlc;
		Media media;
		lock (_engineSync)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			_parseCancellation?.Cancel();
			_parseCancellation?.Dispose();
			player = Player;
			libVlc = _libVlc;
			media = _currentMedia;
			_currentMedia = null;
		}
		QueueEngineCleanup(player, libVlc, media);
	}

	private void WirePlayerEvents(MediaPlayer player)
	{
		player.TimeChanged += delegate(object? _, MediaPlayerTimeChangedEventArgs args)
		{
			if (player == Player)
			{
				long num = Math.Max(0L, args.Time);
				long num2 = Interlocked.Exchange(ref _cachedTime, num);
				if (num != num2)
				{
					TouchProgressClock();
				}
			}
		};
		player.LengthChanged += delegate(object? _, MediaPlayerLengthChangedEventArgs args)
		{
			if (player == Player)
			{
				Interlocked.Exchange(ref _cachedLength, Math.Max(0L, args.Length));
			}
		};
		player.Playing += delegate
		{
			if (player == Player)
			{
				Volatile.Write(ref _cachedIsPlaying, 1);
				TouchProgressClock();
				DiagnosticLog.Write("PLAY", "Playback entered Playing state");
				this.PlaybackReady?.Invoke(this, EventArgs.Empty);
			}
		};
		player.Paused += delegate
		{
			if (player == Player)
			{
				Volatile.Write(ref _cachedIsPlaying, 0);
			}
		};
		player.Stopped += delegate
		{
			if (player == Player)
			{
				Volatile.Write(ref _cachedIsPlaying, 0);
			}
		};
		player.EndReached += delegate
		{
			if (player == Player)
			{
				Volatile.Write(ref _cachedIsPlaying, 0);
				Volatile.Write(ref _desiredIsPlaying, 0);
				DiagnosticLog.Write("PLAY", "Playback reached end of media");
				this.Ended?.Invoke(this, EventArgs.Empty);
			}
		};
		player.EncounteredError += delegate
		{
			if (player == Player)
			{
				Volatile.Write(ref _cachedIsPlaying, 0);
				Volatile.Write(ref _desiredIsPlaying, 0);
				DiagnosticLog.Write("PLAY", "LibVLC reported a playback error");
				this.PlaybackError?.Invoke(this, EventArgs.Empty);
			}
		};
	}

	private void EnsureEngine(string visualizationMode, string audioBackend, string? spatialAudioMode, string? equalizerPreset)
	{
		string backend = NormalizeAudioBackend(audioBackend);
		string spatialAudio = AudioEffectPresets.NormalizeSpatialAudio(spatialAudioMode);
		string equalizer = AudioEffectPresets.NormalizeEqualizer(equalizerPreset);
		if (string.Equals(_engineVisualizationMode, visualizationMode, StringComparison.OrdinalIgnoreCase) && string.Equals(_engineAudioBackend, backend, StringComparison.OrdinalIgnoreCase) && string.Equals(_engineSpatialAudioMode, spatialAudio, StringComparison.OrdinalIgnoreCase))
		{
			if (!string.Equals(_equalizerPreset, equalizer, StringComparison.OrdinalIgnoreCase))
			{
				SetEqualizerPreset(equalizer);
			}
			return;
		}
		EngineResources fresh = CreateEngineResources(visualizationMode, backend, spatialAudio, equalizer, Volume);
		MediaPlayer oldPlayer;
		LibVLC oldLibVlc;
		Media oldMedia;
		lock (_engineSync)
		{
			oldPlayer = Player;
			oldLibVlc = _libVlc;
			oldMedia = _currentMedia;
			Player = fresh.Player;
			_libVlc = fresh.LibVlc;
			_currentMedia = null;
			_engineVisualizationMode = visualizationMode;
			_engineAudioBackend = backend;
			_engineSpatialAudioMode = spatialAudio;
			_equalizerPreset = equalizer;
			_parseCancellation?.Cancel();
			Interlocked.Increment(ref _playRequestVersion);
			ResetCachedPlaybackState();
		}
		this.PlayerChanged?.Invoke(this, EventArgs.Empty);
		QueueEngineCleanup(oldPlayer, oldLibVlc, oldMedia);
	}

	private void CreateInitialEngine(string visualizationMode, string audioBackend, string spatialAudioMode, string equalizerPreset, int volume)
	{
		string backend = NormalizeAudioBackend(audioBackend);
		string spatialAudio = AudioEffectPresets.NormalizeSpatialAudio(spatialAudioMode);
		string equalizer = AudioEffectPresets.NormalizeEqualizer(equalizerPreset);
		EngineResources resources = CreateEngineResources(visualizationMode, backend, spatialAudio, equalizer, volume);
		_libVlc = resources.LibVlc;
		Player = resources.Player;
		_engineVisualizationMode = visualizationMode;
		_engineAudioBackend = backend;
		_engineSpatialAudioMode = spatialAudio;
		_equalizerPreset = equalizer;
		Volatile.Write(ref _cachedVolume, Math.Clamp(volume, 0, 100));
	}

	private EngineResources CreateEngineResources(string visualizationMode, string audioBackend, string spatialAudioMode, string equalizerPreset, int volume)
	{
		List<string> arguments = new List<string> { "--quiet", "--no-video-title-show", "--no-metadata-network-access" };
		string audioOutput = AudioOutputModule(audioBackend);
		if (audioOutput.Length > 0)
		{
			arguments.Add("--aout=" + audioOutput);
		}
		string audioFilter = AudioEffectPresets.SpatialFilter(spatialAudioMode);
		if (audioFilter.Length > 0)
		{
			arguments.Add("--audio-filter=" + audioFilter);
		}
		string effect = VisualizationEffect(visualizationMode);
		if (effect.Length > 0)
		{
			arguments.Add("--audio-visual=visual");
			arguments.Add("--effect-list=" + effect);
		}
		LibVLC libVLC = new LibVLC(arguments.ToArray());
		MediaPlayer player = new MediaPlayer(libVLC)
		{
			Volume = Math.Clamp(volume, 0, 100)
		};
		WirePlayerEvents(player);
		ApplyEqualizer(player, equalizerPreset);
		DiagnosticLog.Write("ENGINE", $"Created LibVLC engine: audio={audioBackend}, visual={visualizationMode}, spatial={spatialAudioMode}, eq={equalizerPreset}");
		return new EngineResources(libVLC, player);
	}

	private static void ApplyEqualizer(MediaPlayer player, string? preset)
	{
		try
		{
			EqualizerProfile profile = AudioEffectPresets.GetProfile(preset);
			if ((object)profile == null)
			{
				player.UnsetEqualizer();
				return;
			}
			using Equalizer equalizer = new Equalizer();
			equalizer.SetPreamp(profile.Preamp);
			int count = Math.Min((int)equalizer.BandCount, profile.Bands.Count);
			for (int index = 0; index < count; index++)
			{
				equalizer.SetAmp(profile.Bands[index], (uint)index);
			}
			if (!player.SetEqualizer(equalizer))
			{
				DiagnosticLog.Write("AUDIO", "LibVLC rejected equalizer preset '" + preset + "'");
			}
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("AUDIO", "Could not apply equalizer preset '" + preset + "'", exception);
		}
	}

	private static Media CreateMedia(LibVLC libVlc, string path, PlaybackEngineProfile profile, string? sidecarSubtitlePath)
	{
		Media media = new Media(libVlc, new Uri(path));
		int cacheMilliseconds = Math.Clamp(profile.CacheMilliseconds, 500, 30000);
		media.AddOption($":file-caching={cacheMilliseconds}");
		media.AddOption($":disc-caching={cacheMilliseconds}");
		if (profile.IsNetworkSource)
		{
			media.AddOption($":network-caching={cacheMilliseconds}");
		}
		AddHardwareOptions(media, profile.HardwareDecoding);
		AddVideoOutputOption(media, profile.VideoOutput);
		AddSidecarSubtitle(media, sidecarSubtitlePath);
		return media;
	}

	private async Task ParseMediaAsync(Media media, CancellationToken cancellationToken)
	{
		try
		{
			await media.Parse(MediaParseOptions.ParseLocal, 5000, cancellationToken);
			if (cancellationToken.IsCancellationRequested || media != _currentMedia)
			{
				return;
			}
			uint width = 0u;
			uint height = 0u;
			double frameRate = 0.0;
			uint sampleRate = 0u;
			uint channels = 0u;
			int audioBitrate = 0;
			int videoBitrate = 0;
			string audioCodec = "-";
			string videoCodec = "-";
			bool hasAudio = false;
			bool hasVideo = false;
			MediaTrack[] tracks = media.Tracks;
			for (int i = 0; i < tracks.Length; i++)
			{
				MediaTrack track = tracks[i];
				checked
				{
					if (track.TrackType == TrackType.Audio)
					{
						hasAudio = true;
						sampleRate = track.Data.Audio.Rate;
						channels = track.Data.Audio.Channels;
						audioBitrate = (int)Math.Min(track.Bitrate, 2147483647u);
						audioCodec = FourCc(track.Codec);
					}
					else if (track.TrackType == TrackType.Video)
					{
						hasVideo = true;
						width = track.Data.Video.Width;
						height = track.Data.Video.Height;
						if (track.Data.Video.FrameRateDen != 0)
						{
							frameRate = (double)track.Data.Video.FrameRateNum / (double)track.Data.Video.FrameRateDen;
						}
						videoBitrate = (int)Math.Min(track.Bitrate, 2147483647u);
						videoCodec = FourCc(track.Codec);
					}
				}
			}
			this.MediaDetailsChanged?.Invoke(new MediaDetails
			{
				Width = width,
				Height = height,
				FrameRate = frameRate,
				SampleRate = sampleRate,
				Channels = channels,
				AudioBitrate = audioBitrate,
				VideoBitrate = videoBitrate,
				AudioCodec = audioCodec,
				VideoCodec = videoCodec,
				HasAudio = hasAudio,
				HasVideo = hasVideo
			});
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			DiagnosticLog.Write("MEDIA", "Could not parse media details", exception);
		}
	}

	private void RunNativeCommand(MediaPlayer player, Action<MediaPlayer> action, string description, bool trackBound = true)
	{
		int requestVersion = Volatile.Read(in _playRequestVersion);
		SemaphoreSlim gate = _commandGates.GetValue(player, (MediaPlayer _) => new SemaphoreSlim(1, 1));
		Task.Run(async delegate
		{
			await gate.WaitAsync();
			try
			{
				if (!_disposed && player == Player && (!trackBound || requestVersion == Volatile.Read(in _playRequestVersion)))
				{
					action(player);
				}
			}
			catch (Exception exception)
			{
				DiagnosticLog.Write("COMMAND", description, exception);
			}
			finally
			{
				gate.Release();
			}
		});
	}

	private void QueueEngineCleanup(MediaPlayer? player, LibVLC? libVlc, Media? media)
	{
		if (player == null || (object)libVlc == null)
		{
			return;
		}
		SemaphoreSlim gate = _commandGates.GetValue(player, (MediaPlayer _) => new SemaphoreSlim(1, 1));
		Task.Run(async delegate
		{
			await gate.WaitAsync();
			try
			{
				try
				{
					player.Stop();
				}
				catch
				{
				}
				try
				{
					player.Dispose();
				}
				catch
				{
				}
				try
				{
					media?.Dispose();
				}
				catch
				{
				}
				try
				{
					libVlc.Dispose();
				}
				catch
				{
				}
			}
			finally
			{
				gate.Release();
			}
		});
	}

	private void ResetCachedPlaybackState()
	{
		Interlocked.Exchange(ref _cachedTime, 0L);
		Interlocked.Exchange(ref _cachedLength, 0L);
		Volatile.Write(ref _cachedIsPlaying, 0);
		Volatile.Write(ref _desiredIsPlaying, 0);
		Interlocked.Increment(ref _pauseIntentVersion);
		TouchProgressClock();
	}

	private void TouchProgressClock()
	{
		Interlocked.Exchange(ref _lastProgressUtcTicks, DateTime.UtcNow.Ticks);
	}

	private static IReadOnlyList<MediaTrackOption> MapTracks(IEnumerable<TrackDescription>? tracks, string fallback)
	{
		if (tracks == null)
		{
			return Array.Empty<MediaTrackOption>();
		}
		return tracks.Select((TrackDescription track) => new MediaTrackOption(track.Id, string.IsNullOrWhiteSpace(track.Name) ? $"{fallback} {track.Id}" : track.Name)).ToList();
	}

	private static string FourCc(uint value)
	{
		if (value == 0)
		{
			return "-";
		}
		return Encoding.ASCII.GetString(BitConverter.GetBytes(value)).TrimEnd(new char[2] { '\0', ' ' });
	}

	private static void AddHardwareOptions(Media media, string mode)
	{
		media.AddOption(":avcodec-hw=" + mode switch
		{
			"D3D11VA" => "d3d11va",
			"DXVA2" => "dxva2",
			"Disabled" => "none",
			_ => "any",
		});
	}

	private static void AddVideoOutputOption(Media media, string mode)
	{
		string value = mode switch
		{
			"Direct3D11" => "direct3d11",
			"Direct3D9" => "direct3d9",
			"OpenGL" => "glwin32",
			_ => "",
		};
		if (value.Length > 0)
		{
			media.AddOption(":vout=" + value);
		}
	}

	private static string VisualizationEffect(string mode)
	{
		return mode switch
		{
			"Scope" => "scope",
			"Spectrometer" => "spectrometer",
			"Spectrum" => "spectrum",
			_ => "",
		};
	}

	private static string NormalizeAudioBackend(string? value)
	{
		return value switch
		{
			"Auto" => "Auto",
			"Wasapi" => "Wasapi",
			"WaveOut" => "WaveOut",
			_ => "DirectSound",
		};
	}

	private static string AudioOutputModule(string backend)
	{
		return backend switch
		{
			"Auto" => "",
			"Wasapi" => "mmdevice",
			"WaveOut" => "waveout",
			_ => "directsound",
		};
	}

	private static void AddSidecarSubtitle(Media media, string? subtitlePath)
	{
		if (!string.IsNullOrWhiteSpace(subtitlePath))
		{
			media.AddOption(":sub-file=" + subtitlePath);
		}
	}
}
