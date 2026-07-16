using LibVLCSharp.Shared;

namespace OfflineMusicLibrary;

public sealed class PlaybackService : IDisposable
{
    private readonly object _sync = new();
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private bool _initializationAttempted;
    private string _initializationError = "";

    public event Action<long, long>? PositionChanged;
    public event Action? Ended;
    public event Action<string>? Failed;

    public bool IsPlaying => _player?.IsPlaying ?? false;
    public string InitializationError => _initializationError;

    public int Volume
    {
        get => _player?.Volume ?? 76;
        set
        {
            if (EnsureInitialized())
                _player!.Volume = Math.Clamp(value, 0, 100);
        }
    }

    public bool EnsureInitialized()
    {
        lock (_sync)
        {
            if (_player is not null)
                return true;
            if (_initializationAttempted)
                return false;
            _initializationAttempted = true;

            try
            {
                InitializeNativeEngine();
                _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--network-caching=1000", "--file-caching=1200");
                _player = new MediaPlayer(_libVlc);
                _player.TimeChanged += (_, args) => PositionChanged?.Invoke(args.Time, Math.Max(0, _player.Length));
                _player.LengthChanged += (_, args) => PositionChanged?.Invoke(Math.Max(0, _player.Time), args.Length);
                _player.EndReached += (_, _) => Ended?.Invoke();
                _player.EncounteredError += (_, _) => Failed?.Invoke("VLC 播放引擎报告错误，请检查文件或 VLC/libVLC 安装。");
                return true;
            }
            catch (Exception exception)
            {
                _initializationError = BuildInitializationError(exception);
                DiagnosticLog.Write("Playback", "LibVLC 初始化失败。", exception);
                return false;
            }
        }
    }

    public void Play(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".ncm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("NCM 是加密容器，已收录进曲库，但需要先转换成 FLAC/MP3 才能播放。");
        if (!File.Exists(path))
            throw new FileNotFoundException("歌曲文件已经移动或删除。", path);
        if (!EnsureInitialized())
            throw new InvalidOperationException(_initializationError);

        var media = new Media(_libVlc!, new Uri(Path.GetFullPath(path)));
        var old = Interlocked.Exchange(ref _media, media);
        if (!_player!.Play(media))
        {
            media.Dispose();
            throw new InvalidOperationException("播放引擎拒绝了该文件。请检查文件是否损坏。");
        }
        old?.Dispose();
    }

    public void TogglePause()
    {
        if (!EnsureInitialized())
            throw new InvalidOperationException(_initializationError);
        _player!.SetPause(_player.IsPlaying);
    }

    public void Seek(long milliseconds)
    {
        if (_player is null || _player.Length <= 0)
            return;
        _player.Time = Math.Clamp(milliseconds, 0, _player.Length);
    }

    public void Stop() => _player?.Stop();

    private static void InitializeNativeEngine()
    {
        try
        {
            Core.Initialize();
            return;
        }
        catch when (OperatingSystem.IsMacOS())
        {
            foreach (var candidate in new[]
                     {
                         "/Applications/VLC.app/Contents/MacOS/lib",
                         "/Applications/VLC.app/Contents/MacOS"
                     })
            {
                if (!Directory.Exists(candidate))
                    continue;
                Core.Initialize(candidate);
                return;
            }
            throw;
        }
    }

    private static string BuildInitializationError(Exception exception)
    {
        var platformHelp = OperatingSystem.IsLinux()
            ? "Linux 需要系统 libVLC：Debian/Ubuntu 可安装 vlc 与 libvlc5，Fedora 可安装 vlc。"
            : OperatingSystem.IsMacOS()
                ? "macOS 请安装 VLC.app；Intel 包也可使用随包附带的 LibVLC。"
                : "请重新解压完整程序包，确认 libvlc 文件没有被安全软件隔离。";
        return $"播放引擎暂不可用。{platformHelp}\n\n{exception.Message}";
    }

    public void Dispose()
    {
        try
        {
            _player?.Stop();
        }
        catch
        {
        }
        _media?.Dispose();
        _player?.Dispose();
        _libVlc?.Dispose();
    }
}
