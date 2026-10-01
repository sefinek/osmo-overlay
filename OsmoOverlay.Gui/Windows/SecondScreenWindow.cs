using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace OsmoOverlay.Gui;

/// <summary>
///     A full screen window on another monitor: the log while nothing plays, the preview's viewport (the same controls the
///     main window owns, moved here and back - MainWindow.SecondScreen.cs) while it does. It has no behavior of its own;
///     the main window feeds it the log lines and handles its keys, and Esc/F9 only ask for it to be closed.
/// </summary>
internal sealed class SecondScreenWindow : Window
{
	private readonly SelectableTextBlock _log;
	private readonly ScrollViewer _logScroll;
	private readonly ContentControl _viewportHost = new();

	public SecondScreenWindow(Screen screen)
	{
		Title = "OsmoOverlay second screen";
		Background = Brushes.Black;
		WindowDecorations = WindowDecorations.None;

		_log = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 20, Margin = new Thickness(48) };
		_logScroll = new ScrollViewer { Content = _log };
		Content = new Grid { Children = { _logScroll, _viewportHost } };

		WindowStartupLocation = WindowStartupLocation.Manual;
		Position = screen.Bounds.Position;
		Width = screen.Bounds.Width / screen.Scaling;
		Height = screen.Bounds.Height / screen.Scaling;
		WindowState = WindowState.FullScreen;
		Opened += (_, _) =>
		{
			if (WindowState != WindowState.FullScreen) WindowState = WindowState.FullScreen;
		};
	}

	/// <summary>Esc or F9 here - the main window closes it, and remembers it's off.</summary>
	public event Action? CloseRequested;

	public bool ShowsViewport => _viewportHost.Content is not null;

	public void ShowLog()
	{
		_viewportHost.Content = null;
		_logScroll.IsVisible = true;
		_logScroll.ScrollToEnd();
	}

	public void ShowViewport(Control viewport)
	{
		_logScroll.IsVisible = false;
		_viewportHost.Content = viewport;
	}

	public Control? TakeViewport()
	{
		var viewport = _viewportHost.Content as Control;
		_viewportHost.Content = null;
		return viewport;
	}

	public void AppendLog(string message, LogLevel level)
	{
		_log.AppendLogLine(_logScroll, message, level);
	}

	public void ClearLog()
	{
		_log.ClearLog();
	}

	/// <summary>The lines the main window's LOG panel already has - a copy, as an inline belongs to one text block.</summary>
	public void CopyLogFrom(InlineCollection? source)
	{
		if (source is null) return;

		InlineCollection inlines = _log.Inlines ??= [];
		foreach (Inline inline in source)
		{
			if (inline is Run run) inlines.Add(new Run(run.Text) { Foreground = run.Foreground });
			else if (inline is LineBreak) inlines.Add(new LineBreak());
		}

		_logScroll.ScrollToEnd();
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Escape or Key.F9)) return;

		CloseRequested?.Invoke();
		e.Handled = true;
	}
}
