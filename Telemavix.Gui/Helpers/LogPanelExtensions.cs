using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Telemavix.Core.Logging;

namespace Telemavix.Gui;

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
	Debug,
	Info,
	Warn,
	Error
}

internal static class LogPanelExtensions
{
	private static readonly ConditionalWeakTable<SelectableTextBlock, LogState> States = new();
	private static readonly IBrush WarnBrush = Palette.Warning;
	private static readonly IBrush ErrorBrush = Palette.Danger;
	private static readonly IBrush DebugBrush = Palette.TextMuted;

	private sealed record Entry(Run Run, string? Details, string? Key)
	{
		public int Start { get; set; }
	}

	private sealed class LogState
	{
		public List<Entry> Entries { get; } = [];
		public Dictionary<string, int> Statuses { get; } = [];
		public int Length { get; set; }
	}

	private static LogState CreateState(SelectableTextBlock logBox)
	{
		var state = new LogState();
		logBox.PointerMoved += (_, e) =>
		{
			Point point = e.GetPosition(logBox);
			int position = logBox.TextLayout.HitTestPoint(new Point(point.X - logBox.Padding.Left, point.Y - logBox.Padding.Top)).TextPosition;
			int low = 0, high = state.Entries.Count - 1;
			string? details = null;
			while (low <= high)
			{
				int mid = low + (high - low) / 2;
				Entry entry = state.Entries[mid];
				if (position < entry.Start) high = mid - 1;
				else if (position >= entry.Start + (entry.Run.Text?.Length ?? 0)) low = mid + 1;
				else
				{
					details = entry.Details;
					break;
				}
			}
			if (!Equals(ToolTip.GetTip(logBox), details)) ToolTip.SetTip(logBox, details);
		};
		logBox.PointerExited += (_, _) => ToolTip.SetTip(logBox, null);
		return state;
	}

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
			AppLogger.FileOnly(message);
		}

		/// <summary>
		///     Like AppendLog, but skips the AppLogger.Info call - for a message that's already been logged
		///     elsewhere (e.g. AppLogger.Notified, raised by AppLogger.Notify after it already logged the
		///     message itself), so mirroring it into the GUI doesn't also duplicate the file log entry.
		/// </summary>
		public void AppendLogLine(ScrollViewer logScroll, string message, LogLevel level = LogLevel.Info, string? details = null, string? statusKey = null)
		{
			LogState state = States.GetValue(logBox, CreateState);
			string text = $"[{DateTime.Now:HH:mm:ss}] {message}";
			if (statusKey is not null && state.Statuses.TryGetValue(statusKey, out int index))
			{
				Entry entry = state.Entries[index];
				if (index == state.Entries.Count - 1)
				{
					int difference = text.Length - (entry.Run.Text?.Length ?? 0);
					entry.Run.Text = text;
					state.Length += difference;
					logScroll.ScrollToEnd();
					return;
				}

				// Other lines came after it: the status moves to the end, so its updates stay in view instead of changing out of sight.
				int removed = (entry.Run.Text?.Length ?? 0) + Environment.NewLine.Length;
				InlineCollection existing = logBox.Inlines!;
				existing.RemoveAt(index * 2);
				existing.RemoveAt(index * 2);
				state.Entries.RemoveAt(index);
				state.Statuses.Remove(statusKey);
				state.Length -= removed;
				for (int i = index; i < state.Entries.Count; i++) state.Entries[i].Start -= removed;
				foreach (string key in state.Statuses.Where(s => s.Value > index).Select(s => s.Key).ToList()) state.Statuses[key]--;
			}
			var run = new Run(text);

			// Only set Foreground for Warn/Error - leaving it untouched for Info means it stays
			// unset (not a local value), so it properly inherits the theme's text color once added
			// to the tree below. Reading run.Foreground here to resolve it eagerly, before the Run is
			// parented, would instead capture TextElement.Foreground's hard default (Brushes.Black)
			// and lock it in as an explicit local value - invisible text on a near-black panel.
			if (level == LogLevel.Warn) run.Foreground = WarnBrush;
			else if (level == LogLevel.Error) run.Foreground = ErrorBrush;
			else if (level == LogLevel.Debug) run.Foreground = DebugBrush;

			InlineCollection inlines = logBox.Inlines ??= [];
			inlines.Add(run);
			inlines.Add(new LineBreak());
			if (statusKey is not null) state.Statuses[statusKey] = state.Entries.Count;
			state.Entries.Add(new Entry(run, details, statusKey) { Start = state.Length });
			state.Length += text.Length + Environment.NewLine.Length;

			logScroll.ScrollToEnd();
		}

		/// <summary>Clears the panel back to empty.</summary>
		public void ClearLog()
		{
			logBox.Inlines?.Clear();
			if (States.TryGetValue(logBox, out LogState? state))
			{
				state.Entries.Clear();
				state.Statuses.Clear();
				state.Length = 0;
			}
			ToolTip.SetTip(logBox, null);
		}

		public void CopyLogFrom(SelectableTextBlock source)
		{
			logBox.ClearLog();
			if (!States.TryGetValue(source, out LogState? original)) return;
			LogState copy = States.GetValue(logBox, CreateState);
			InlineCollection inlines = logBox.Inlines ??= [];
			foreach (Entry entry in original.Entries)
			{
				var run = new Run(entry.Run.Text) { Foreground = entry.Run.Foreground };
				if (entry.Key is not null) copy.Statuses[entry.Key] = copy.Entries.Count;
				copy.Entries.Add(new Entry(run, entry.Details, entry.Key) { Start = entry.Start });
				inlines.Add(run);
				inlines.Add(new LineBreak());
			}
			copy.Length = original.Length;
		}
	}
}
