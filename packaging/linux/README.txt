本地音乐库 1.4.0（Linux x64）

程序包含 .NET 运行时，但播放引擎使用系统 libVLC。
Debian/Ubuntu 可先运行：sudo apt install vlc libvlc5
其他发行版请安装本发行版提供的 VLC/libVLC 软件包。

启动：
  chmod +x OfflineMusicLibrary
  ./OfflineMusicLibrary

程序不上传曲库；配置保存在 ~/.local/share/OfflineMusicLibrary。

1.4.0 新增“很久没听”“从未播放”“收藏延伸”和“30 分钟电台”；
陌生歌曲会在列表中显示推荐理由。
顶部“导入播放历史”支持网易云记录 JSON 以及 CSV / TSV，数据仅在本机合并。
