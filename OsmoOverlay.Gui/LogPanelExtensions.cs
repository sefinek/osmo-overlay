using Avalonia.Controls;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

internal static class LogPanelExtensions
{
	/// <summary>
	///     Single choke point for every on-screen log line (MainWindow and DependencyPromptWindow both
	///     go through this) - timestamps the line for the log box and mirrors it to AppLogger, so a run
	///     stays inspectable in the file log after the window that showed it has moved on or closed.
	/// </summary>
	public static void AppendLog(this TextBox logBox, ScrollViewer logScroll, string message)
	{
		logBox.AppendLogLine(logScroll, message);
		AppLogger.Info(message);
	}

	/// <summary>
	///     Like AppendLog, but skips the AppLogger.Info call - for a message that's already been logged
	///     elsewhere (e.g. AppLogger.Notified, raised by AppLogger.Notify after it already logged the
	///     message itself), so mirroring it into the GUI doesn't also duplicate the file log entry.
	/// </summary>
	public static void AppendLogLine(this TextBox logBox, ScrollViewer logScroll, string message)
	{
		logBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
		logScroll.ScrollToEnd();
	}
}
