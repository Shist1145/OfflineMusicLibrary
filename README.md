# 本地音乐库 1.4.0

一个以本地文件为准的离线音乐管理与播放工具。Windows 使用完整 WPF 界面；macOS 与 Linux 使用 Avalonia 跨平台界面。当前发行版为 **1.4.0**。

## 主要功能

- 递归扫描音乐文件夹，支持 MP3、FLAC、M4A、OGG、OPUS、WAV、WMA、AAC、APE、NCM 及常见视频容器。
- 即使标签损坏或读取失败，也按文件名收录支持的媒体文件；NCM 会进入曲库，但加密文件仍需转换后才能播放。
- 网易云歌单导入会先取得完整 `trackIds`，再分批补齐歌曲详情，不再只依赖接口中被截断的少量 `tracks`。
- 网易云播放历史可从 JSON、CSV 或 TSV 离线导入；累计次数和最近播放时间会立即参与个性化推荐。
- 本地匹配先使用网易云歌曲 ID，再使用经过 Unicode、特殊符号、feat. 与艺术家信息归一化的标题匹配，并在导入报告中列出真实缺失项。
- 单击播放、收藏、独立专辑页、专辑收藏、社团分类、搜索、封面显示和可点击表头排序。
- 歌曲可通过统一的“添加到…”界面同时加入多个歌单、安排下一首或追加播放队列；歌曲行和右键菜单都可直接操作。
- 歌单支持自定义或自动封面、简介、标签、来源与更新时间；侧栏、歌单详情页和播放队列均显示封面。
- 播放历史按最近播放时间展示，并显示每首歌曲的累计播放次数，数据完全保存在本机。
- “很久没听”“从未播放”“收藏延伸”和“30 分钟电台”会从红心、播放记录以及“喜欢的音乐”类歌单建立本地画像。
- 陌生歌曲必须能与常听艺术家、专辑、社团或标签建立联系，并在列表中显示逐曲推荐理由；没有可靠信号时宁可不推荐。
- 桌面歌词专注模式只在直接点击歌词字形后显示控制栏，未激活时其余区域保持鼠标穿透。
- 本地播放状态、封面和诊断日志保存在用户本机，不依赖在线账号。

## 网易云导入为什么曾经只得到几首

网易云歌单详情接口可能声明数百首并返回完整的 ID 列表，却只在 `tracks` 字段中嵌入少量歌曲。1.2 现在会按 ID 分批请求详情、重试失败批次，并保留未解析 ID；导入结果会分别显示声明数、取得的 ID 数、详情数、精确匹配、模糊匹配与本地缺失数。因此“303 首”不会再静默变成 4 首，也不会为了凑数误配同名改编曲。

## 导入网易云播放历史

Windows 在“网易云导入”菜单选择“导入播放历史…”，macOS / Linux 使用顶部的“导入播放历史”按钮。支持：

- 网易云播放记录接口响应 JSON（含 `allData` 或 `weekData`）；有全量记录时会自动忽略重复的周记录。
- CSV / TSV：歌名列必需，建议同时提供歌手；还可包含歌曲 ID、专辑、播放次数与最近播放时间。

匹配优先使用网易云歌曲 ID；没有 ID 时，只有歌名、艺人或专辑足以确定唯一结果才会写入。同名歧义会留在导入报告中。播放次数按较大的可信值合并，最近播放时间按较新值合并，因此可以安全地重复导入同一份文件。全部处理均在本机完成，不需要把账号 Cookie 交给应用。

## 下载与运行

- Windows：解压 `OfflineMusicLibrary-1.4.0-windows-x64.zip` 后运行 `OfflineMusicLibrary.exe`。
- macOS Apple Silicon：解压 `OfflineMusicLibrary-1.4.0-macos-arm64.zip`，把应用拖入“应用程序”。播放需要系统中安装 VLC.app。
- macOS Intel：使用 `OfflineMusicLibrary-1.4.0-macos-x64.zip`；发行包包含 Intel LibVLC。
- Linux x64：解压 `OfflineMusicLibrary-1.4.0-linux-x64.tar.gz`，安装发行版提供的 VLC/libVLC，然后运行 `./OfflineMusicLibrary`。

macOS 包采用临时签名而非 Apple 公证；首次启动可在 Finder 中右键应用并选择“打开”。Linux 的桌面入口模板位于 `packaging/linux`。

## 从源码构建

需要 .NET 10 SDK。

```powershell
# Windows 完整版
dotnet publish OfflineMusicLibrary.csproj -c Release -r win-x64 --self-contained true

# 跨平台版示例
dotnet publish CrossPlatform/OfflineMusicLibrary.CrossPlatform.csproj -c Release -r linux-x64 --self-contained true
```

确定性测试不会读取个人曲库或访问网易云网络：

```powershell
dotnet test release/OfflineMusicLibrary.Tests/OfflineMusicLibrary.Tests.csproj -c Release -r win-x64 --filter "Category!=Integration"
```

发行资产提供 Windows x64、Linux x64、macOS Intel 和 macOS Apple Silicon 四种包。
