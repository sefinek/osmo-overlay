using System.Runtime.InteropServices;

namespace Telemavix.Gui.Native;

/// <summary>Keys held while the app was starting. Windows only: other systems have no key state to read before a window exists.</summary>
internal static partial class StartupKeys
{
	private const int VkShift = 0x10;

	public static bool ShiftHeld { get; private set; }

	public static void Capture()
	{
		ShiftHeld = OperatingSystem.IsWindows() && (GetAsyncKeyState(VkShift) & 0x8000) != 0;
	}

	[LibraryImport("user32.dll")]
	private static partial short GetAsyncKeyState(int key);
}
