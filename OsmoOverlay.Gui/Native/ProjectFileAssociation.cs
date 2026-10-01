using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OsmoOverlay.Core;

namespace OsmoOverlay.Gui.Native;

/// <summary>
///     Makes Windows open .ovproj files with this app: the extension and its ProgID under HKCU\Software\Classes, no admin
///     rights needed. The installer writes the same keys (ProgId and the command line must match OsmoOverlay.iss). Does
///     nothing off Windows, or when the app runs through the dotnet muxer (no executable of its own to point at).
/// </summary>
internal static class ProjectFileAssociation
{
	public const string ProgId = "OsmoOverlay.Project";

	private const string ClassesKey = @"Software\Classes";

	private static string ExtensionKey => $@"{ClassesKey}\{OverlayProject.Extension}";

	private static string ProgIdKey => $@"{ClassesKey}\{ProgId}";

	public static bool IsSupported => OperatingSystem.IsWindows() && CurrentExecutable() is not null;

	/// <summary>True when .ovproj opens with this very executable - a registration left by another copy of the app counts as not registered.</summary>
	public static bool IsRegistered()
	{
		if (!OperatingSystem.IsWindows() || CurrentExecutable() is not { } exe) return false;

		using RegistryKey? extension = Registry.CurrentUser.OpenSubKey(ExtensionKey);
		using RegistryKey? command = Registry.CurrentUser.OpenSubKey($@"{ProgIdKey}\shell\open\command");
		return extension?.GetValue(null) as string == ProgId &&
		       string.Equals(command?.GetValue(null) as string, OpenCommand(exe), StringComparison.OrdinalIgnoreCase);
	}

	[SupportedOSPlatform("windows")]
	private static bool IsRegisteredAtAll()
	{
		using RegistryKey? extension = Registry.CurrentUser.OpenSubKey(ExtensionKey);
		return extension?.GetValue(null) as string == ProgId;
	}

	public static void Register()
	{
		if (!OperatingSystem.IsWindows() || CurrentExecutable() is not { } exe) return;

		Registry.CurrentUser.CreateSubKey(ExtensionKey).Dispose();
		SetDefault(ExtensionKey, ProgId);
		SetDefault(ProgIdKey, "OsmoOverlay project");
		SetDefault($@"{ProgIdKey}\DefaultIcon", $"\"{exe}\",0");
		SetDefault($@"{ProgIdKey}\shell\open\command", OpenCommand(exe));
		NotifyShell();
	}

	public static void Unregister()
	{
		if (!OperatingSystem.IsWindows()) return;
		if (!IsRegistered() && IsRegisteredAtAll()) return;

		Registry.CurrentUser.DeleteSubKeyTree(ExtensionKey, false);
		Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, false);
		NotifyShell();
	}

	private static string? CurrentExecutable()
	{
		(string path, string[] args) = AppCommand.Current();
		return args.Length == 0 ? path : null;
	}

	private static string OpenCommand(string exe)
	{
		return $"\"{exe}\" \"%1\"";
	}

	[SupportedOSPlatform("windows")]
	private static void SetDefault(string key, string value)
	{
		using RegistryKey created = Registry.CurrentUser.CreateSubKey(key);
		created.SetValue(null, value);
	}

	private static void NotifyShell()
	{
		const int assocChanged = 0x08000000;
		try
		{
			SHChangeNotify(assocChanged, 0, IntPtr.Zero, IntPtr.Zero);
		}
		catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
		{
		}
	}

	[DllImport("shell32.dll")]
	private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
