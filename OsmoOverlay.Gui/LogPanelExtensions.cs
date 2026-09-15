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
		logBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
		logScroll.ScrollToEnd();
		AppLogger.Info(message);
	}
}
