using OfflineMusicLibrary;

static void Require(bool condition, string message)
{
	if (!condition)
	{
		throw new InvalidOperationException(message);
	}
}

string temporaryDirectory = Path.Combine(Path.GetTempPath(), "OfflineMusicLibrary-LyricsChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
	string audioPath = Path.Combine(temporaryDirectory, "sample.mp3");
	string lyricPath = Path.ChangeExtension(audioPath, ".lrc");
	await File.WriteAllTextAsync(lyricPath,
		"[ar:Dandelion Trio]\n" +
		"[00:47.26]懐かしい童謡\n" +
		"[00:47.26]na tsu ka shi i do u yo u\n" +
		"[00:47.26]令人怀念 隔世童谣\n" +
		"[00:55.58]—聞こえた—\n" +
		"[00:55.58]—ki ko e ta—\n" +
		"[00:55.58]—听见了—\n");

	List<LyricLine> lines = LyricsService.LoadForTrack(audioPath);
	Require(lines.Count == 2, "三语 LRC 应合并为两个时间点。");
	Require(lines[0].Original == "懐かしい童謡", "原文识别错误。");
	Require(lines[0].Romanization == "na tsu ka shi i do u yo u", "音译识别错误。");
	Require(lines[0].Translation == "令人怀念 隔世童谣", "中文翻译识别错误。");

	LyricsDisplayContent defaultDisplay = LyricsDisplayService.Resolve(lines[0], LyricsDisplayModes.OriginalTranslation);
	Require(defaultDisplay.Primary == lines[0].Original && defaultDisplay.Secondary == lines[0].Translation,
		"默认模式应为原文 + 中文。");
	Require(defaultDisplay.PrimaryKind == LyricTextKind.Original && defaultDisplay.SecondaryKind == LyricTextKind.Translation,
		"默认模式应保留原文与翻译的语义类型。");
	LyricsDisplayContent allDisplay = LyricsDisplayService.Resolve(lines[0], LyricsDisplayModes.All);
	Require(allDisplay.Primary == lines[0].Original &&
		allDisplay.Secondary == lines[0].Romanization &&
		allDisplay.Tertiary == lines[0].Translation,
		"三语模式顺序应为原文、音译、中文。");
	Require(allDisplay.PrimaryKind == LyricTextKind.Original &&
		allDisplay.SecondaryKind == LyricTextKind.Romanization &&
		allDisplay.TertiaryKind == LyricTextKind.Translation,
		"三语模式的独立配色语义应保持正确。");
	LyricsDisplayContent romanizationTranslation = LyricsDisplayService.Resolve(lines[0], LyricsDisplayModes.RomanizationTranslation);
	Require(romanizationTranslation.PrimaryKind == LyricTextKind.Romanization &&
		romanizationTranslation.SecondaryKind == LyricTextKind.Translation,
		"音译 + 中文模式不应因行位置变化而串色。");

	AppState customStyle = new()
	{
		DesktopLyricsColorScheme = "Custom",
		DesktopLyricsPrimaryColor = "#112233",
		DesktopLyricsSecondaryColor = "#223344",
		DesktopLyricsRomanizationColor = "#334455",
		DesktopLyricsTranslationColor = "#445566",
		DesktopLyricsStrokeColor = "#556677"
	};
	LyricsPalette palette = LyricsStyleService.ResolvePalette(customStyle);
	Require(palette.Original.ToString() == "#FF112233" &&
		palette.Romanization.ToString() == "#FF334455" &&
		palette.Translation.ToString() == "#FF445566" &&
		palette.Stroke.ToString() == "#FF556677",
		"自定义原文、音译、翻译和描边颜色应分别生效。");

	await File.WriteAllTextAsync(lyricPath, "[00:01.00]さくら\n[00:01.00]樱花\n");
	lines = LyricsService.LoadForTrack(audioPath);
	Require(lines[0].Romanization == "" && lines[0].Translation == "樱花", "旧式原文 + 中文歌词应保持兼容。");

	await File.WriteAllTextAsync(lyricPath, "[00:01.00]さくら\n[00:01.00]sa ku ra\n");
	lines = LyricsService.LoadForTrack(audioPath);
	Require(lines[0].Romanization == "sa ku ra" && lines[0].Translation == "", "原文 + 音译歌词应正确分类。");

	await File.WriteAllTextAsync(lyricPath, "[00:01.00]さくら\n[00:01.00]sa ku ra\n[00:01.00]内嵌翻译\n");
	await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "sample.zh.lrc"), "[00:01.10]独立翻译\n");
	lines = LyricsService.LoadForTrack(audioPath);
	Require(lines[0].Romanization == "sa ku ra" && lines[0].Translation == "独立翻译",
		"独立翻译文件应覆盖内嵌翻译，同时保留音译。");

	if (args.Length > 0 && File.Exists(args[0]))
	{
		string suppliedAudioPath = Path.ChangeExtension(args[0], ".mp3");
		List<LyricLine> suppliedLines = LyricsService.LoadForTrack(suppliedAudioPath);
		LyricLine? sample = suppliedLines.FirstOrDefault(line => line.TimeMs == 47260);
		Require(sample != null, "用户提供的歌词中缺少 00:47.26 样本。");
		Require(sample.Original == "懐かしい童謡", "用户歌词的原文解析错误。");
		Require(sample.Romanization == "na tsu ka shi i do u yo u", "用户歌词的音译解析错误。");
		Require(sample.Translation == "令人怀念 隔世童谣", "用户歌词的中文解析错误。");
	}

	Console.WriteLine("Lyrics checks passed.");
}
finally
{
	Directory.Delete(temporaryDirectory, recursive: true);
}
