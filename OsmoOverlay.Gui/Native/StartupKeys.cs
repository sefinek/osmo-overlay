using System.Runtime.InteropServices;

namespace OsmoOverlay.Gui.Native;

/// <summary>Keys held while the app was starting. Windows only: other systems have no key state to read before a window exists.</summary>
internal static class StartupKeys
{
	private const int VkShift = 0x10;

	public static bool ShiftHeld { get; private set; }

	public static void Capture()
	{
		ShiftHeld = OperatingSystem.IsWindows() && (GetAsyncKeyState(VkShift) & 0x8000) != 0;
	}

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int key);
}
