namespace OfflineMusicLibrary;

public sealed class SidebarNavigationItem
{
	public SidebarNavigationKind Kind { get; init; }

	public string Title { get; init; } = "";

	public string Icon { get; init; } = "";

	public string NavigationKey { get; init; } = "";

	public string CountText { get; init; } = "";

	public string ExpansionGlyph { get; init; } = "";

	public string? Category { get; init; }

	public PlaylistModel? Playlist { get; init; }

	public bool IsHeader
	{
		get
		{
			switch (Kind)
			{
			case SidebarNavigationKind.LibraryHeader:
			case SidebarNavigationKind.CategoryHeader:
			case SidebarNavigationKind.PlaylistHeader:
				return true;
			default:
				return false;
			}
		}
	}
}
