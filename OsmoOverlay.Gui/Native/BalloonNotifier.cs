using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace OsmoOverlay.Gui.Native;

/// <summary>
///     Shows a classic Windows notification-area balloon tip via Shell_NotifyIcon - used only when the
///     app window isn't focused (see MainWindow's render-completion handling), since a focused window
///     shows its own ConfirmDialog instead. No persistent tray icon is left behind: one is added just
///     long enough to display the balloon, then removed a few seconds later.
/// </summary>
internal static class BalloonNotifier
{
	private const int IconId = 1;

	private const int NimAdd = 0x00000000;
	private const int NimModify = 0x00000001;
	private const int NimDelete = 0x00000002;
	private const int NifIcon = 0x00000002;
	private const int NifTip = 0x00000004;
	private const int NifInfo = 0x00000010;
	private const int NiifInfo = 0x00000001;
	private const int IdiInformation = 32516;

	public static void Show(Window window, string title, string message)
	{
		if (!OperatingSystem.IsWindows()) return;

		try
		{
			var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
			if (hwnd == IntPtr.Zero) return;

			var data = new NotifyIconData
			{
				cbSize = Marshal.SizeOf<NotifyIconData>(),
				hWnd = hwnd,
				uID = IconId,
				uFlags = NifIcon | NifTip | NifInfo,
				hIcon = LoadIconW(IntPtr.Zero, IdiInformation),
				szTip = "OsmoOverlay",
				szInfo = message,
				szInfoTitle = title,
				dwInfoFlags = NiifInfo
			};

			// NIM_ADD can fail harmlessly if an icon with this uID is already registered (e.g. a
			// previous notification's removal is still pending) - NIM_MODIFY below is what actually
			// needs to succeed to show the balloon.
			Shell_NotifyIcon(NimAdd, ref data);
			Shell_NotifyIcon(NimModify, ref data);

			_ = RemoveAfterDelay(hwnd);
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
		}
	}

	private static async Task RemoveAfterDelay(IntPtr hwnd)
	{
		await Task.Delay(TimeSpan.FromSeconds(8));

		var data = new NotifyIconData
		{
			cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = IconId,
			szTip = "", szInfo = "", szInfoTitle = ""
		};
		Shell_NotifyIcon(NimDelete, ref data);
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr LoadIconW(IntPtr hInstance, int lpIconName);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public int cbSize;
		public IntPtr hWnd;
		public int uID;
		public int uFlags;
		public int uCallbackMessage;
		public IntPtr hIcon;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string szTip;

		public int dwState;
		public int dwStateMask;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string szInfo;

		public int uTimeoutOrVersion;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string szInfoTitle;

		public int dwInfoFlags;
	}
}
