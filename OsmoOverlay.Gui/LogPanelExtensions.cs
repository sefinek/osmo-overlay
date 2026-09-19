using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

/// <summary>
///     GUI-only presentation concept, not a general logging severity - Core's AppLogger stays
///     string-only (see its own "no GUI dependency" note) since it's shared with the CLI, which has no
///     panel to color. Only call sites this window itself writes (RunGetSummaryAsync etc.) can set
///     this explicitly; lines mirrored from AppLogger.Notified or PreviewPlayer.Message are plain
///     strings with no severity attached, so they always render as Info.
/// </summary>
public enum LogLevel
{
	Info,
	Warn,
	Error
}

internal static class LogPanelExtensions
{
	private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#E5A83E"));
	private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E5484D"));

	/// <summary>
	///     Single choke point for every on-screen log line (MainWindow and DependencyPromptWindow both
	///     go through this) - timestamps the line for the log box and mirrors it to AppLogger, so a run
	///     stays inspectable in the file log after the window that showed it has moved on or closed.
	/// </summary>
	public static void AppendLog(this SelectableTextBlock logBox, ScrollViewer logScroll, string message, LogLevel level = LogLevel.Info)
	{
		logBox.AppendLogLine(logScroll, message, level);
		AppLogger.Info(message);
	}

	/// <summary>
	///     Like AppendLog, but skips the AppLogger.Info call - for a message that's already been logged
	///     elsewhere (e.g. AppLogger.Notified, raised by AppLogger.Notify after it already logged the
	///     message itself), so mirroring it into the GUI doesn't also duplicate the file log entry.
	/// </summary>
	public static void AppendLogLine(this SelectableTextBlock logBox, ScrollViewer logScroll, string message, LogLevel level = LogLevel.Info)
	{
		var run = new Run($"[{DateTime.Now:HH:mm:ss}] {message}");
		if (level == LogLevel.Warn) run.Foreground = WarnBrush;
		else if (level == LogLevel.Error) run.Foreground = ErrorBrush;

		InlineCollection inlines = logBox.Inlines ??= [];
		inlines.Add(run);
		inlines.Add(new LineBreak());

		logScroll.ScrollToEnd();
	}

	/// <summary>Clears the panel back to empty - same effect the old plain-TextBox's `LogBox.Text = ""` had.</summary>
	public static void ClearLog(this SelectableTextBlock logBox)
	{
		logBox.Inlines?.Clear();
	}
}
