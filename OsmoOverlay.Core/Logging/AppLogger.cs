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

	public static void Info(string message)
	{
		Logger.Info(message);
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
