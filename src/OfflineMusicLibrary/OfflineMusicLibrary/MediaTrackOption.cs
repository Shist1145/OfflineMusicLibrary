namespace OfflineMusicLibrary;

public sealed record MediaTrackOption(int Id, string Name)
{
	public override string ToString()
	{
		return Name;
	}
}
