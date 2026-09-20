using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Gui;

/// <summary>
///     GUI-only presentation concept, not a general logging severity - Core's AppLogger stays
///     string-only (see its own "no GUI dependency" note) since it's shared with the CLI, which has no
///     panel to color. Call sites this window itself writes (RunGetSummaryAsync etc.) set this
///     explicitly; lines mirrored from AppLogger.Notified carry their own AppLogLevel (mapped to this
///     enum in MainWindow's subscriber), while PreviewPlayer.Message has no severity and always renders
///     as Info.
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

	extension(SelectableTextBlock logBox)
	{
		/// <summary>
		///     Single choke point for every on-screen log line (MainWindow and DependencyPromptWindow both
		///     go through this) - timestamps the line for the log box and mirrors it to AppLogger, so a run
		///     stays inspectable in the file log after the window that showed it has moved on or closed.
		/// </summary>
		public void AppendLog(ScrollViewer logScroll, string message, LogLevel level = LogLevel.Info)
		{
			logBox.AppendLogLine(logScroll, message, level);
			AppLogger.Info(message);
		}

		/// <summary>
		///     Like AppendLog, but skips the AppLogger.Info call - for a message that's already been logged
		///     elsewhere (e.g. AppLogger.Notified, raised by AppLogger.Notify after it already logged the
		///     message itself), so mirroring it into the GUI doesn't also duplicate the file log entry.
		/// </summary>
		public void AppendLogLine(ScrollViewer logScroll, string message, LogLevel level = LogLevel.Info)
		{
			var run = new Run($"[{DateTime.Now:HH:mm:ss}] {message}");

			// Only set Foreground for Warn/Error - leaving it untouched for Info means it stays
			// unset (not a local value), so it properly inherits the theme's text color once added
			// to the tree below. Reading run.Foreground here to resolve it eagerly, before the Run is
			// parented, would instead capture TextElement.Foreground's hard default (Brushes.Black)
			// and lock it in as an explicit local value - invisible text on a near-black panel.
			if (level == LogLevel.Warn) run.Foreground = WarnBrush;
			else if (level == LogLevel.Error) run.Foreground = ErrorBrush;

			InlineCollection inlines = logBox.Inlines ??= [];
			inlines.Add(run);
			inlines.Add(new LineBreak());

			logScroll.ScrollToEnd();
		}

		/// <summary>Clears the panel back to empty - same effect the old plain-TextBox's `LogBox.Text = ""` had.</summary>
		public void ClearLog()
		{
			logBox.Inlines?.Clear();
		}
	}

}
