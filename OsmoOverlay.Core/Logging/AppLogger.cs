using System.Text.RegularExpressions;
using NLog;

namespace OsmoOverlay.Core.Logging;

/// <summary>GUI-agnostic severity for Notified - kept here (not in the GUI's own LogLevel) since Core has no GUI dependency to define it against.</summary>
public enum AppLogLevel
{
	Info,
	Warn,
	Error
}

/// <summary>
///     Single shared logger for the whole app (GUI + CLI). Targets/levels come from NLog.config next
///     to each executable (NLog auto-loads it from the app's base directory), not from code here, so
///     logging can be retuned without a rebuild. The GUI writes app.log and the CLI cli.log - two
///     processes running at once must not share one file. The file gets the user's home folder as "~"
///     (Scrub): a log attached to a bug report shouldn't carry the user name and folder names - the
///     on-screen panel (Notified) keeps the full paths.
/// </summary>
public static class AppLogger
{
	private static readonly Logger Logger = LogManager.GetLogger("OsmoOverlay");

	// The home folder only as a whole path segment - "C:\Users\Ann" must not eat the start of "C:\Users\Anna".
	private static readonly Regex HomeFolder = BuildHomeFolderPattern();

	/// <summary>
	///     Raised by Notify/Warn/Error (not Info - see the comment on it below) - lets the GUI mirror
	///     these into its own on-screen log panel instead of a separate ad-hoc status label per window,
	///     on top of the file log every call already reaches. Core has no GUI dependency, so this event
	///     (not a direct call into a GUI type) is how that crosses the boundary; a subscriber is called
	///     on whatever thread raised it, so it must marshal to the UI thread itself.
	/// </summary>
	public static event Action<string, AppLogLevel>? Notified;

	// Deliberately doesn't raise Notified - most Info calls are routine, high-frequency internal
	// progress logging (ffprobe output, per-frame render status, etc.), and MainWindow's own render
	// flow already mirrors the handful of these worth surfacing live via its own direct AppendLog
	// calls. Raising Notified here too would double them up in the panel. Use Notify below for an
	// Info-level line that has no other route onto the screen yet.
	public static void Info(string message)
	{
		Logger.Info(Scrub(message));
	}

	/// <summary>Like Info, but also raises Notified - use for a line the GUI should surface live (an executed command, a one-off status) that isn't already reaching the screen some other way.</summary>
	public static void Notify(string message)
	{
		Logger.Info(Scrub(message));
		Notified?.Invoke(message, AppLogLevel.Info);
	}

	public static void Warn(string message)
	{
		Logger.Warn(Scrub(message));
		Notified?.Invoke(message, AppLogLevel.Warn);
	}

	public static void Warn(Exception ex, string message)
	{
		Logger.Warn(WithException(message, ex));
		Notified?.Invoke(message, AppLogLevel.Warn);
	}

	public static void Error(Exception ex, string message)
	{
		Logger.Error(WithException(message, ex));
		Notified?.Invoke(message, AppLogLevel.Error);
	}

	/// <summary>For a failure with no Exception object to attach (e.g. a subprocess's own non-zero exit code) - see the Exception overload above for the normal case.</summary>
	public static void Error(string message)
	{
		Logger.Error(Scrub(message));
		Notified?.Invoke(message, AppLogLevel.Error);
	}

	internal static string Scrub(string text)
	{
		return HomeFolder.Replace(text, "~");
	}

	/// <summary>
	///     The message with the exception under it, as NLog.config's layout writes an exception passed to NLog - formatted
	///     here because the exception's own text (a FileNotFoundException's message, say) carries paths too.
	/// </summary>
	private static string WithException(string message, Exception ex)
	{
		return Scrub($"{message}{Environment.NewLine}{ex}");
	}

	private static Regex BuildHomeFolderPattern()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
		// No home folder, or a drive or root as one, would match far more than the user's own paths.
		if (home.Length < 4) return new Regex("(?!)");

		RegexOptions options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
		return new Regex(Regex.Escape(home) + @"(?![\w.-])", options);
	}
}
