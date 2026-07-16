using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace OfflineMusicLibrary.Tests;

public sealed class LibraryTests
{
    [Fact]
    public void LyricsService_MergesTranslationAtTheSameTimestamp()
    {
        WithTemporaryDirectory(directory =>
        {
            var audio = Path.Combine(directory, "song.flac");
            File.WriteAllText(Path.ChangeExtension(audio, ".lrc"),
                "[00:01.00]Hello\n[00:01.00]你好\n[00:03.25]World", Encoding.UTF8);

            var lines = LyricsService.LoadForTrack(audio);

            Assert.Equal(2, lines.Count);
            Assert.Equal("Hello", lines[0].Original);
            Assert.Equal("你好", lines[0].Translation);
            Assert.Equal(3250, lines[1].TimeMs);
        });
    }

    [Fact]
    public void LyricsService_MergesSeparateChineseLyrics()
    {
        WithTemporaryDirectory(directory =>
        {
            var audio = Path.Combine(directory, "song.mp3");
            File.WriteAllText(Path.ChangeExtension(audio, ".lrc"),
                "[00:02.00]Good morning\n[00:05.00]Good night", Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "song.zh.lrc"),
                "[00:02.20]早上好\n[00:05.00]晚安", Encoding.UTF8);

            var lines = LyricsService.LoadForTrack(audio);

            Assert.Equal("早上好", lines[0].Translation);
            Assert.Equal("晚安", lines[1].Translation);
            Assert.Equal(0, LyricsService.FindCurrentIndex(lines, 2100));
            Assert.Equal(1, LyricsService.FindCurrentIndex(lines, 5200));
            Assert.Equal(0, LyricsService.FindCurrentIndex(lines, 5200, offsetMs: 1000));
            Assert.Equal(1, LyricsService.FindCurrentIndex(lines, 4200, offsetMs: -1000));
        });
    }

    [Fact]
    public void LyricsService_ReadsLegacyChineseEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        WithTemporaryDirectory(directory =>
        {
            var audio = Path.Combine(directory, "legacy.flac");
            File.WriteAllText(Path.ChangeExtension(audio, ".lrc"), "[00:01.00]旧编码歌词", Encoding.GetEncoding(936));

            var lines = LyricsService.LoadForTrack(audio);

            Assert.Single(lines);
            Assert.Equal("旧编码歌词", lines[0].Original);
        });
    }

    [Fact]
    public void CoverService_FindsAlbumFolderArtworkCaseInsensitively()
    {
        WithTemporaryDirectory(directory =>
        {
            var audio = Path.Combine(directory, "song.flac");
            var cover = Path.Combine(directory, "FOLDER.PNG");
            File.WriteAllBytes(cover, [1, 2, 3]);

            Assert.Equal(cover, CoverService.FindSidecar(audio), ignoreCase: true);
        });
    }

    [Theory]
    [InlineData("19723756", "19723756")]
    [InlineData("https://music.163.com/playlist?id=19723756", "19723756")]
    [InlineData("https://music.163.com/playlist/19723756", "19723756")]
    public void NetEasePlaylistService_ExtractsPlaylistId(string source, string expected) =>
        Assert.Equal(expected, NetEasePlaylistService.ExtractPlaylistId(source));

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RealLibraryState_HasReadableArtworkAndLyrics()
    {
        var state = await new AppStore().LoadAsync();
        Assert.NotEmpty(state.Tracks);
        var sample = state.Tracks
            .Where(track => track.HasCover && track.HasLyrics && File.Exists(track.FilePath))
            .Select(track => new { Track = track, Lines = LyricsService.LoadForTrack(track.FilePath) })
            .First(item => item.Lines.Count >= 10);

        Assert.NotNull(CoverService.LoadCover(sample.Track));
        Assert.True(sample.Lines.Count >= 10);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NetEasePlaylistService_ReadsPublicPlaylist()
    {
        var state = await new AppStore().LoadAsync();
        var result = await new NetEasePlaylistService().ImportAsync("19723756", state.Tracks);

        Assert.Equal("19723756", result.PlaylistId);
        Assert.NotEmpty(result.PlaylistName);
        Assert.NotEmpty(result.Tracks);
        Assert.Equal(result.Tracks.Count, result.Matched.Count + result.Missing.Count);
    }

    [Fact]
    public async Task NetEasePlaylistService_ImportsEveryIdWhenOnlyOneTrackIsEmbedded()
    {
        const string playlistJson = """
            {
              "code": 200,
              "playlist": {
                "name": "Large playlist",
                "trackCount": 5,
                "trackIds": [{"id":1},{"id":2},{"id":3},{"id":4},{"id":5}],
                "tracks": [{"id":1,"name":"Song 1","artists":[{"name":"Artist"}],"album":{"name":"Album"}}]
              }
            }
            """;
        const string primaryDetailsJson = """
            {"songs":[
              {"id":2,"name":"Song 2","artists":[{"name":"Artist"}],"album":{"name":"Album"}},
              {"id":3,"name":"Song 3","artists":[{"name":"Artist"}],"album":{"name":"Album"}}
            ]}
            """;
        const string fallbackDetailsJson = """
            {"songs":[
              {"id":4,"name":"Song 4","artists":[{"name":"Artist"}],"album":{"name":"Album"}},
              {"id":5,"name":"Song 5","artists":[{"name":"Artist"}],"album":{"name":"Album"}}
            ]}
            """;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var json = path.Contains("playlist/detail", StringComparison.OrdinalIgnoreCase)
                ? playlistJson
                : path.Contains("v3/song/detail", StringComparison.OrdinalIgnoreCase)
                    ? fallbackDetailsJson
                    : primaryDetailsJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var local = Enumerable.Range(1, 5).Select(index => new TrackModel
        {
            Id = $"local-{index}",
            FilePath = $@"G:\Music\Song {index} - Artist.flac",
            Title = $"Song {index}",
            Artist = "Artist",
            Album = "Album",
            CloudId = index == 5 ? "legacy-5" : index.ToString(),
            CloudIds = index == 5 ? ["5"] : []
        }).ToList();

        var result = await new NetEasePlaylistService(httpClient).ImportAsync("12345", local);

        Assert.Equal(5, result.DeclaredTrackCount);
        Assert.Equal(5, result.TrackIdCount);
        Assert.Equal(5, result.ResolvedTrackCount);
        Assert.Equal(5, result.Tracks.Count);
        Assert.Equal(5, result.Matched.Count);
        Assert.Equal(5, result.ExactMatchCount);
        Assert.Empty(result.Missing);
        Assert.Empty(result.UnresolvedTrackIds);
    }

    [Fact]
    public async Task MusicLibraryService_KeepsUnreadableFlacUsingFilenameMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "01 Broken Song - Artist.flac");
            await File.WriteAllBytesAsync(path, [0, 1, 2, 3, 4]);

            var tracks = await new MusicLibraryService().ScanAsync([directory], []);

            var track = Assert.Single(tracks);
            Assert.Equal("Broken Song", track.Title);
            Assert.Equal("Artist", track.Artist);
            Assert.Equal("FLAC", track.Format);
            Assert.Equal(path, track.FilePath);
        }
        finally
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary.Tests"));
            var target = Path.GetFullPath(directory);
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task AppStore_RoundTripsPlaylistsCategoriesAndPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var track = new TrackModel
            {
                Id = "track-1",
                FilePath = @"G:\Music\song.flac",
                Title = "Song",
                Categories = ["夜间"],
                CloudId = "cloud-primary",
                CloudIds = ["cloud-alias"],
                IsFavorite = true,
                PlayCount = 7,
                LastPlayedAt = new DateTime(2026, 7, 16, 20, 30, 0, DateTimeKind.Local)
            };
            var state = new AppState
            {
                Tracks = [track],
                Playlists = [new PlaylistModel
                {
                    Name = "自定义歌单",
                    Description = "工作时播放",
                    CoverPath = @"C:\Artwork\playlist.png",
                    Tags = ["工作", "无损"],
                    TrackIds = [track.Id],
                    CreatedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local)
                }],
                Volume = 42,
                ShuffleEnabled = true,
                ShuffleMode = "Smart",
                RepeatMode = "One",
                PlaybackRate = 1.25,
                VisualizationMode = "Scope",
                HardwareDecoding = "D3D11VA",
                VideoOutput = "Direct3D11",
                PreferredAudioDeviceId = "test-device",
                GlobalHotkeysEnabled = true,
                SystemMediaKeysEnabled = false,
                LyricOffsetMs = 500,
                DesktopLyricsFontSize = 31,
                DesktopLyricsTranslationFontSize = 19,
                DesktopLyricsClickToActivate = true,
                DesktopLyricsColorScheme = "HighContrast",
                DesktopLyricsShowTranslation = false
            };
            var store = new AppStore(directory);

            await store.SaveAsync(state);
            var loaded = await store.LoadAsync();

            Assert.Equal(42, loaded.Volume);
            Assert.True(loaded.ShuffleEnabled);
            Assert.Equal("Smart", loaded.ShuffleMode);
            Assert.Equal("One", loaded.RepeatMode);
            Assert.Equal(1.25, loaded.PlaybackRate);
            Assert.Equal("Scope", loaded.VisualizationMode);
            Assert.Equal("D3D11VA", loaded.HardwareDecoding);
            Assert.Equal("Direct3D11", loaded.VideoOutput);
            Assert.Equal("test-device", loaded.PreferredAudioDeviceId);
            Assert.True(loaded.GlobalHotkeysEnabled);
            Assert.False(loaded.SystemMediaKeysEnabled);
            Assert.Equal(500, loaded.LyricOffsetMs);
            Assert.Equal(31, loaded.DesktopLyricsFontSize);
            Assert.Equal(19, loaded.DesktopLyricsTranslationFontSize);
            Assert.True(loaded.DesktopLyricsClickToActivate);
            Assert.Equal("HighContrast", loaded.DesktopLyricsColorScheme);
            Assert.False(loaded.DesktopLyricsShowTranslation);
            var loadedPlaylist = Assert.Single(loaded.Playlists);
            Assert.Equal("自定义歌单", loadedPlaylist.Name);
            Assert.Equal("工作时播放", loadedPlaylist.Description);
            Assert.Equal(@"C:\Artwork\playlist.png", loadedPlaylist.CoverPath);
            Assert.Equal(["工作", "无损"], loadedPlaylist.Tags);
            Assert.Equal("夜间", Assert.Single(Assert.Single(loaded.Tracks).Categories));
            Assert.True(loaded.Tracks[0].IsFavorite);
            Assert.Equal(7, loaded.Tracks[0].PlayCount);
            Assert.Equal(new DateTime(2026, 7, 16, 20, 30, 0, DateTimeKind.Local), loaded.Tracks[0].LastPlayedAt);
            Assert.True(loaded.Tracks[0].HasCloudId("cloud-primary"));
            Assert.True(loaded.Tracks[0].HasCloudId("cloud-alias"));
        }
        finally
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary.Tests"));
            var target = Path.GetFullPath(directory);
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    [Theory]
    [InlineData("movie.mkv", true)]
    [InlineData("movie.webm", true)]
    [InlineData("movie.mp4", true)]
    [InlineData("song.flac", false)]
    [InlineData("song.opus", false)]
    public void MediaExtensions_DistinguishVideoFromAudio(string fileName, bool isVideo)
    {
        var extension = Path.GetExtension(fileName);
        Assert.Contains(extension, MusicLibraryService.SupportedExtensions);
        Assert.Equal(isVideo, MusicLibraryService.VideoExtensions.Contains(extension));
    }

    [Fact]
    public void ShuffleService_LeastPlayedPrioritizesUnheardMedia()
    {
        var tracks = new[]
        {
            Track("a", "Artist A", "Album A", 7),
            Track("b", "Artist B", "Album B", 0),
            Track("c", "Artist C", "Album C", 3)
        };

        var selected = ShuffleService.Choose(tracks, tracks[0], "LeastPlayed", new Random(17));

        Assert.Equal("b", selected.Id);
    }

    [Fact]
    public void ShuffleService_GroupModesAvoidCurrentGroupWhenPossible()
    {
        var tracks = new[]
        {
            Track("a", "Artist A", "Album A", 0),
            Track("b", "Artist A", "Album A", 0),
            Track("c", "Artist B", "Album B", 0)
        };

        Assert.Equal("Album B", ShuffleService.Choose(tracks, tracks[0], "Album", new Random(4)).Album);
        Assert.Equal("Artist B", ShuffleService.Choose(tracks, tracks[0], "Artist", new Random(4)).Artist);
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "Integration")]
    public async Task PlaybackService_RealFilesAdvanceSwitchAndRecover()
    {
        var state = await new AppStore().LoadAsync();
        state.VisualizationMode = "Off";
        state.AudioBackend = "Auto";
        state.PreferredAudioDeviceId = "";
        var tracks = state.Tracks
            .Where(track => !track.IsEncryptedNcm && track.DurationMs > 30_000 && File.Exists(track.FilePath))
            .Take(6)
            .ToList();
        Assert.True(tracks.Count >= 4);

        using var playback = new PlaybackService();
        playback.Volume = 0;
        await Task.Delay(350);

        foreach (var track in tracks)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (_, _) => ready.TrySetResult();
            playback.PlaybackReady += handler;
            try
            {
                playback.Play(track.FilePath, state, track.IsVideo);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally
            {
                playback.PlaybackReady -= handler;
            }

            var before = playback.Time;
            await Task.Delay(1250);
            var after = playback.Time;
            Assert.True(after >= before + 300,
                $"Playback time did not advance for {track.FilePath}: {before} -> {after}");
        }

        var finalTrack = tracks[^1];
        for (var index = 0; index < 10; index++)
            playback.TogglePause();
        await Task.Delay(600);
        var rapidToggleStart = playback.Time;
        await Task.Delay(2000);
        Assert.True(playback.IsPlaying);
        Assert.True(playback.Time >= rapidToggleStart + 200);

        var recovered = await playback.RecoverAsync(finalTrack.FilePath, state, finalTrack.IsVideo, 3000);
        Assert.True(recovered);
        await Task.Delay(1000);
        Assert.True(playback.Time >= 3000);
        playback.Stop();
    }

    private static TrackModel Track(string id, string artist, string album, int playCount) => new()
    {
        Id = id,
        Title = id,
        Artist = artist,
        Album = album,
        PlayCount = playCount
    };

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary.Tests"));
            var target = Path.GetFullPath(directory);
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
