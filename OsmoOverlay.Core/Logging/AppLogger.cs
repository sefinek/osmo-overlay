using NLog;

namespace OsmoOverlay.Core.Logging;

/// <summary>
///     Single shared logger for the whole app (GUI + CLI). Targets/levels come from NLog.config next
///     to each executable (NLog auto-loads it from the app's base directory), not from code here, so
///     logging can be retuned without a rebuild.
/// </summary>
public static class AppLogger
{
	private static readonly Logger Logger = LogManager.GetLogger("OsmoOverlay");

	/// <summary>
	///     Raised by Notify (external commands via ProcessHelper.CreateHidden, and one-off status lines
	///     like "Cleared N cached files") - lets the GUI mirror these into its own on-screen log panel
	///     instead of a separate ad-hoc status label per window, on top of the file log every call
	///     already reaches. Core has no GUI dependency, so this event (not a direct call into a GUI
	///     type) is how that crosses the boundary; a subscriber is called on whatever thread raised it,
	///     so it must marshal to the UI thread itself.
	/// </summary>
	public static event Action<string>? Notified;

	public static void Info(string message)
	{
		Logger.Info(message);
	}

	/// <summary>Like Info, but also raises Notified - use for a line the GUI should surface live (an executed command, a one-off status), not routine app info.</summary>
	public static void Notify(string message)
	{
		Logger.Info(message);
		Notified?.Invoke(message);
	}

	public static void Warn(string message)
	{
		Logger.Warn(message);
	}

	public static void Warn(Exception ex, string message)
	{
		Logger.Warn(ex, message);
	}

	public static void Error(Exception ex, string message)
	{
		Logger.Error(ex, message);
	}
}
