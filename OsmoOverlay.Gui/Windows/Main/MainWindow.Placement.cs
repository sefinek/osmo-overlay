using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using OsmoOverlay.Core.Overlay;

namespace OsmoOverlay.Gui;

/// <summary>
///     Brings the window back where it was closed (OverlaySettings.MainWindowPlacement), unless Settings says to always
///     start maximized. A maximized window doesn't report the bounds it restores to, so the normal bounds are tracked while
///     the window is in its normal state. They're read a dispatcher pass after a change: maximizing moves and resizes the
///     window before its WindowState says so, and reading right away would take the maximized bounds as normal ones.
/// </summary>
public partial class MainWindow
{
	// At least this much of the saved title bar area has to be on a screen, otherwise the window is centered instead.
	private const int MinVisibleWidth = 120;
	private const int MinVisibleHeight = 32;

	private PixelPoint? _normalPosition;
	private Size? _normalSize;
	private bool _maximized = true;
	private bool _placementUpdateQueued;
	private bool _placementFinal;

	private void RestorePlacement(OverlaySettings settings)
	{
		PositionChanged += (_, _) => QueuePlacementUpdate();
		PropertyChanged += (_, e) =>
		{
			if (e.Property == ClientSizeProperty || e.Property == WindowStateProperty) QueuePlacementUpdate();
		};

		if (settings.MainWindowPlacement is not { } saved) return;

		// Kept even when not restored, so turning the setting back on later still has them.
		if (saved.Width > 0 && saved.Height > 0 && double.IsFinite(saved.Width) && double.IsFinite(saved.Height))
			_normalSize = new Size(saved.Width, saved.Height);
		if (saved is { X: { } x, Y: { } y }) _normalPosition = new PixelPoint(x, y);

		if (!settings.RestoreWindowPlacement) return;

		if (_normalSize is { } size)
		{
			// Layout units without the interface scale - UiScale scales them when the window opens.
			Width = size.Width;
			Height = size.Height;
		}

		if (_normalPosition is { } position && _normalSize is { } visibleSize && IsOnScreen(position, visibleSize))
		{
			WindowStartupLocation = WindowStartupLocation.Manual;
			Position = position;
		}

		// Set after the position, so a maximized window comes back on the screen it was on.
		WindowState = saved.Maximized ? WindowState.Maximized : WindowState.Normal;
	}

	private bool IsOnScreen(PixelPoint position, Size size)
	{
		foreach (Screen screen in Screens.All)
		{
			var titleBar = new PixelRect(position, new PixelSize((int)(size.Width * UiScale.Factor * screen.Scaling), MinVisibleHeight));
			PixelRect visible = titleBar.Intersect(screen.WorkingArea);
			if (visible.Width >= MinVisibleWidth && visible.Height >= MinVisibleHeight) return true;
		}

		return false;
	}

	private void QueuePlacementUpdate()
	{
		if (_placementUpdateQueued) return;

		_placementUpdateQueued = true;
		Dispatcher.UIThread.Post(UpdatePlacement, DispatcherPriority.Background);
	}

	private void UpdatePlacement()
	{
		_placementUpdateQueued = false;
		if (_placementFinal) return;

		switch (WindowState)
		{
			case WindowState.Normal:
				_maximized = false;
				_normalPosition = Position;
				_normalSize = new Size(ClientSize.Width / UiScale.Factor, ClientSize.Height / UiScale.Factor);
				break;
			case WindowState.Maximized or WindowState.FullScreen:
				_maximized = true;
				break;
		}
	}

	/// <summary>The last reading while the native window still exists - once closed, Position reads (0, 0).</summary>
	protected override void OnClosing(WindowClosingEventArgs e)
	{
		base.OnClosing(e);
		if (e.Cancel) return;

		UpdatePlacement();
		_placementFinal = true;
	}

	/// <summary>Saved even with the setting off - it only decides whether the placement is used at startup.</summary>
	private void SavePlacement()
	{
		var placement = new WindowPlacement(_normalPosition?.X, _normalPosition?.Y,
			_normalSize?.Width ?? 0, _normalSize?.Height ?? 0, _maximized);
		OverlaySettingsStore.Save(OverlaySettingsStore.Load() with { MainWindowPlacement = placement });
	}
}
