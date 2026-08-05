namespace OfflineMusicLibrary;

public sealed record ScanProgress(int Scanned, int Total, string CurrentFile, int Errors);
