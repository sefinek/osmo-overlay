using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace OsmoOverlay.Gui.Native;

/// <summary>
///     Wraps the Windows taskbar's own progress indicator (ITaskbarList3) so a long-running summary
///     read or render shows up on the taskbar icon even while the window is minimized or not focused.
///     No-ops on any failure (wrong Windows version, COM not available, etc.) - this is a cosmetic
///     touch, never worth failing a render over.
/// </summary>
internal static class TaskbarProgress
{
	public enum State
	{
		NoProgress,
		Indeterminate,
		Normal,
		Error,
		Paused
	}

	private static readonly ITaskbarList3? Instance = CreateInstance();

	public static void SetState(Window window, State state)
	{
		if (Instance is null || !OperatingSystem.IsWindows()) return;

		try
		{
			var hwnd = GetHwnd(window);
			if (hwnd == IntPtr.Zero) return;
			Instance.SetProgressState(hwnd, ToFlag(state));
		}
		catch (COMException)
		{
		}
	}

	public static void SetValue(Window window, ulong completed, ulong total)
	{
		if (Instance is null || !OperatingSystem.IsWindows()) return;

		try
		{
			var hwnd = GetHwnd(window);
			if (hwnd == IntPtr.Zero) return;
			Instance.SetProgressValue(hwnd, completed, total);
		}
		catch (COMException)
		{
		}
	}

	private static IntPtr GetHwnd(Window window)
	{
		return window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
	}

	private static TbpFlag ToFlag(State state)
	{
		return state switch
		{
			State.Indeterminate => TbpFlag.Indeterminate,
			State.Normal => TbpFlag.Normal,
			State.Error => TbpFlag.Error,
			State.Paused => TbpFlag.Paused,
			_ => TbpFlag.NoProgress
		};
	}

	private static ITaskbarList3? CreateInstance()
	{
		if (!OperatingSystem.IsWindows()) return null;

		try
		{
			var taskbarList = (ITaskbarList3)new TaskbarInstance();
			taskbarList.HrInit();
			return taskbarList;
		}
		catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
		{
			return null;
		}
	}

	[Flags]
	private enum TbpFlag
	{
		NoProgress = 0,
		Indeterminate = 0x1,
		Normal = 0x2,
		Error = 0x4,
		Paused = 0x8
	}

	// CLSID of the shell's TaskbarList coclass.
	[ComImport]
	[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
	private class TaskbarInstance;

	// Only the ITaskbarList/ITaskbarList2/ITaskbarList3 members up through SetProgressState are
	// declared - COM vtable order matters (this must match the real interface exactly, in order),
	// but any members after the last one this app actually calls can simply be omitted.
	[ComImport]
	[Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ITaskbarList3
	{
		// ITaskbarList
		void HrInit();
		void AddTab(IntPtr hwnd);
		void DeleteTab(IntPtr hwnd);
		void ActivateTab(IntPtr hwnd);
		void SetActiveAlt(IntPtr hwnd);

		// ITaskbarList2
		void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

		// ITaskbarList3
		void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
		void SetProgressState(IntPtr hwnd, TbpFlag tbpFlags);
	}
}
