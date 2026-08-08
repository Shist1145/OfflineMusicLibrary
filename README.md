<p align="center">
  <img src="src/OfflineMusicLibrary/Assets/OfflineMusicLibrary-logo.png" width="128" alt="OfflineMusicLibrary logo" />
</p>

# 本地音乐库 1.6.2 / OfflineMusicLibrary 1.6.2

[![Build and regression checks](https://github.com/Shist1145/OfflineMusicLibrary/actions/workflows/release.yml/badge.svg)](https://github.com/Shist1145/OfflineMusicLibrary/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/Shist1145/OfflineMusicLibrary?display_name=tag)](https://github.com/Shist1145/OfflineMusicLibrary/releases/latest)

把音乐留在本地，把控制权还给你。

OfflineMusicLibrary 是一个以本地文件为准的离线音乐管理与播放工具。曲库索引、歌单、收藏、播放记录、歌词样式和播放器设置都保存在本机；应用不会要求登录音乐账号，也不会把个人曲库上传到服务器。

> **1.6.2 是 Windows x64 完整版。** 已发布的 Linux/macOS 安装包仍是 [v1.4.0 跨平台预览版](https://github.com/Shist1145/OfflineMusicLibrary/releases/tag/v1.4.0)。跨平台源码已同步本次网易云大歌单与 Off Vocal 匹配修复，但界面和功能仍未与 Windows 完整版完全一致。

OfflineMusicLibrary is a local-first music manager and player. Version 1.6.2 is the full Windows x64 release. The published Linux/macOS packages remain v1.4.0 previews, although their source now shares the safer NetEase playlist matching rules.

## 1.6.2 解决了什么

这次更新以“导入成功率”和“不要因为一次异常破坏已有数据”为核心。

- **大型网易云歌单不再轻易少歌。** 100 首歌曲详情整批返回空时，会自动拆成 25 首小批次重试；即使详情接口暂时不完整，也会保留完整歌曲 ID，等待下一次继续补全。
- **普通版与无人声版严格隔离。** `Off Vocal`、`Instrumental`、`伴奏`、`純音樂`、`無主唱`、`Backing Track`、日文オフボーカル等标记不会再被当作可以忽略的普通标题噪声。
- **不再由前面的模糊歌曲抢走后面的唯一文件。** 匹配改为全局一对一分配，并综合歌名、艺人、专辑、时长、可播放格式和已有云 ID。
- **会修正历史错误云 ID。** 如果过去把普通版 ID 记到了伴奏文件上，新导入会把错误关联移走并写入正确文件，导入报告会显示修正数量。
- **导入前曲库同步改为增量扫描。** 未变化文件直接复用已保存元数据；新文件和修改过的文件才读取标签、封面和歌词。老版本曲库会先补齐文件戳，而不是强迫 5,000 首歌曲全部重读一遍。
- **扫描可以取消。** 扫描时刷新按钮会变为取消按钮；已进入的单个 TagLib 同步读取需要等该次读取返回，但取消后不会保存半成品扫描结果。
- **阻止播放器双开。** 第二个实例会在创建播放引擎、热键和音频资源前退出，避免两个内存状态最后互相覆盖歌单、播放次数与设置。
- **状态备份真正保留上一代。** `backup` 不再是刚写完主文件的镜像，而是上一代有效状态；另有 `previous` 保存上上代。序列化、落盘、JSON 校验或替换失败会向调用方报告，不再悄悄显示成功。

完整变更见 [CHANGELOG.md](CHANGELOG.md)，网易云导入机制见 [docs/NETEASE_PLAYLIST_IMPORT.md](docs/NETEASE_PLAYLIST_IMPORT.md)，数据保护说明见 [docs/DATA_SAFETY.md](docs/DATA_SAFETY.md)。

## 主要能力

| 范围 | 能力 |
| --- | --- |
| 本地曲库 | 扫描 FLAC、MP3、M4A、OGG、OPUS、WAV、WMA、AAC、APE、NCM，以及常见本地视频格式；按歌曲、专辑、社团和分类整理 |
| 播放体验 | 标准、黑胶、沉浸歌词三种播放页；播放队列、随机模式、相似歌曲、迷你播放器、任务栏控制与全局热键 |
| 歌词 | 原文、音译、翻译三轨显示；桌面歌词；独立颜色、透明度、字号、描边颜色、描边比例和原文渐变 |
| 网易云迁移 | 导入公开歌单和离线导出的播放历史；优先匹配本地可播放文件，不下载歌曲，也不替代本地文件管理 |
| 音频 | EQ 预设、空间效果、播放速度、音频后端、硬件解码、视频输出和缓存配置 |
| 稳定性 | 安全播放模式、播放停滞看门狗、有限次数自动恢复、单实例保护、增量扫描与多代状态恢复 |
| 隐私 | 不要求账号登录；状态默认仅写入 `%LOCALAPPDATA%\OfflineMusicLibrary`；媒体文件保持在用户指定目录 |

### 关于 NCM

应用可以索引 `.ncm` 文件并在匹配时保留其身份，但会优先选择同曲目的普通可播放文件。OfflineMusicLibrary 不承诺解密受保护的 NCM 内容，也不会从网易云下载缺失歌曲。

## 网易云歌单导入

在主界面选择“网易云 → 导入歌单”，粘贴公开歌单链接或纯数字歌单 ID。导入流程会：

1. 快速同步本地音乐文件夹，把新增歌曲加入索引。
2. 读取歌单声明数量、完整歌曲 ID 和可取得的歌曲详情。
3. 对临时失败的歌曲详情进行重试，并保留仍未解析的 ID。
4. 先锁定可信云 ID，再进行全局一对一本地匹配。
5. 只有远端 ID 与歌曲详情足够完整时，才允许清理旧歌单中已确认消失的内容。

“网易云有 700 首但本地只导入 554 首”并不总是同一个原因。1.6.2 修复了批次空响应和错误抢占造成的少导入；剩余未匹配项通常属于本地确实缺少文件、标题/艺人信息不足、同一文件对应多个网易云版本，或远端详情暂时没有返回。完成窗口会把这些情况分开统计。

## 歌词与播放页面

- 原文、音译、翻译可独立设置颜色与透明度。
- 描边颜色和描边比例可单独调整，不再只能跟随主文字颜色。
- 支持原文、音译、翻译的单轨、双轨和三轨组合。
- 桌面歌词与沉浸播放页使用同一套样式规则，并兼容旧版配置。
- 播放页支持标准、黑胶和歌词布局；返回曲库时会恢复正常导航状态。
- 同名 `.lrc` 可识别同时间轴内的音译/翻译，也支持 `.zh.lrc`、`.cn.lrc`、`.trans.lrc`、`.translated.lrc` 和 `.tlrc` 翻译旁路文件。

## 下载与安装

1. 打开 [GitHub Releases](https://github.com/Shist1145/OfflineMusicLibrary/releases)。
2. 下载 `OfflineMusicLibrary-1.6.2-windows-x64.zip`。
3. 完整解压到一个可写目录，不要直接在压缩包内运行。
4. 运行 `OfflineMusicLibrary.exe`。

Windows 包是自包含 x64 版本，不要求另行安装 .NET。LibVLC 运行库和插件必须与 `OfflineMusicLibrary.exe` 保持在同一个解压目录中。

### 从旧版本更新

- 退出旧版后，解压 1.6.2 到新的程序目录。
- 不需要复制 `%LOCALAPPDATA%\OfflineMusicLibrary`；新版本会继续读取原状态。
- 建议保留旧程序目录几天，确认新版本正常后再手动删除。
- 不要删除 `library-v2.json` 来解决界面问题；先查看日志和备份，必要时按 [数据恢复说明](docs/DATA_SAFETY.md) 操作。

## 数据保存与恢复

Windows 完整版默认使用：

```text
%LOCALAPPDATA%\OfflineMusicLibrary\
├─ library-v2.json           最新有效状态
├─ library-v2.backup.json    上一代有效状态
├─ library-v2.previous.json  上上代有效状态
├─ library-v2.write.lock     跨保存方写入锁
├─ playlist-artwork\         自定义歌单封面
└─ logs\                     诊断日志
```

扫描不会把暂时离线的移动硬盘或无法访问的子目录直接当作“歌曲已删除”。只有已确认可访问的目录中确实不存在的文件，才会从本次索引中移除。取消扫描、枚举失败或元数据读取异常不会授权写入一个半成品空曲库。

## 从源码构建

Windows 完整版需要 .NET 10 SDK 和 Windows x64：

```powershell
dotnet restore src/OfflineMusicLibrary/OfflineMusicLibrary.csproj
dotnet build src/OfflineMusicLibrary/OfflineMusicLibrary.csproj -c Release --no-restore

$projects = Get-ChildItem tests -Recurse -Filter *.csproj | Sort-Object FullName
foreach ($project in $projects) {
    dotnet run --project $project.FullName -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet publish src/OfflineMusicLibrary/OfflineMusicLibrary.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o artifacts/windows
```

跨平台预览源码可以单独构建：

```powershell
dotnet build CrossPlatform/OfflineMusicLibrary.CrossPlatform.csproj -c Release
```

确定性回归检查使用临时目录和模拟网易云响应，不读取或改写个人曲库，也不要求真实网络。测试覆盖：

- 专辑身份与社团整理
- 增量扫描、离线根目录保留、修改文件刷新与取消
- 三轨歌词解析与样式
- 迷你播放器和播放页状态
- Windows 与跨平台网易云 700 首批次重试、Off Vocal 隔离和全局匹配
- 歌单安全同步与维护
- 推荐和播放历史导入
- 单实例、播放恢复、状态轮换、损坏恢复和保存失败传播

## 仓库结构

```text
src/OfflineMusicLibrary/     Windows WPF 完整版
CrossPlatform/               Avalonia 跨平台预览源码
tests/                       无个人数据的确定性回归检查
docs/                        导入与数据安全说明
packaging/                   Linux/macOS 预览版打包资料
.github/workflows/           构建、测试、发布和 SHA-256 工作流
```

## 当前边界

- 这是本地播放器与迁移工具，不是网易云客户端，也不提供在线流媒体播放或歌曲下载。
- 网易云公开接口可能临时限流或不返回部分详情；1.6.2 会保留 ID 和旧歌单内容，但无法凭空补出本地没有的媒体文件。
- Linux/macOS 已发布包仍是 1.4.0 预览版，缺少部分 Windows 播放页、桌面集成、歌词样式和稳定性设置。
- 自动测试可以证明匹配、保存和状态迁移规则，但不能替代所有显卡、音频设备和桌面环境上的人工播放验收。

## 反馈问题

请在 [GitHub Issues](https://github.com/Shist1145/OfflineMusicLibrary/issues) 中说明：

- 应用版本和 Windows 版本
- 问题发生前执行的操作
- 是否使用移动硬盘、NCM、视频或特殊音频后端
- 导入报告中的声明数量、详情数量、匹配数量和未匹配数量
- 对应时间段的诊断日志

请不要上传完整 `library-v2.json`、真实媒体文件、私人歌单链接或包含个人路径的未经处理日志。
