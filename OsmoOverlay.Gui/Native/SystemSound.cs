using System.Runtime.InteropServices;

namespace OsmoOverlay.Gui.Native;

/// <summary>
///     Plays the OS's own "notification" system sound (whatever the user picked for it in Windows
///     sound settings) via MessageBeep, rather than shipping/loading an audio asset - a render
///     finishing is exactly the kind of event that sound is for. No-ops on any failure or off Windows,
///     same as TaskbarProgress/BalloonNotifier - this is a cosmetic touch, never worth failing over.
/// </summary>
internal static class SystemSound
{
	private const uint MbIconAsterisk = 0x00000040;

	public static void PlayNotification()
	{
		if (!OperatingSystem.IsWindows()) return;

		try
		{
			MessageBeep(MbIconAsterisk);
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
		}
	}

	[DllImport("user32.dll")]
	private static extern bool MessageBeep(uint uType);
}
