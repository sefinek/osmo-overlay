using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace OsmoOverlay.Gui;

/// <summary>
///     Holds the preview's viewport (MainWindow.PreviewViewport) full screen on one monitor while it's open - the same
///     controls, moved here and back, so the one decoder and the one frame source keep feeding the one video view. Esc or
///     F11 closes it; the other keys and the clicks are the main window's (MainWindow.PreviewFullscreen.cs).
/// </summary>
internal sealed class PreviewFullscreenWindow : Window
{
	public PreviewFullscreenWindow(Control viewport, Screen? screen)
	{
		Title = "OsmoOverlay preview";
		Background = Brushes.Black;
		WindowDecorations = WindowDecorations.None;
		Content = viewport;

		if (screen is not null)
		{
			// The window goes full screen on the monitor it's placed on.
			WindowStartupLocation = WindowStartupLocation.Manual;
			Position = screen.Bounds.Position;
			Width = screen.Bounds.Width / screen.Scaling;
			Height = screen.Bounds.Height / screen.Scaling;
		}

		WindowState = WindowState.FullScreen;
		Opened += (_, _) =>
		{
			if (WindowState != WindowState.FullScreen) WindowState = WindowState.FullScreen;
			Activate();
		};
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Escape or Key.F11)) return;

		Close();
		e.Handled = true;
	}
}
