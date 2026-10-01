using Avalonia.Platform;

namespace OsmoOverlay.Gui;

/// <summary>
///     Picks a monitor for the full screen preview. Avalonia gives a screen no stable id, so one is identified by where it
///     sits on the desktop and how big it is - which only changes when the user rearranges their displays, and then the
///     preview falls back to the main window's own screen.
/// </summary>
internal static class MonitorChoice
{
	public static string KeyOf(Screen screen)
	{
		return $"{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height}";
	}

	public static Screen? Find(IReadOnlyList<Screen> screens, string? key)
	{
		return key is null ? null : screens.FirstOrDefault(s => KeyOf(s) == key);
	}

	public static string Describe(Screen screen, int index)
	{
		return $"Display {index + 1} - {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.IsPrimary ? " (primary)" : "")}";
	}
}
