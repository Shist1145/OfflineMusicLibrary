# 本地音乐库 1.2 / OfflineMusicLibrary 1.2

一个以本地文件为准的离线音乐管理与播放工具。Windows 使用完整 WPF 界面；macOS 与 Linux 使用 Avalonia 跨平台界面。产品版本目前固定为 **1.2**。
An offline music management and playback tool that uses local files. Windows uses a full WPF interface; macOS and Linux use the Avalonia cross-platform interface. The current product version is fixed at **1.2**.

## 主要功能 / Main functions

- 递归扫描音乐文件夹，支持 MP3、FLAC、M4A、OGG、OPUS、WAV、WMA、AAC、APE、NCM 及常见视频容器。
- Recursively scans music folders, supporting MP3, FLAC, M4A, OGG, OPUS, WAV, WMA, AAC, APE, NCM, and common video containers.
- 即使标签损坏或读取失败，也按文件名收录支持的媒体文件；NCM 会进入曲库，但加密文件仍需转换后才能播放。
- Even if tags are damaged or reading fails, supported media files are still included by filename; NCM files are added to the library, but encrypted files still need to be converted before playback.
- 网易云歌单导入会先取得完整 `trackIds`，再分批补齐歌曲详情，不再只依赖接口中被截断的少量 `tracks`。
- Importing NetEase Cloud Music playlists first retrieves complete `trackIds`, then fills in song details in batches, no longer relying solely on the truncated `tracks` from the API.
- 本地匹配先使用网易云歌曲 ID，再使用经过 Unicode、特殊符号、feat. 与艺术家信息归一化的标题匹配，并在导入报告中列出真实缺失项。
- Local matching first uses the NetEase Cloud Music song ID, then uses a title match normalized with Unicode, special characters, features, and artist information, listing the actual missing items in the import report.
- 单击播放、收藏、独立专辑页、专辑收藏、社团分类、搜索、封面显示和可点击表头排序。
- Single-click play, favorites, individual album pages, album collections, community categories, search, album art display, and clickable header sorting.
- 歌曲可通过统一的“添加到…”界面同时加入多个歌单、安排下一首或追加播放队列；歌曲行和右键菜单都可直接操作。
- Songs can be added to multiple playlists simultaneously, scheduled as the next song, or added to the playback queue through a unified "Add to..." interface; song rows and right-click menus offer direct operation.
- 歌单支持自定义或自动封面、简介、标签、来源与更新时间；侧栏、歌单详情页和播放队列均显示封面。
- Playlists support custom or automatic album art, descriptions, tags, sources, and update times; album art is displayed in the sidebar, playlist details page, and playback queue.
- 播放历史按最近播放时间展示，并显示每首歌曲的累计播放次数，数据完全保存在本机。
- Playback history is displayed by most recent playback time, showing the cumulative number of plays for each song; data is entirely stored locally.
- 桌面歌词专注模式只在直接点击歌词字形后显示控制栏，未激活时其余区域保持鼠标穿透。
- Desktop lyrics focus mode only displays the control bar when the lyrics are clicked; when inactive, the mouse hovers over the rest of the screen.
- 本地播放状态、封面和诊断日志保存在用户本机，不依赖在线账号。
- Local playback status, album art, and diagnostic logs are stored on the user's device, independent of online accounts.

## 下载与运行

- Windows：解压 `OfflineMusicLibrary-1.2-windows-x64.zip` 后运行 `OfflineMusicLibrary.exe`。
- Windows: Unzip `OfflineMusicLibrary-1.2-windows-x64.zip` and run `OfflineMusicLibrary.exe`.
- macOS Apple Silicon：解压 `OfflineMusicLibrary-1.2-macos-arm64.zip`，把应用拖入“应用程序”。播放需要系统中安装 VLC.app。
- macOS Apple Silicon: Unzip `OfflineMusicLibrary-1.2-macos-arm64.zip` and drag the application into Applications. Playback requires VLC.app to be installed on your system.
- macOS Intel：使用 `OfflineMusicLibrary-1.2-macos-x64.zip`；发行包包含 Intel LibVLC。
- macOS Intel: Use `OfflineMusicLibrary-1.2-macos-x64.zip`; the distribution includes Intel LibVLC.
- Linux x64：解压 `OfflineMusicLibrary-1.2-linux-x64.tar.gz`，安装发行版提供的 VLC/libVLC，然后运行 `./OfflineMusicLibrary`。
- Linux x64: Unzip `OfflineMusicLibrary-1.2-linux-x64.tar.gz`, install the VLC/libVLC provided by your distribution, and then run `./OfflineMusicLibrary`.

macOS 包采用临时签名而非 Apple 公证；首次启动可在 Finder 中右键应用并选择“打开”。Linux 的桌面入口模板位于 `packaging/linux`。
The macOS package uses a temporary signature, not Apple's official signature; upon first launch, right-click the application in Finder and select "Open". The desktop entry template for Linux is located in `packaging/linux`.

## 从源码构建 / Build from source code

需要 .NET 10 SDK。

```powershell
# Windows 完整版 / Windows Full Version
dotnet publish OfflineMusicLibrary.csproj -c Release -r win-x64 --self-contained true

# 跨平台版示例 / Cross-platform version example
dotnet publish CrossPlatform/OfflineMusicLibrary.CrossPlatform.csproj -c Release -r linux-x64 --self-contained true
```

确定性测试不会读取个人曲库或访问网易云网络：
Deterministic tests will not read personal music libraries or access the NetEase Cloud Music network.

```powershell
dotnet test release/OfflineMusicLibrary.Tests/OfflineMusicLibrary.Tests.csproj -c Release -r win-x64 --filter "Category!=Integration"
```

GitHub Actions 会在 Windows、Linux、Apple Silicon Mac 和 Intel Mac 原生运行器上生成 1.2 安装包；推送 `v1.2` 或 `v1.2.0` 标签时会自动建立发行版。
GitHub Actions will generate a 1.2 installer on native Windows, Linux, Apple Silicon Mac, and Intel Mac runtimes; the release will be automatically created when you push the `v1.2` or `v1.2.0` tag.
