using System.Text;
using System.IO;
using System.Runtime.CompilerServices;
using LibVLCSharp.Shared;

namespace OfflineMusicLibrary;

public sealed class PlaybackService : IDisposable
{
    private readonly object _engineSync = new();
    private readonly ConditionalWeakTable<MediaPlayer, SemaphoreSlim> _commandGates = new();
    private LibVLC _libVlc = null!;
    private Media? _currentMedia;
    private CancellationTokenSource? _parseCancellation;
    private double _requestedRate = 1.0;
    private string _preferredAudioDeviceId = "";
    private string _engineVisualizationMode = "Off";
    private string _engineAudioBackend = "DirectSound";
    private long _cachedTime;
    private long _cachedLength;
    private long _lastProgressUtcTicks = DateTime.UtcNow.Ticks;
    private int _cachedIsPlaying;
    private int _desiredIsPlaying;
    private int _cachedVolume = 76;
    private int _playRequestVersion;
    private int _pauseIntentVersion;
    private bool _disposed;

    public PlaybackService()
    {
        Core.Initialize();
        CreateInitialEngine("Off", "DirectSound", 76);
    }

    public MediaPlayer Player { get; private set; } = null!;
    public event EventHandler? Ended;
    public event EventHandler? PlaybackError;
    public event EventHandler? PlaybackReady;
    public event EventHandler? PlayerChanged;
    public event Action<MediaDetails>? MediaDetailsChanged;

    public bool IsPlaying => Volatile.Read(ref _cachedIsPlaying) != 0;
    public long Time => Math.Max(0, Interlocked.Read(ref _cachedTime));
    public long Length => Math.Max(0, Interlocked.Read(ref _cachedLength));
    public float Fps => 0;
    public DateTime LastProgressUtc => new(
        Math.Clamp(Interlocked.Read(ref _lastProgressUtcTicks), DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks),
        DateTimeKind.Utc);

    public int Volume
    {
        get => Volatile.Read(ref _cachedVolume);
        set
        {
            var volume = Math.Clamp(value, 0, 100);
            Volatile.Write(ref _cachedVolume, volume);
            RunNativeCommand(Player, player => player.Volume = volume, "set volume", trackBound: false);
        }
    }

    public void Play(string path, AppState state, bool isVideo)
    {
        _requestedRate = Math.Clamp(state.PlaybackRate, 0.25, 4.0);
        _preferredAudioDeviceId = state.PreferredAudioDeviceId;
        EnsureEngine(isVideo ? "Off" : state.VisualizationMode, state.AudioBackend);

        var player = Player;
        var next = CreateMedia(_libVlc, path, state);
        CancellationTokenSource parseCancellation;
        Media? old;
        lock (_engineSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            old = _currentMedia;
            _currentMedia = next;
            _parseCancellation?.Cancel();
            _parseCancellation?.Dispose();
            parseCancellation = new CancellationTokenSource();
            _parseCancellation = parseCancellation;
            Interlocked.Increment(ref _playRequestVersion);
            ResetCachedPlaybackState();
        }

        DiagnosticLog.Write("PLAY", $"Opening '{path}' via {_engineAudioBackend}, cache 1500 ms");
        Volatile.Write(ref _desiredIsPlaying, 1);
        if (!player.Play(next))
        {
            Volatile.Write(ref _desiredIsPlaying, 0);
            next.Dispose();
            throw new InvalidOperationException("底层播放引擎拒绝了该媒体文件。请检查文件是否损坏或仍可访问。");
        }

        RunNativeCommand(player, activePlayer =>
        {
            activePlayer.SetRate((float)_requestedRate);
            if (!string.IsNullOrWhiteSpace(_preferredAudioDeviceId))
                activePlayer.SetOutputDevice(_preferredAudioDeviceId, null);
        }, "apply playback options", trackBound: false);
        old?.Dispose();
        _ = ParseMediaAsync(next, parseCancellation.Token);
    }

    public void TogglePause()
    {
        int desired;
        int next;
        do
        {
            desired = Volatile.Read(ref _desiredIsPlaying);
            next = desired == 0 ? 1 : 0;
        } while (Interlocked.CompareExchange(ref _desiredIsPlaying, next, desired) != desired);
        var shouldPause = next == 0;
        Volatile.Write(ref _cachedIsPlaying, next);
        TouchProgressClock();
        var player = Player;
        var intentVersion = Interlocked.Increment(ref _pauseIntentVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(70);
            if (intentVersion != Volatile.Read(ref _pauseIntentVersion))
                return;
            RunNativeCommand(player, activePlayer => activePlayer.SetPause(shouldPause),
                shouldPause ? "pause" : "resume");
        });
    }

    public void Seek(long milliseconds)
    {
        var length = Length;
        if (length <= 0)
            return;
        var target = Math.Clamp(milliseconds, 0, length);
        Interlocked.Exchange(ref _cachedTime, target);
        TouchProgressClock();
        RunNativeCommand(Player, player => player.Time = target, "seek");
    }

    public bool SetRate(double rate)
    {
        _requestedRate = Math.Clamp(rate, 0.25, 4.0);
        RunNativeCommand(Player, player => player.SetRate((float)_requestedRate), "set rate", trackBound: false);
        return true;
    }

    public bool TakeSnapshot(string path) => Player.TakeSnapshot(0, path, 0, 0);

    public IReadOnlyList<MediaTrackOption> GetAudioTracks() => MapTracks(Player.AudioTrackDescription, "音轨");

    public IReadOnlyList<MediaTrackOption> GetVideoTracks() => MapTracks(Player.VideoTrackDescription, "视频轨");

    public IReadOnlyList<MediaTrackOption> GetSubtitleTracks() => MapTracks(Player.SpuDescription, "字幕");

    public IReadOnlyList<AudioDeviceOption> GetAudioDevices()
    {
        var devices = new List<AudioDeviceOption> { new("", "系统默认设备") };
        var available = Player.AudioOutputDeviceEnum;
        if (available is null)
            return devices;
        devices.AddRange(available
            .Where(device => !string.IsNullOrWhiteSpace(device.DeviceIdentifier))
            .Select(device => new AudioDeviceOption(device.DeviceIdentifier, device.Description)));
        return devices;
    }

    public MediaControlSnapshot CaptureMediaControls()
    {
        var player = Player;
        return new MediaControlSnapshot(
            MapTracks(player.AudioTrackDescription, "音轨"), player.AudioTrack,
            MapTracks(player.VideoTrackDescription, "视频轨"), player.VideoTrack,
            MapTracks(player.SpuDescription, "字幕"), player.Spu,
            GetAudioDevices());
    }

    public bool SetAudioTrack(int id)
    {
        RunNativeCommand(Player, player => player.SetAudioTrack(id), "set audio track");
        return true;
    }

    public bool SetVideoTrack(int id)
    {
        RunNativeCommand(Player, player => player.SetVideoTrack(id), "set video track");
        return true;
    }

    public bool SetSubtitleTrack(int id)
    {
        RunNativeCommand(Player, player => player.SetSpu(id), "set subtitle track");
        return true;
    }

    public void SetSubtitleDelay(long milliseconds) =>
        RunNativeCommand(Player, player => player.SetSpuDelay(milliseconds * 1000), "set subtitle delay");

    public void SetAudioDevice(string deviceId)
    {
        _preferredAudioDeviceId = deviceId;
        if (!string.IsNullOrWhiteSpace(deviceId))
            RunNativeCommand(Player, player => player.SetOutputDevice(deviceId, null), "set audio device", trackBound: false);
    }

    public void Stop()
    {
        Volatile.Write(ref _desiredIsPlaying, 0);
        Volatile.Write(ref _cachedIsPlaying, 0);
        Interlocked.Increment(ref _pauseIntentVersion);
        RunNativeCommand(Player, player => player.Stop(), "stop");
    }

    public async Task<bool> RecoverAsync(
        string path,
        AppState state,
        bool isVideo,
        long resumeAtMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var expectedRequest = Volatile.Read(ref _playRequestVersion);
        var visualization = isVideo ? "Off" : state.VisualizationMode;
        var backend = NormalizeAudioBackend(state.AudioBackend);
        var volume = Volume;

        DiagnosticLog.Write("WATCHDOG", $"Rebuilding engine for '{path}' at {resumeAtMilliseconds} ms");
        var fresh = await Task.Run(
            () => CreateEngineResources(visualization, backend, volume),
            cancellationToken);
        var media = CreateMedia(fresh.LibVlc, path, state);

        MediaPlayer? oldPlayer = null;
        LibVLC? oldLibVlc = null;
        Media? oldMedia = null;
        CancellationTokenSource parseCancellation;
        lock (_engineSync)
        {
            if (_disposed || expectedRequest != Volatile.Read(ref _playRequestVersion))
            {
                media.Dispose();
                QueueEngineCleanup(fresh.Player, fresh.LibVlc, null);
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
            _parseCancellation?.Cancel();
            _parseCancellation?.Dispose();
            parseCancellation = new CancellationTokenSource();
            _parseCancellation = parseCancellation;
            Interlocked.Increment(ref _playRequestVersion);
            ResetCachedPlaybackState();
        }

        PlayerChanged?.Invoke(this, EventArgs.Empty);
        QueueEngineCleanup(oldPlayer, oldLibVlc, oldMedia);

        var startTask = Task.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(_preferredAudioDeviceId))
                fresh.Player.SetOutputDevice(_preferredAudioDeviceId, null);
            Volatile.Write(ref _desiredIsPlaying, 1);
            var started = fresh.Player.Play(media);
            fresh.Player.SetRate((float)_requestedRate);
            return started;
        }, cancellationToken);
        var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(8), cancellationToken));
        if (completed != startTask || !await startTask)
        {
            DiagnosticLog.Write("WATCHDOG", "Replacement engine did not start within eight seconds");
            return false;
        }

        if (resumeAtMilliseconds > 0)
        {
            await Task.Delay(600, cancellationToken);
            if (ReferenceEquals(Player, fresh.Player))
            {
                var target = Math.Max(0, resumeAtMilliseconds);
                Interlocked.Exchange(ref _cachedTime, target);
                await Task.Run(() => fresh.Player.Time = target, cancellationToken);
            }
        }

        _ = ParseMediaAsync(media, parseCancellation.Token);
        DiagnosticLog.Write("WATCHDOG", "Replacement engine started successfully");
        return true;
    }

    public void Dispose()
    {
        MediaPlayer player;
        LibVLC libVlc;
        Media? media;
        lock (_engineSync)
        {
            if (_disposed)
                return;
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
        player.TimeChanged += (_, args) =>
        {
            if (!ReferenceEquals(player, Player))
                return;
            var next = Math.Max(0, args.Time);
            var previous = Interlocked.Exchange(ref _cachedTime, next);
            if (next != previous)
                TouchProgressClock();
        };
        player.LengthChanged += (_, args) =>
        {
            if (ReferenceEquals(player, Player))
                Interlocked.Exchange(ref _cachedLength, Math.Max(0, args.Length));
        };
        player.Playing += (_, _) =>
        {
            if (!ReferenceEquals(player, Player))
                return;
            Volatile.Write(ref _cachedIsPlaying, 1);
            TouchProgressClock();
            DiagnosticLog.Write("PLAY", "Playback entered Playing state");
            PlaybackReady?.Invoke(this, EventArgs.Empty);
        };
        player.Paused += (_, _) =>
        {
            if (ReferenceEquals(player, Player))
                Volatile.Write(ref _cachedIsPlaying, 0);
        };
        player.Stopped += (_, _) =>
        {
            if (ReferenceEquals(player, Player))
                Volatile.Write(ref _cachedIsPlaying, 0);
        };
        player.EndReached += (_, _) =>
        {
            if (!ReferenceEquals(player, Player))
                return;
            Volatile.Write(ref _cachedIsPlaying, 0);
            Volatile.Write(ref _desiredIsPlaying, 0);
            DiagnosticLog.Write("PLAY", "Playback reached end of media");
            Ended?.Invoke(this, EventArgs.Empty);
        };
        player.EncounteredError += (_, _) =>
        {
            if (!ReferenceEquals(player, Player))
                return;
            Volatile.Write(ref _cachedIsPlaying, 0);
            Volatile.Write(ref _desiredIsPlaying, 0);
            DiagnosticLog.Write("PLAY", "LibVLC reported a playback error");
            PlaybackError?.Invoke(this, EventArgs.Empty);
        };
    }

    private void EnsureEngine(string visualizationMode, string audioBackend)
    {
        var backend = NormalizeAudioBackend(audioBackend);
        if (string.Equals(_engineVisualizationMode, visualizationMode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_engineAudioBackend, backend, StringComparison.OrdinalIgnoreCase))
            return;

        var fresh = CreateEngineResources(visualizationMode, backend, Volume);
        MediaPlayer oldPlayer;
        LibVLC oldLibVlc;
        Media? oldMedia;
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
            _parseCancellation?.Cancel();
            Interlocked.Increment(ref _playRequestVersion);
            ResetCachedPlaybackState();
        }
        PlayerChanged?.Invoke(this, EventArgs.Empty);
        QueueEngineCleanup(oldPlayer, oldLibVlc, oldMedia);
    }

    private void CreateInitialEngine(string visualizationMode, string audioBackend, int volume)
    {
        var resources = CreateEngineResources(visualizationMode, NormalizeAudioBackend(audioBackend), volume);
        _libVlc = resources.LibVlc;
        Player = resources.Player;
        _engineVisualizationMode = visualizationMode;
        _engineAudioBackend = NormalizeAudioBackend(audioBackend);
        Volatile.Write(ref _cachedVolume, Math.Clamp(volume, 0, 100));
    }

    private EngineResources CreateEngineResources(string visualizationMode, string audioBackend, int volume)
    {
        var arguments = new List<string>
        {
            "--quiet",
            "--no-video-title-show",
            "--no-metadata-network-access"
        };
        var audioOutput = AudioOutputModule(audioBackend);
        if (audioOutput.Length > 0)
            arguments.Add($"--aout={audioOutput}");
        var effect = VisualizationEffect(visualizationMode);
        if (effect.Length > 0)
        {
            arguments.Add("--audio-visual=visual");
            arguments.Add($"--effect-list={effect}");
        }

        var libVlc = new LibVLC(arguments.ToArray());
        var player = new MediaPlayer(libVlc) { Volume = Math.Clamp(volume, 0, 100) };
        WirePlayerEvents(player);
        DiagnosticLog.Write("ENGINE", $"Created LibVLC engine: audio={audioBackend}, visual={visualizationMode}");
        return new EngineResources(libVlc, player);
    }

    private static Media CreateMedia(LibVLC libVlc, string path, AppState state)
    {
        var media = new Media(libVlc, new Uri(path));
        media.AddOption(":file-caching=1500");
        media.AddOption(":disc-caching=1500");
        AddHardwareOptions(media, state.HardwareDecoding);
        AddVideoOutputOption(media, state.VideoOutput);
        AddSidecarSubtitle(media, path);
        return media;
    }

    private async Task ParseMediaAsync(Media media, CancellationToken cancellationToken)
    {
        try
        {
            await media.Parse(MediaParseOptions.ParseLocal, 5000, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(media, _currentMedia))
                return;

            uint width = 0;
            uint height = 0;
            double frameRate = 0;
            uint sampleRate = 0;
            uint channels = 0;
            var audioBitrate = 0;
            var videoBitrate = 0;
            var audioCodec = "-";
            var videoCodec = "-";
            var hasAudio = false;
            var hasVideo = false;

            foreach (var track in media.Tracks)
            {
                if (track.TrackType == TrackType.Audio)
                {
                    hasAudio = true;
                    sampleRate = track.Data.Audio.Rate;
                    channels = track.Data.Audio.Channels;
                    audioBitrate = checked((int)Math.Min(track.Bitrate, int.MaxValue));
                    audioCodec = FourCc(track.Codec);
                }
                else if (track.TrackType == TrackType.Video)
                {
                    hasVideo = true;
                    width = track.Data.Video.Width;
                    height = track.Data.Video.Height;
                    if (track.Data.Video.FrameRateDen > 0)
                        frameRate = track.Data.Video.FrameRateNum / (double)track.Data.Video.FrameRateDen;
                    videoBitrate = checked((int)Math.Min(track.Bitrate, int.MaxValue));
                    videoCodec = FourCc(track.Codec);
                }
            }

            MediaDetailsChanged?.Invoke(new MediaDetails
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

    private void RunNativeCommand(
        MediaPlayer player,
        Action<MediaPlayer> action,
        string description,
        bool trackBound = true)
    {
        var requestVersion = Volatile.Read(ref _playRequestVersion);
        var gate = _commandGates.GetValue(player, _ => new SemaphoreSlim(1, 1));
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                if (!_disposed && ReferenceEquals(player, Player) &&
                    (!trackBound || requestVersion == Volatile.Read(ref _playRequestVersion)))
                    action(player);
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
        if (player is null || libVlc is null)
            return;
        var gate = _commandGates.GetValue(player, _ => new SemaphoreSlim(1, 1));
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                // LibVLC media-player calls are not safe to overlap. Cleanup must
                // wait for pending pause/seek/stop commands for this exact player;
                // otherwise Stop and Dispose can race inside native code.
                try { player.Stop(); } catch { }
                try { player.Dispose(); } catch { }
                try { media?.Dispose(); } catch { }
                try { libVlc.Dispose(); } catch { }
            }
            finally
            {
                gate.Release();
            }
        });
    }

    private void ResetCachedPlaybackState()
    {
        Interlocked.Exchange(ref _cachedTime, 0);
        Interlocked.Exchange(ref _cachedLength, 0);
        Volatile.Write(ref _cachedIsPlaying, 0);
        Volatile.Write(ref _desiredIsPlaying, 0);
        Interlocked.Increment(ref _pauseIntentVersion);
        TouchProgressClock();
    }

    private void TouchProgressClock() => Interlocked.Exchange(ref _lastProgressUtcTicks, DateTime.UtcNow.Ticks);

    private static IReadOnlyList<MediaTrackOption> MapTracks(
        IEnumerable<LibVLCSharp.Shared.Structures.TrackDescription>? tracks,
        string fallback)
    {
        if (tracks is null)
            return [];
        return tracks.Select(track => new MediaTrackOption(
            track.Id,
            string.IsNullOrWhiteSpace(track.Name) ? $"{fallback} {track.Id}" : track.Name)).ToList();
    }

    private static string FourCc(uint value)
    {
        if (value == 0)
            return "-";
        return Encoding.ASCII.GetString(BitConverter.GetBytes(value)).TrimEnd('\0', ' ');
    }

    private static void AddHardwareOptions(Media media, string mode)
    {
        var value = mode switch
        {
            "D3D11VA" => "d3d11va",
            "DXVA2" => "dxva2",
            "Disabled" => "none",
            _ => "any"
        };
        media.AddOption($":avcodec-hw={value}");
    }

    private static void AddVideoOutputOption(Media media, string mode)
    {
        var value = mode switch
        {
            "Direct3D11" => "direct3d11",
            "Direct3D9" => "direct3d9",
            "OpenGL" => "glwin32",
            _ => ""
        };
        if (value.Length > 0)
            media.AddOption($":vout={value}");
    }

    private static string VisualizationEffect(string mode) => mode switch
    {
        "Scope" => "scope",
        "Spectrometer" => "spectrometer",
        "Spectrum" => "spectrum",
        _ => ""
    };

    private static string NormalizeAudioBackend(string? value) => value switch
    {
        "Auto" => "Auto",
        "Wasapi" => "Wasapi",
        "WaveOut" => "WaveOut",
        _ => "DirectSound"
    };

    private static string AudioOutputModule(string backend) => backend switch
    {
        "Auto" => "",
        "Wasapi" => "mmdevice",
        "WaveOut" => "waveout",
        _ => "directsound"
    };

    private static void AddSidecarSubtitle(Media media, string mediaPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(mediaPath) ?? "", Path.GetFileNameWithoutExtension(mediaPath));
        var subtitle = new[] { ".ass", ".srt", ".vtt" }
            .Select(extension => stem + extension)
            .FirstOrDefault(File.Exists);
        if (subtitle is not null)
            media.AddOption($":sub-file={subtitle}");
    }

    private sealed record EngineResources(LibVLC LibVlc, MediaPlayer Player);
}

public sealed record MediaControlSnapshot(
    IReadOnlyList<MediaTrackOption> AudioTracks,
    int SelectedAudioTrack,
    IReadOnlyList<MediaTrackOption> VideoTracks,
    int SelectedVideoTrack,
    IReadOnlyList<MediaTrackOption> SubtitleTracks,
    int SelectedSubtitleTrack,
    IReadOnlyList<AudioDeviceOption> AudioDevices);
