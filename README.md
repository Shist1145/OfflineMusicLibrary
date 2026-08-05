# 本地音乐库 1.6.1 / OfflineMusicLibrary 1.6.1

一个以本地文件为准的离线音乐管理与播放工具：音乐、曲库索引、播放记录、歌词样式与设置都留在本机。

**1.6.1 是 Windows x64 完整版。** 原有 Linux 与 macOS 跨平台预览版仍可从 [v1.4.0](https://github.com/Shist1145/OfflineMusicLibrary/releases/tag/v1.4.0) 下载；它们暂不包含本页列出的全部 Windows 新功能。

OfflineMusicLibrary is a local-first music manager and player. Version 1.6.1 is the full Windows x64 release; the Linux/macOS preview remains available in v1.4.0.

## 1.6.1 重点更新

- 沉浸式播放页提供标准、黑胶与歌词三种布局，包含大封面、唱臂进度、本地歌曲资料和相似歌曲建议，并可可靠返回曲库。
- 原文、音译与翻译可独立设置颜色、透明度和显示组合；描边颜色与粗细也可调节，桌面歌词和播放页共用一致的样式规则。
- 加入安全播放模式、播放停滞看门狗、有限次数自动恢复、恢复宽限期，以及音频后端、硬件解码、视频输出和缓存档案。
- 应用状态写入采用备份与恢复路径；音乐根目录暂时不可用时保留已有曲库和歌单，不把离线磁盘误判为已删除内容。
- 增强迷你播放器、任务栏播放控制、主题、播放队列、歌单维护和诊断日志。
- 保留网易云歌单与播放历史离线导入、本地推荐、专辑/社团整理、封面、EQ 与空间音效。

## 歌词能力

- 支持原文、音译、翻译三轨歌词，以及原文+翻译、原文+音译、音译+翻译、全部显示和单轨显示。
- 支持主文字、次文字、音译、翻译、描边五种独立颜色。
- 支持原文渐变、分轨透明度、字号与描边比例，并对旧设置自动补齐兼容默认值。
- 可从同名 `.lrc` 的同一时间轴中区分原文、音译与内嵌翻译，并识别 `.zh.lrc`、`.trans.lrc` 等翻译旁路文件。

## 下载与运行

从 [Releases](https://github.com/Shist1145/OfflineMusicLibrary/releases) 下载 `OfflineMusicLibrary-1.6.1-windows-x64.zip`，解压后运行 `OfflineMusicLibrary.exe`。这是自包含版本，不要求另行安装 .NET；首次启动不会上传曲库或账号信息。

## 从源码构建

需要 .NET 10 SDK 与 Windows x64。

```powershell
dotnet build src/OfflineMusicLibrary/OfflineMusicLibrary.csproj -c Release

$projects = Get-ChildItem tests -Recurse -Filter *.csproj
foreach ($project in $projects) {
    dotnet run --project $project.FullName -c Release
}

dotnet publish src/OfflineMusicLibrary/OfflineMusicLibrary.csproj `
    -c Release -r win-x64 --self-contained true -o artifacts/windows
```

回归检查不读取真实个人曲库，也不访问网易云网络，覆盖曲库离线保留、歌词解析与样式、专辑身份、迷你播放器、歌单维护、推荐、播放稳定性和状态恢复。

## 源码结构

- `src/OfflineMusicLibrary`：Windows WPF 完整版。
- `tests`：无需个人数据的确定性回归检查。
- `CrossPlatform`：保留的 1.4.0 Avalonia 跨平台预览源码。
- `packaging`：Linux 与 macOS 预览版打包资料。
