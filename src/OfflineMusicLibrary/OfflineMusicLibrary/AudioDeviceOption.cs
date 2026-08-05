namespace OfflineMusicLibrary;

public sealed record AudioDeviceOption(string Id, string Name)
{
	public override string ToString()
	{
		return Name;
	}
}
