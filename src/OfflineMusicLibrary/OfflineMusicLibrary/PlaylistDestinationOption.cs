namespace OfflineMusicLibrary;

public sealed class PlaylistDestinationOption(PlaylistModel playlist)
{
	public PlaylistModel Playlist { get; } = playlist;

	public bool IsSelected { get; set; }

	public string SearchText => $"{Playlist.Name} {Playlist.Description} {string.Join(' ', Playlist.Tags)}";
}
