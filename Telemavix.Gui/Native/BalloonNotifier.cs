using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Telemavix.Gui.Native;

/// <summary>
///     Shows a classic Windows notification-area balloon tip via Shell_NotifyIcon - used only when the
///     app window isn't focused (see MainWindow's render-completion handling), since a focused window
///     shows its own ConfirmDialog instead. No persistent tray icon is left behind: one is added just
///     long enough to display the balloon, then removed a few seconds later.
/// </summary>
internal static partial class BalloonNotifier
{
	private const int IconId = 1;

	private const int NimAdd = 0x00000000;
	private const int NimModify = 0x00000001;
	private const int NimDelete = 0x00000002;
	private const int NifIcon = 0x00000002;
	private const int NifTip = 0x00000004;
	private const int NifInfo = 0x00000010;
	private const int NiifInfo = 0x00000001;
	private const int NiifNoSound = 0x00000010;
	private const int IdiInformation = 32516;

	public static void Show(Window window, string title, string message)
	{
		if (!OperatingSystem.IsWindows()) return;

		try
		{
			IntPtr hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
			if (hwnd == IntPtr.Zero) return;

			var data = new NotifyIconData
			{
				cbSize = Unsafe.SizeOf<NotifyIconData>(),
				hWnd = hwnd,
				uID = IconId,
				uFlags = NifIcon | NifTip | NifInfo,
				hIcon = LoadIconW(IntPtr.Zero, IdiInformation),
				dwInfoFlags = NiifInfo | NiifNoSound
			};
			Copy("Telemavix Studio", data.szTip);
			Copy(message, data.szInfo);
			Copy(title, data.szInfoTitle);

			// NIM_ADD can fail harmlessly if an icon with this uID is already registered (e.g. a
			// previous notification's removal is still pending) - NIM_MODIFY below is what actually
			// needs to succeed to show the balloon.
			Notify(NimAdd, ref data);
			Notify(NimModify, ref data);

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
			cbSize = Unsafe.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = IconId
		};
		Notify(NimDelete, ref data);
	}

	private static void Copy(string text, Span<char> destination)
	{
		destination.Clear();
		text.AsSpan(0, Math.Min(text.Length, destination.Length - 1)).CopyTo(destination);
	}

	private static unsafe void Notify(int message, ref NotifyIconData data)
	{
		fixed (NotifyIconData* pointer = &data)
		{
			Shell_NotifyIcon(message, pointer);
		}
	}

	[LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool Shell_NotifyIcon(int dwMessage, NotifyIconData* lpData);

	[LibraryImport("user32.dll")]
	private static partial IntPtr LoadIconW(IntPtr hInstance, int lpIconName);

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public int cbSize;
		public IntPtr hWnd;
		public int uID;
		public int uFlags;
		public int uCallbackMessage;
		public IntPtr hIcon;

		public Text128 szTip;

		public int dwState;
		public int dwStateMask;

		public Text256 szInfo;

		public int uTimeoutOrVersion;

		public Text64 szInfoTitle;

		public int dwInfoFlags;
	}

	[InlineArray(64)]
	private struct Text64
	{
		private char _element;
	}

	[InlineArray(128)]
	private struct Text128
	{
		private char _element;
	}

	[InlineArray(256)]
	private struct Text256
	{
		private char _element;
	}
}
