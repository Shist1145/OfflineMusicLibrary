namespace OfflineMusicLibrary;

public sealed class MediaDetails
{
	public uint Width { get; init; }

	public uint Height { get; init; }

	public double FrameRate { get; init; }

	public uint SampleRate { get; init; }

	public uint Channels { get; init; }

	public int AudioBitrate { get; init; }

	public int VideoBitrate { get; init; }

	public string AudioCodec { get; init; } = "-";

	public string VideoCodec { get; init; } = "-";

	public bool HasAudio { get; init; }

	public bool HasVideo { get; init; }
}
